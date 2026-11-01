using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Consumer;
using JellyShare.Jellyfin.Placeholders;
using JellyShare.Jellyfin.Protocol;
using JellyShare.JellyfinTests.Keys;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyShare.JellyfinTests.Consumer;

public sealed class SyncServiceTests : IDisposable
{
    private const string BunnyDir = "Big Buck Bunny (2008) [tmdbid-10378]";
    private const string SintelDir = "Sintel (2010) [tmdbid-45745]";
    private const string PioneerDir = "Pioneer One [tvdbid-153021]";

    private readonly TempDir _tmp = new();
    private readonly ConsumerConfig _config = new();
    private readonly FakePeerClient _client = new();
    private readonly FakeLocalLibrary _library = new();
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly FakeScans _scans = new();
    private readonly MutableClock _clock = new();
    private readonly RemotePeer _alice = new() { Id = TestItems.PeerA, Name = "alice", BaseUrl = "http://alice", Token = "t" };
    private readonly SyncService _sync;

    public SyncServiceTests()
    {
        _config.Current.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Current.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _config.Current.Peers.Add(_alice);
        var index = new LocalIndex(_library, _config, FakeLocalLibrary.Exists(p => !_missing.Contains(p)));
        _sync = new SyncService(
            _config, _client, index, _scans, new FixedRelay(), () => _tmp.Sub("data"), _clock, NullLogger<SyncService>.Instance);
    }

    private string MoviesRoot => _config.Current.MoviesPlaceholderPath;

    private string ShowsRoot => _config.Current.ShowsPlaceholderPath;

    private string DataDir => _tmp.Sub("data");

    public void Dispose() => _tmp.Dispose();

    private static CatalogItem Bunny() => TestItems.Movie("Big Buck Bunny", tmdb: "10378", id: "bunny") with { Year = 2008 };

    private static CatalogItem Sintel() => TestItems.Movie("Sintel", tmdb: "45745", id: "sintel") with { Year = 2010 };

    private static CatalogItem Pioneer(int s, int e) =>
        TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021" }, s, e, $"p{s}{e}");

    private void Offer(params CatalogItem[] items) => _client.Catalogs[_alice.Id] = _ => FakePeerClient.Ok(items);

    private bool MoviePlaceholder(string dir) => Directory.Exists(Path.Combine(MoviesRoot, dir));

    private void AddLocalSintel()
        => _library.Media.Add(new LocalMedia("Movie", "/media/Movies/Sintel.mkv", new Dictionary<string, string> { ["Tmdb"] = "45745" }, null, null, null));

    [Fact]
    public async Task Sync_LocalCopyPresent_NoPlaceholder()
    {
        AddLocalSintel();
        Offer(Bunny(), Sintel());

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.False(MoviePlaceholder(SintelDir));
        Assert.Equal(2, report.Offered);
        Assert.Equal(1, report.SkippedLocal);
        Assert.Equal(2, report.Written);
        Assert.Empty(report.PeerErrors);
        Assert.Equal(
            "http://127.0.0.1:8096/JellyShare/v1/play/movie%3Atmdb%3A10378",
            File.ReadAllText(Path.Combine(MoviesRoot, BunnyDir, "Big Buck Bunny (2008).strm")));
    }

    [Fact]
    public async Task Sync_LocalFileMissing_WritesPlaceholder()
    {
        AddLocalSintel();
        _missing.Add("/media/Movies/Sintel.mkv");
        Offer(Sintel());

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(SintelDir));
        Assert.Equal(0, report.SkippedLocal);
    }

    [Fact]
    public async Task SyncPlanner_PeerUnreachable_KeepsItsPlaceholders()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("connection failed");
        _clock.Now += TimeSpan.FromDays(3);
        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.Equal(0, report.Deleted);
        Assert.Equal(["alice: connection failed"], report.PeerErrors);

        _clock.Now += TimeSpan.FromDays(3);
        report = await _sync.RunAsync(CancellationToken.None);
        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.Equal(0, report.Deleted);
    }

    [Fact]
    public async Task SyncPlanner_PeerUnreachable8Days_RemovesItsPlaceholders()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("timeout");
        _clock.Now += TimeSpan.FromDays(1);
        await _sync.RunAsync(CancellationToken.None);
        Assert.True(MoviePlaceholder(BunnyDir));

        _clock.Now += TimeSpan.FromDays(8);
        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.False(MoviePlaceholder(BunnyDir));
        Assert.Equal(2, report.Deleted);
        Assert.Equal(["alice: timeout"], report.PeerErrors);
        var state = new PeerStateStore(Path.Combine(DataDir, "peer-state.json")).Load()[_alice.Id];
        Assert.Equal(_clock.Now - TimeSpan.FromDays(8), state.FailingSinceUtc);
        Assert.Equal("timeout", state.LastError);
    }

    [Fact]
    public async Task Sync_IncompatibleProtocol_ReportsErrorAndKeepsLastGood()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("incompatible protocol 2.0");
        _clock.Now += TimeSpan.FromHours(6);
        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(["alice: incompatible protocol 2.0"], report.PeerErrors);
        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.Equal(0, report.Deleted);
        Assert.Equal(1, report.Offered);
    }

    [Fact]
    public async Task Sync_NotModified_ReusesLastGoodAndRefreshesLastSuccess()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _client.Catalogs[_alice.Id] = etag => etag == "etag-1"
            ? new PeerFetchResult(false, true, null, null)
            : FakePeerClient.Fail("unexpected etag " + etag);
        _clock.Now += TimeSpan.FromHours(6);
        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal([(_alice.Id, (string?)null), (_alice.Id, "etag-1")], _client.Fetches);
        Assert.Empty(report.PeerErrors);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(1, report.Offered);
        Assert.True(MoviePlaceholder(BunnyDir));
        var state = new PeerStateStore(Path.Combine(DataDir, "peer-state.json")).Load()[_alice.Id];
        Assert.Equal(_clock.Now, state.LastSuccessUtc);
        Assert.Null(state.FailingSinceUtc);
    }

    [Fact]
    public async Task Sync_RemovedPeer_ContributesNothingAndStateIsDropped()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _config.Current.Peers.Clear();
        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.False(MoviePlaceholder(BunnyDir));
        Assert.Equal(2, report.Deleted);
        Assert.Empty(new PeerStateStore(Path.Combine(DataDir, "peer-state.json")).Load());
        Assert.True(File.Exists(Path.Combine(MoviesRoot, PlaceholderWriter.MarkerName)));
    }

    [Fact]
    public async Task Sync_DisabledPeer_ContributesNothing()
    {
        Offer(Bunny());
        _alice.Enabled = false;

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Empty(_client.Fetches);
        Assert.Equal(0, report.Offered);
        Assert.False(MoviePlaceholder(BunnyDir));
    }

    [Fact]
    public async Task Sync_SecondRun_NoWritesNoScans()
    {
        Offer(Bunny(), Pioneer(1, 1), Pioneer(2, 1));
        var first = await _sync.RunAsync(CancellationToken.None);
        Assert.True(first.Written > 0);
        Assert.NotEmpty(_scans.Reported);

        _scans.Reported.Clear();
        var second = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(0, second.Written);
        Assert.Equal(0, second.Deleted);
        Assert.Equal(0, second.Scanned);
        Assert.Empty(_scans.Reported);
    }

    [Fact]
    public async Task Sync_ScansEachTouchedDirectoryOnce()
    {
        Offer(Bunny(), Pioneer(2, 1), Pioneer(2, 2));
        var report = await _sync.RunAsync(CancellationToken.None);

        var expected = new[]
        {
            Path.Combine(MoviesRoot, BunnyDir),
            Path.Combine(ShowsRoot, PioneerDir),
            Path.Combine(ShowsRoot, PioneerDir, "Season 02"),
        }.Select(Path.GetFullPath).Order(StringComparer.Ordinal);
        Assert.Equal(expected, _scans.Reported.Order(StringComparer.Ordinal));
        Assert.Equal(3, report.Scanned);
    }

    [Fact]
    public async Task Sync_ScanBatching_Max200PerRun_RestNextRun()
    {
        Offer(Enumerable.Range(1, 250).Select(i => TestItems.Movie("Movie " + i, tmdb: i.ToString(), id: "m" + i)).ToArray());

        var first = await _sync.RunAsync(CancellationToken.None);
        Assert.Equal(200, _scans.Reported.Count);
        Assert.Equal(200, first.Scanned);
        Assert.Equal(50, first.PendingScans);

        var reportedFirst = _scans.Reported.ToList();
        _scans.Reported.Clear();
        var second = await _sync.RunAsync(CancellationToken.None);
        Assert.Equal(0, second.Written);
        Assert.Equal(50, _scans.Reported.Count);
        Assert.Empty(reportedFirst.Intersect(_scans.Reported));
        Assert.Equal(250, reportedFirst.Concat(_scans.Reported).Distinct().Count());
        Assert.Equal(0, second.PendingScans);

        _scans.Reported.Clear();
        await _sync.RunAsync(CancellationToken.None);
        Assert.Empty(_scans.Reported);
    }

    [Fact]
    public async Task Sync_PersistsMergedIndex_RoundTrip()
    {
        var bunny = Bunny() with { Media = new MediaInfo("mkv", "h264", 1920, 1080, 1312543, [], []) };
        Offer(bunny, Pioneer(1, 1));

        await _sync.RunAsync(CancellationToken.None);

        var file = MergedIndexStore.Load(Path.Combine(DataDir, "merged-index.json"));
        Assert.NotNull(file);
        Assert.Equal(_clock.Now, file.GeneratedUtc);
        var entries = file.ToEntries();
        Assert.Equal(["ep:tvdb:153021:s1e1", "movie:tmdb:10378"], entries.Select(e => e.CanonicalKey));
        var movie = entries[1];
        Assert.Equal(["movie:tmdb:10378"], movie.Keys);
        Assert.Equal(_alice.Id, movie.Sources[0].PeerId);
        Assert.Equal("bunny", movie.Sources[0].Item.Id);
        Assert.Equal(1312543, movie.Sources[0].Item.Media.BitRate);
        Assert.Equal("Big Buck Bunny", movie.Sources[0].Item.Name);
        Assert.Equal("s1", entries[0].Sources[0].Item.SeriesId);
    }

    [Fact]
    public async Task Sync_SeriesPlaceholder_UsesLocalSeries_AndFetchesSeriesImages()
    {
        _library.Series.Add(new LocalSeriesItem("Pioneer One", "/media/Shows/Pioneer One", new Dictionary<string, string> { ["Tvdb"] = "153021" }));
        _library.Media.Add(new LocalMedia("Episode", "/media/Shows/Pioneer One/S01E01.mkv", null, new Dictionary<string, string> { ["Tvdb"] = "153021" }, 1, 1));
        Offer(Pioneer(1, 1), Pioneer(2, 1));
        _client.Images[(_alice.Id, "s1", "Primary")] = [7];

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(1, report.SkippedLocal);
        Assert.False(File.Exists(Path.Combine(ShowsRoot, PioneerDir, "Season 01", "Pioneer One S01E01.strm")));
        Assert.True(File.Exists(Path.Combine(ShowsRoot, PioneerDir, "Season 02", "Pioneer One S02E01.strm")));
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(ShowsRoot, PioneerDir, "poster.jpg")));
        Assert.Contains((_alice.Id, "s1", "Backdrop"), _client.ImageRequests);
    }

    [Fact]
    public async Task Sync_ReportsConflicts()
    {
        Offer(
            TestItems.Movie("A", tmdb: "1", imdb: "tt1", id: "a"),
            TestItems.Movie("B", tmdb: "2", imdb: "tt1", id: "b"),
            Bunny());

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(2, report.Conflicts);
        Assert.Equal(2, report.Unmatched);
        Assert.Equal(1, report.Offered);
        Assert.Equal(0, report.Skipped);
    }

    [Fact]
    public async Task Sync_UnreadablePeerState_AbortsWithoutDeleting()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        var statePath = Path.Combine(DataDir, "peer-state.json");
        File.WriteAllText(statePath, "{ truncated");
        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("connection failed");
        _clock.Now += TimeSpan.FromHours(6);

        await Assert.ThrowsAsync<PeerStateUnreadableException>(() => _sync.RunAsync(CancellationToken.None));

        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.Equal("{ truncated", File.ReadAllText(statePath));
    }

    [Fact]
    public async Task Sync_WriterFailure_KeepsChangedDirsQueued()
    {
        Offer(Bunny(), Pioneer(2, 1));
        Directory.CreateDirectory(ShowsRoot);
        File.WriteAllText(Path.Combine(ShowsRoot, PlaceholderWriter.MarkerName), string.Empty);
        var blocker = Path.Combine(ShowsRoot, PioneerDir);
        File.WriteAllText(blocker, "a file where the series folder should go");

        await Assert.ThrowsAnyAsync<Exception>(() => _sync.RunAsync(CancellationToken.None));
        Assert.True(MoviePlaceholder(BunnyDir));

        File.Delete(blocker);
        _scans.Reported.Clear();
        await _sync.RunAsync(CancellationToken.None);

        Assert.Contains(Path.GetFullPath(Path.Combine(MoviesRoot, BunnyDir)), _scans.Reported);
        Assert.Contains(Path.GetFullPath(Path.Combine(ShowsRoot, PioneerDir)), _scans.Reported);
    }

    [Fact]
    public async Task Sync_ScanReportFailure_RequeuesDir()
    {
        Offer(Bunny());
        var bunnyFull = Path.GetFullPath(Path.Combine(MoviesRoot, BunnyDir));
        _scans.FailFor = p => p == bunnyFull;

        var first = await _sync.RunAsync(CancellationToken.None);
        Assert.Empty(_scans.Reported);
        Assert.Equal(1, first.PendingScans);

        _scans.FailFor = null;
        var second = await _sync.RunAsync(CancellationToken.None);
        Assert.Equal([bunnyFull], _scans.Reported);
        Assert.Equal(0, second.PendingScans);
    }

    [Fact]
    public async Task Sync_OverlappingPlaceholderRoots_RefusesToWrite()
    {
        _config.Current.ShowsPlaceholderPath = Path.Combine(MoviesRoot, "Shows");
        Offer(Bunny(), Pioneer(2, 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        Assert.False(Directory.Exists(MoviesRoot));
    }

    [Fact]
    public async Task Sync_PeerFetchThrowsIOException_RecordsPeerErrorAndOthersStillSync()
    {
        var bob = new RemotePeer { Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob", Token = "t2", Priority = 1 };
        _config.Current.Peers.Add(bob);
        _client.Catalogs[_alice.Id] = _ => throw new IOException("response ended prematurely");
        _client.Catalogs[bob.Id] = _ => FakePeerClient.Ok(Bunny());

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.Single(report.PeerErrors);
        Assert.StartsWith("alice: connection failed", report.PeerErrors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_PeerFetchThrows_KeepsItsLastGoodCatalog()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        _client.Catalogs[_alice.Id] = _ => throw new HttpRequestException("reset");

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.Single(report.PeerErrors);
        Assert.Equal(0, report.Deleted);
    }

    [Fact]
    public async Task Sync_NonLoopbackRelayOverride_RefusesToWrite()
    {
        _config.Current.RelayBaseOverride = "http://10.0.0.2:8096";
        Offer(Bunny());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        Assert.Contains("RelayBaseOverride", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(MoviesRoot));
    }

    private string BunnyNfo => Path.Combine(MoviesRoot, BunnyDir, "Big Buck Bunny (2008).nfo");

    private string BunnyStrm => Path.ChangeExtension(BunnyNfo, ".strm");

    [Fact]
    public async Task Sync_JellyfinRewritesNfo_NoRewriteWhileDesiredContentUnchanged()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        Assert.True(File.Exists(BunnyNfo));
        const string jellyfin = "<?xml version=\"1.0\"?><movie><title>Big Buck Bunny</title><lockdata>false</lockdata></movie>";
        File.WriteAllText(BunnyNfo, jellyfin);
        _scans.Reported.Clear();

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(0, report.Written);
        Assert.Equal(0, report.Deleted);
        Assert.Empty(_scans.Reported);
        Assert.Equal(jellyfin, File.ReadAllText(BunnyNfo));
    }

    [Fact]
    public async Task Sync_JellyfinSeasonNfo_IsLeftAlone()
    {
        Offer(Pioneer(1, 1));
        await _sync.RunAsync(CancellationToken.None);
        var seasonDir = Directory.GetDirectories(Path.Combine(ShowsRoot, PioneerDir)).Single();
        var seasonNfo = Path.Combine(seasonDir, "season.nfo");
        File.WriteAllText(seasonNfo, "<season/>");
        File.WriteAllText(Path.Combine(ShowsRoot, PioneerDir, "tvshow.nfo"), "<tvshow>rewritten by jellyfin</tvshow>");
        _scans.Reported.Clear();

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(0, report.Written + report.Deleted);
        Assert.True(File.Exists(seasonNfo));
        Assert.Empty(_scans.Reported);
    }

    [Fact]
    public async Task Sync_DesiredNfoChanged_IsRewritten_EvenAfterJellyfinEdit()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        File.WriteAllText(BunnyNfo, "<movie>jellyfin</movie>");

        Offer(Bunny() with { Overview = "A new plot" });
        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(1, report.Written);
        Assert.Contains("A new plot", File.ReadAllText(BunnyNfo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_TitleGone_RemovesWholeFolder_IncludingJellyfinFiles()
    {
        Offer(Pioneer(1, 1));
        await _sync.RunAsync(CancellationToken.None);
        var series = Path.Combine(ShowsRoot, PioneerDir);
        File.WriteAllText(Path.Combine(Directory.GetDirectories(series).Single(), "season.nfo"), "<season/>");

        Offer();
        await _sync.RunAsync(CancellationToken.None);

        Assert.False(Directory.Exists(series));
    }

    [Fact]
    public async Task Sync_NoLedgerYet_SeedsFromDisk_NoRewrites_AndKeepsForeignNfo()
    {
        Offer(Pioneer(1, 1), Bunny());
        await _sync.RunAsync(CancellationToken.None);
        var seasonNfo = Path.Combine(Directory.GetDirectories(Path.Combine(ShowsRoot, PioneerDir)).Single(), "season.nfo");
        File.WriteAllText(seasonNfo, "<season/>");
        File.Delete(Path.Combine(DataDir, "placeholder-ledger.json"));
        _scans.Reported.Clear();

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(0, report.Written + report.Deleted);
        Assert.True(File.Exists(seasonNfo));
        Assert.True(File.Exists(Path.Combine(DataDir, "placeholder-ledger.json")));
    }

    [Fact]
    public async Task Sync_PlaceholderDeletedByHand_IsWrittenAgain()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        File.Delete(BunnyStrm);

        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(1, report.Written);
        Assert.True(File.Exists(BunnyStrm));
    }

    [Theory]
    [InlineData("ph")]                 // a library folder containing the placeholder roots
    [InlineData("ph/Movies/Owner")]    // a library folder inside a placeholder root
    public async Task Sync_RootOverlapsAnOwnerLibraryFolder_RefusesWithClearError(string location)
    {
        _library.Locations.Add(_tmp.Sub(location.Split('/')));
        Offer(Bunny());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        Assert.Contains("library folder", ex.Message, StringComparison.Ordinal);
        Assert.False(MoviePlaceholder(BunnyDir));
    }

    [Fact]
    public async Task Sync_RootIsAnOwnerMovieFolder_RefusesAndDeletesNothing()
    {
        var owner = _tmp.Sub("media", "Movies");
        Directory.CreateDirectory(Path.Combine(owner, "Owned (2001)"));
        var ownerNfo = Path.Combine(owner, "Owned (2001)", "Owned (2001).nfo");
        File.WriteAllText(ownerNfo, "<movie/>");
        _library.Locations.Add(owner);
        _config.Current.MoviesPlaceholderPath = owner;
        Offer(Bunny());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        Assert.True(File.Exists(ownerNfo));
        Assert.False(File.Exists(Path.Combine(owner, ".jellyshare")));
    }

    [Fact]
    public async Task Sync_AttachedPluginRoots_AreNotOverlaps()
    {
        _library.Locations.Add(MoviesRoot);
        _library.Locations.Add(ShowsRoot);
        _library.Locations.Add(_tmp.Sub("media", "Movies"));
        Offer(Bunny());

        await _sync.RunAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(BunnyDir));
    }

    [Fact]
    public async Task Sync_ExistingNonEmptyRootWithoutMarker_Refuses()
    {
        Directory.CreateDirectory(MoviesRoot);
        var foreign = Path.Combine(MoviesRoot, "someone else.nfo");
        File.WriteAllText(foreign, "x");
        Offer(Bunny());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        Assert.Contains(".jellyshare", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(Path.Combine(MoviesRoot, ".jellyshare")));
    }

    [Fact]
    public async Task Sync_ExistingEmptyRoot_IsAdopted()
    {
        Directory.CreateDirectory(MoviesRoot);
        Offer(Bunny());

        await _sync.RunAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(MoviesRoot, ".jellyshare")));
        Assert.True(MoviePlaceholder(BunnyDir));
    }

    [Fact]
    public async Task Reconcile_RootOverlapsLibraryFolder_Refuses()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        _library.Locations.Add(_tmp.Sub("ph"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.ReconcileAsync(["movie:tmdb:10378"], CancellationToken.None));
    }

    [Fact]
    public async Task Sync_ImagesAreFetchedAfterTheLock_PlaceholdersAndReconcileDoNotWait()
    {
        _client.Images[(_alice.Id, "bunny", "Primary")] = [1, 2, 3];
        var imageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseImages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.BeforeImage = async () =>
        {
            imageStarted.TrySetResult();
            await releaseImages.Task;
        };
        Offer(Bunny() with { ImageTypes = ["Primary"] }, Sintel());

        var run = _sync.RunAsync(CancellationToken.None);
        await imageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Placeholders are on disk and scanned before any image arrives, and the lock is free for a reconcile.
        Assert.True(File.Exists(BunnyStrm));
        Assert.Contains(Path.GetFullPath(Path.Combine(MoviesRoot, BunnyDir)), _scans.Reported);
        var reconcile = _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);
        Assert.Same(reconcile, await Task.WhenAny(reconcile, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.False(run.IsCompleted);

        releaseImages.SetResult();
        var report = await run;
        Assert.Equal(1, report.ImagesWritten);
        Assert.True(File.Exists(Path.Combine(MoviesRoot, BunnyDir, "poster.jpg")));
    }

    [Fact]
    public async Task Sync_ImageDownloads_RunInParallel_AtMostFour()
    {
        var items = Enumerable.Range(1, 12).Select(i => TestItems.Movie("M" + i, tmdb: (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), id: "m" + i) with { Year = 2000, ImageTypes = ["Primary", "Backdrop"] }).ToArray();
        _client.BeforeImage = () => Task.Delay(30);
        Offer(items);

        await _sync.RunAsync(CancellationToken.None);

        Assert.InRange(_client.MaxConcurrentImages, 2, 4);
    }

    [Fact]
    public async Task Sync_MissingImage_IsRetriedOnALaterSync()
    {
        Offer(Bunny() with { ImageTypes = ["Primary"] });
        await _sync.RunAsync(CancellationToken.None);
        var poster = Path.Combine(MoviesRoot, BunnyDir, "poster.jpg");
        Assert.False(File.Exists(poster));

        _client.Images[(_alice.Id, "bunny", "Primary")] = [9];
        var report = await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(0, report.Written);
        Assert.Equal(1, report.ImagesWritten);
        Assert.True(File.Exists(poster));
    }

    [Fact]
    public async Task Sync_ImageRetries_AreBoundedPerRun()
    {
        var items = Enumerable.Range(1, SyncService.MaxImageRetriesPerRun + 10)
            .Select(i => TestItems.Movie("M" + i, tmdb: (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), id: "m" + i) with { Year = 2000, ImageTypes = ["Primary", "Backdrop"] })
            .ToArray();
        Offer(items);
        await _sync.RunAsync(CancellationToken.None);
        lock (_client.ImageRequests)
        {
            _client.ImageRequests.Clear();
        }

        await _sync.RunAsync(CancellationToken.None);

        var folders = _client.ImageRequests.Select(r => r.Item).Distinct().Count();
        Assert.Equal(SyncService.MaxImageRetriesPerRun, folders);
    }

    [Fact]
    public async Task Sync_TryStartBackground_StartsOnce_ReportsRunning_ThenLastReport()
    {
        Offer(Bunny());
        var release = new TaskCompletionSource();
        _client.BeforeFetch = () => release.Task;

        Assert.True(_sync.TryStartBackground());
        await WaitUntil(() => _sync.IsRunning);
        Assert.False(_sync.TryStartBackground());

        release.SetResult();
        await WaitUntil(() => !_sync.IsRunning);
        Assert.NotNull(_sync.LastReport);
        Assert.Null(_sync.LastError);
        Assert.True(MoviePlaceholder(BunnyDir));
    }

    [Fact]
    public async Task Sync_BackgroundFailure_IsRecordedAsLastError_AndClearedBySuccess()
    {
        _config.Current.RelayBaseOverride = "http://10.0.0.2:8096";
        Offer(Bunny());

        Assert.True(_sync.TryStartBackground());
        await WaitUntil(() => !_sync.IsRunning && _sync.LastError is not null);
        Assert.Contains("RelayBaseOverride", _sync.LastError, StringComparison.Ordinal);

        _config.Current.RelayBaseOverride = null;
        await _sync.RunAsync(CancellationToken.None);
        Assert.Null(_sync.LastError);
    }

    [Fact]
    public async Task Sync_QueuedBackgroundRun_RunsAgainAfterTheCurrentOne()
    {
        Offer(Bunny());
        var release = new TaskCompletionSource();
        _client.BeforeFetch = () => release.Task;
        var first = _sync.RunAsync(CancellationToken.None);
        await WaitUntil(() => _sync.IsRunning);

        Assert.False(_sync.TryStartBackground(queueIfRunning: true));
        release.SetResult();
        await first;

        await WaitUntil(() => _client.Fetches.Count >= 2 && !_sync.IsRunning);
        Assert.Equal(2, _client.Fetches.Count);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Sync_OverlappingRuns_AreSerialized()
    {
        Offer(Bunny());
        var release = new TaskCompletionSource();
        var inside = 0;
        var maxInside = 0;
        _client.BeforeFetch = async () =>
        {
            maxInside = Math.Max(maxInside, Interlocked.Increment(ref inside));
            await release.Task;
            Interlocked.Decrement(ref inside);
        };

        var a = _sync.RunAsync(CancellationToken.None);
        var b = _sync.RunAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(1, _client.Entered);
        release.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(1, maxInside);
        Assert.Equal(2, _client.Fetches.Count);
    }
}
