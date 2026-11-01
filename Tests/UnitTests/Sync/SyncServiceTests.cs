using System.Globalization;
using JellyShare.Application.Features.Sync;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.UnitTests.Sync;

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
        _config.Settings.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Settings.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _config.Settings.Peers.Add(_alice);
        var index = new LocalIndex(_library, _config, FakeLocalLibrary.Exists(p => !_missing.Contains(p)));
        _sync = TestSync.Create(
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

        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        MoviePlaceholder(SintelDir).ShouldBeFalse();
        report.Offered.ShouldBe(2);
        report.SkippedLocal.ShouldBe(1);
        report.Written.ShouldBe(2);
        report.PeerErrors.ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(MoviesRoot, BunnyDir, "Big Buck Bunny (2008).strm"))).ShouldBe("http://127.0.0.1:8096/JellyShare/v1/play/movie%3Atmdb%3A10378");
    }

    [Fact]
    public async Task Sync_LocalFileMissing_WritesPlaceholder()
    {
        AddLocalSintel();
        _missing.Add("/media/Movies/Sintel.mkv");
        Offer(Sintel());

        var report = await _sync.RunAsync(CancellationToken.None);

        MoviePlaceholder(SintelDir).ShouldBeTrue();
        report.SkippedLocal.ShouldBe(0);
    }

    [Fact]
    public async Task SyncPlanner_PeerUnreachable_KeepsItsPlaceholders()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("connection failed");
        _clock.Now += TimeSpan.FromDays(3);
        var report = await _sync.RunAsync(CancellationToken.None);

        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        report.Deleted.ShouldBe(0);
        report.PeerErrors.ShouldBe(["alice: connection failed"]);

        _clock.Now += TimeSpan.FromDays(3);
        report = await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        report.Deleted.ShouldBe(0);
    }

    [Fact]
    public async Task SyncPlanner_PeerUnreachable8Days_RemovesItsPlaceholders()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("timeout");
        _clock.Now += TimeSpan.FromDays(1);
        await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(BunnyDir).ShouldBeTrue();

        _clock.Now += TimeSpan.FromDays(8);
        var report = await _sync.RunAsync(CancellationToken.None);

        MoviePlaceholder(BunnyDir).ShouldBeFalse();
        report.Deleted.ShouldBe(2);
        report.PeerErrors.ShouldBe(["alice: timeout"]);
        var state = new PeerStateStore(Path.Combine(DataDir, "peer-state.json")).Load()[_alice.Id];
        state.FailingSinceUtc.ShouldBe(_clock.Now - TimeSpan.FromDays(8));
        state.LastError.ShouldBe("timeout");
    }

    [Fact]
    public async Task Sync_IncompatibleProtocol_ReportsErrorAndKeepsLastGood()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("incompatible protocol 2.0");
        _clock.Now += TimeSpan.FromHours(6);
        var report = await _sync.RunAsync(CancellationToken.None);

        report.PeerErrors.ShouldBe(["alice: incompatible protocol 2.0"]);
        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        report.Deleted.ShouldBe(0);
        report.Offered.ShouldBe(1);
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

        _client.Fetches.ShouldBe([(_alice.Id, (string?)null), (_alice.Id, "etag-1")]);
        report.PeerErrors.ShouldBeEmpty();
        report.Deleted.ShouldBe(0);
        report.Offered.ShouldBe(1);
        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        var state = new PeerStateStore(Path.Combine(DataDir, "peer-state.json")).Load()[_alice.Id];
        state.LastSuccessUtc.ShouldBe(_clock.Now);
        state.FailingSinceUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Sync_RemovedPeer_ContributesNothingAndStateIsDropped()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        _config.Settings.Peers.Clear();
        var report = await _sync.RunAsync(CancellationToken.None);

        MoviePlaceholder(BunnyDir).ShouldBeFalse();
        report.Deleted.ShouldBe(2);
        (new PeerStateStore(Path.Combine(DataDir, "peer-state.json")).Load()).ShouldBeEmpty();
        File.Exists(Path.Combine(MoviesRoot, PlaceholderWriter.MarkerName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Sync_DisabledPeer_ContributesNothing()
    {
        Offer(Bunny());
        _alice.Enabled = false;

        var report = await _sync.RunAsync(CancellationToken.None);

        _client.Fetches.ShouldBeEmpty();
        report.Offered.ShouldBe(0);
        MoviePlaceholder(BunnyDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_SecondRun_NoWritesNoScans()
    {
        Offer(Bunny(), Pioneer(1, 1), Pioneer(2, 1));
        var first = await _sync.RunAsync(CancellationToken.None);
        (first.Written > 0).ShouldBeTrue();
        _scans.Reported.ShouldNotBeEmpty();

        _scans.Reported.Clear();
        var second = await _sync.RunAsync(CancellationToken.None);

        second.Written.ShouldBe(0);
        second.Deleted.ShouldBe(0);
        second.Scanned.ShouldBe(0);
        _scans.Reported.ShouldBeEmpty();
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
        _scans.Reported.Order(StringComparer.Ordinal).ShouldBe(expected);
        report.Scanned.ShouldBe(3);
    }

    [Fact]
    public async Task Sync_ScanBatching_Max200PerRun_RestNextRun()
    {
        Offer(Enumerable.Range(1, 250).Select(i => TestItems.Movie("Movie " + i, tmdb: i.ToString(CultureInfo.InvariantCulture), id: "m" + i)).ToArray());

        var first = await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.Count.ShouldBe(200);
        first.Scanned.ShouldBe(200);
        first.PendingScans.ShouldBe(50);

        var reportedFirst = _scans.Reported.ToList();
        _scans.Reported.Clear();
        var second = await _sync.RunAsync(CancellationToken.None);
        second.Written.ShouldBe(0);
        _scans.Reported.Count.ShouldBe(50);
        reportedFirst.Intersect(_scans.Reported).ShouldBeEmpty();
        reportedFirst.Concat(_scans.Reported).Distinct().Count().ShouldBe(250);
        second.PendingScans.ShouldBe(0);

        _scans.Reported.Clear();
        await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_PersistsMergedIndex_RoundTrip()
    {
        var bunny = Bunny() with { Media = new MediaInfo("mkv", "h264", 1920, 1080, 1312543, [], []) };
        Offer(bunny, Pioneer(1, 1));

        await _sync.RunAsync(CancellationToken.None);

        var file = MergedIndexStore.Load(Path.Combine(DataDir, "merged-index.json"));
        file.ShouldNotBeNull();
        file.GeneratedUtc.ShouldBe(_clock.Now);
        var entries = file.ToEntries();
        entries.Select(e => e.CanonicalKey).ShouldBe(["ep:tvdb:153021:s1e1", "movie:tmdb:10378"]);
        var movie = entries[1];
        movie.Keys.ShouldBe(["movie:tmdb:10378"]);
        movie.Sources[0].PeerId.ShouldBe(_alice.Id);
        movie.Sources[0].Item.Id.ShouldBe("bunny");
        movie.Sources[0].Item.Media.BitRate.ShouldBe(1312543);
        movie.Sources[0].Item.Name.ShouldBe("Big Buck Bunny");
        entries[0].Sources[0].Item.SeriesId.ShouldBe("s1");
    }

    [Fact]
    public async Task Sync_SeriesPlaceholder_UsesLocalSeries_AndFetchesSeriesImages()
    {
        _library.Series.Add(new LocalSeriesItem("Pioneer One", "/media/Shows/Pioneer One", new Dictionary<string, string> { ["Tvdb"] = "153021" }));
        _library.Media.Add(new LocalMedia("Episode", "/media/Shows/Pioneer One/S01E01.mkv", null, new Dictionary<string, string> { ["Tvdb"] = "153021" }, 1, 1));
        Offer(Pioneer(1, 1), Pioneer(2, 1));
        _client.Images[(_alice.Id, "s1", "Primary")] = [7];

        var report = await _sync.RunAsync(CancellationToken.None);

        report.SkippedLocal.ShouldBe(1);
        File.Exists(Path.Combine(ShowsRoot, PioneerDir, "Season 01", "Pioneer One S01E01.strm")).ShouldBeFalse();
        File.Exists(Path.Combine(ShowsRoot, PioneerDir, "Season 02", "Pioneer One S02E01.strm")).ShouldBeTrue();
        (await File.ReadAllBytesAsync(Path.Combine(ShowsRoot, PioneerDir, "poster.jpg"))).ShouldBe([7]);
        _client.ImageRequests.ShouldContain((_alice.Id, "s1", "Backdrop"));
    }

    [Fact]
    public async Task Sync_ReportsConflicts()
    {
        Offer(
            TestItems.Movie("A", tmdb: "1", imdb: "tt1", id: "a"),
            TestItems.Movie("B", tmdb: "2", imdb: "tt1", id: "b"),
            Bunny());

        var report = await _sync.RunAsync(CancellationToken.None);

        report.Conflicts.ShouldBe(2);
        report.Unmatched.ShouldBe(2);
        report.Offered.ShouldBe(1);
        report.Skipped.ShouldBe(0);
    }

    [Fact]
    public async Task Sync_UnreadablePeerState_AbortsWithoutDeleting()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);

        var statePath = Path.Combine(DataDir, "peer-state.json");
        await File.WriteAllTextAsync(statePath, "{ truncated");
        _client.Catalogs[_alice.Id] = _ => FakePeerClient.Fail("connection failed");
        _clock.Now += TimeSpan.FromHours(6);

        await ShouldExactly.ThrowAsync<PeerStateUnreadableException>(() => _sync.RunAsync(CancellationToken.None));

        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        (await File.ReadAllTextAsync(statePath)).ShouldBe("{ truncated");
    }

    [Fact]
    public async Task Sync_WriterFailure_KeepsChangedDirsQueued()
    {
        Offer(Bunny(), Pioneer(2, 1));
        Directory.CreateDirectory(ShowsRoot);
        await File.WriteAllTextAsync(Path.Combine(ShowsRoot, PlaceholderWriter.MarkerName), string.Empty);
        var blocker = Path.Combine(ShowsRoot, PioneerDir);
        await File.WriteAllTextAsync(blocker, "a file where the series folder should go");

        await Should.ThrowAsync<Exception>(() => _sync.RunAsync(CancellationToken.None));
        MoviePlaceholder(BunnyDir).ShouldBeTrue();

        File.Delete(blocker);
        _scans.Reported.Clear();
        await _sync.RunAsync(CancellationToken.None);

        _scans.Reported.ShouldContain(Path.GetFullPath(Path.Combine(MoviesRoot, BunnyDir)));
        _scans.Reported.ShouldContain(Path.GetFullPath(Path.Combine(ShowsRoot, PioneerDir)));
    }

    [Fact]
    public async Task Sync_ScanReportFailure_RequeuesDir()
    {
        Offer(Bunny());
        var bunnyFull = Path.GetFullPath(Path.Combine(MoviesRoot, BunnyDir));
        _scans.FailFor = p => p == bunnyFull;

        var first = await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.ShouldBeEmpty();
        first.PendingScans.ShouldBe(1);

        _scans.FailFor = null;
        var second = await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.ShouldBe([bunnyFull]);
        second.PendingScans.ShouldBe(0);
    }

    [Fact]
    public async Task Sync_OverlappingPlaceholderRoots_RefusesToWrite()
    {
        _config.Settings.ShowsPlaceholderPath = Path.Combine(MoviesRoot, "Shows");
        Offer(Bunny(), Pioneer(2, 1));

        await ShouldExactly.ThrowAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        Directory.Exists(MoviesRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_PeerFetchThrowsIOException_RecordsPeerErrorAndOthersStillSync()
    {
        var bob = new RemotePeer { Id = Guid.NewGuid(), Name = "bob", BaseUrl = "http://bob", Token = "t2", Priority = 1 };
        _config.Settings.Peers.Add(bob);
        _client.Catalogs[_alice.Id] = _ => throw new IOException("response ended prematurely");
        _client.Catalogs[bob.Id] = _ => FakePeerClient.Ok(Bunny());

        var report = await _sync.RunAsync(CancellationToken.None);

        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        report.PeerErrors.ShouldHaveSingleItem();
        report.PeerErrors[0].ShouldStartWith("alice: connection failed", Case.Sensitive);
    }

    [Fact]
    public async Task Sync_PeerFetchThrows_KeepsItsLastGoodCatalog()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        _client.Catalogs[_alice.Id] = _ => throw new HttpRequestException("reset");

        var report = await _sync.RunAsync(CancellationToken.None);

        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        report.PeerErrors.ShouldHaveSingleItem();
        report.Deleted.ShouldBe(0);
    }

    [Fact]
    public async Task Sync_NonLoopbackRelayOverride_RefusesToWrite()
    {
        _config.Settings.RelayBaseOverride = "http://10.0.0.2:8096";
        Offer(Bunny());

        var ex = await ShouldExactly.ThrowAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        ex.Message.ShouldContain("RelayBaseOverride", Case.Sensitive);
        Directory.Exists(MoviesRoot).ShouldBeFalse();
    }

    private string BunnyNfo => Path.Combine(MoviesRoot, BunnyDir, "Big Buck Bunny (2008).nfo");

    private string BunnyStrm => Path.ChangeExtension(BunnyNfo, ".strm");

    [Fact]
    public async Task Sync_JellyfinRewritesNfo_NoRewriteWhileDesiredContentUnchanged()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        File.Exists(BunnyNfo).ShouldBeTrue();
        const string jellyfin = "<?xml version=\"1.0\"?><movie><title>Big Buck Bunny</title><lockdata>false</lockdata></movie>";
        await File.WriteAllTextAsync(BunnyNfo, jellyfin);
        _scans.Reported.Clear();

        var report = await _sync.RunAsync(CancellationToken.None);

        report.Written.ShouldBe(0);
        report.Deleted.ShouldBe(0);
        _scans.Reported.ShouldBeEmpty();
        (await File.ReadAllTextAsync(BunnyNfo)).ShouldBe(jellyfin);
    }

    [Fact]
    public async Task Sync_JellyfinSeasonNfo_IsLeftAlone()
    {
        Offer(Pioneer(1, 1));
        await _sync.RunAsync(CancellationToken.None);
        var seasonDir = Directory.GetDirectories(Path.Combine(ShowsRoot, PioneerDir)).Single();
        var seasonNfo = Path.Combine(seasonDir, "season.nfo");
        await File.WriteAllTextAsync(seasonNfo, "<season/>");
        await File.WriteAllTextAsync(Path.Combine(ShowsRoot, PioneerDir, "tvshow.nfo"), "<tvshow>rewritten by jellyfin</tvshow>");
        _scans.Reported.Clear();

        var report = await _sync.RunAsync(CancellationToken.None);

        (report.Written + report.Deleted).ShouldBe(0);
        File.Exists(seasonNfo).ShouldBeTrue();
        _scans.Reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_DesiredNfoChanged_IsRewritten_EvenAfterJellyfinEdit()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        await File.WriteAllTextAsync(BunnyNfo, "<movie>jellyfin</movie>");

        Offer(Bunny() with { Overview = "A new plot" });
        var report = await _sync.RunAsync(CancellationToken.None);

        report.Written.ShouldBe(1);
        (await File.ReadAllTextAsync(BunnyNfo)).ShouldContain("A new plot", Case.Sensitive);
    }

    [Fact]
    public async Task Sync_TitleGone_RemovesWholeFolder_IncludingJellyfinFiles()
    {
        Offer(Pioneer(1, 1));
        await _sync.RunAsync(CancellationToken.None);
        var series = Path.Combine(ShowsRoot, PioneerDir);
        await File.WriteAllTextAsync(Path.Combine(Directory.GetDirectories(series).Single(), "season.nfo"), "<season/>");

        Offer();
        await _sync.RunAsync(CancellationToken.None);

        Directory.Exists(series).ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_NoLedgerYet_SeedsFromDisk_NoRewrites_AndKeepsForeignNfo()
    {
        Offer(Pioneer(1, 1), Bunny());
        await _sync.RunAsync(CancellationToken.None);
        var seasonNfo = Path.Combine(Directory.GetDirectories(Path.Combine(ShowsRoot, PioneerDir)).Single(), "season.nfo");
        await File.WriteAllTextAsync(seasonNfo, "<season/>");
        File.Delete(Path.Combine(DataDir, "placeholder-ledger.json"));
        _scans.Reported.Clear();

        var report = await _sync.RunAsync(CancellationToken.None);

        (report.Written + report.Deleted).ShouldBe(0);
        File.Exists(seasonNfo).ShouldBeTrue();
        File.Exists(Path.Combine(DataDir, "placeholder-ledger.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Sync_PlaceholderDeletedByHand_IsWrittenAgain()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        File.Delete(BunnyStrm);

        var report = await _sync.RunAsync(CancellationToken.None);

        report.Written.ShouldBe(1);
        File.Exists(BunnyStrm).ShouldBeTrue();
    }

    [Theory]
    [InlineData("ph")]                 // a library folder containing the placeholder roots
    [InlineData("ph/Movies/Owner")]    // a library folder inside a placeholder root
    public async Task Sync_RootOverlapsAnOwnerLibraryFolder_RefusesWithClearError(string location)
    {
        _library.Locations.Add(_tmp.Sub(location.Split('/')));
        Offer(Bunny());

        var ex = await ShouldExactly.ThrowAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        ex.Message.ShouldContain("library folder", Case.Sensitive);
        MoviePlaceholder(BunnyDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_RootIsAnOwnerMovieFolder_RefusesAndDeletesNothing()
    {
        var owner = _tmp.Sub("media", "Movies");
        Directory.CreateDirectory(Path.Combine(owner, "Owned (2001)"));
        var ownerNfo = Path.Combine(owner, "Owned (2001)", "Owned (2001).nfo");
        await File.WriteAllTextAsync(ownerNfo, "<movie/>");
        _library.Locations.Add(owner);
        _config.Settings.MoviesPlaceholderPath = owner;
        Offer(Bunny());

        await ShouldExactly.ThrowAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        File.Exists(ownerNfo).ShouldBeTrue();
        File.Exists(Path.Combine(owner, ".jellyshare")).ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_AttachedPluginRoots_AreNotOverlaps()
    {
        _library.Locations.Add(MoviesRoot);
        _library.Locations.Add(ShowsRoot);
        _library.Locations.Add(_tmp.Sub("media", "Movies"));
        Offer(Bunny());

        await _sync.RunAsync(CancellationToken.None);

        MoviePlaceholder(BunnyDir).ShouldBeTrue();
    }

    [Fact]
    public async Task Sync_ExistingNonEmptyRootWithoutMarker_Refuses()
    {
        Directory.CreateDirectory(MoviesRoot);
        var foreign = Path.Combine(MoviesRoot, "someone else.nfo");
        await File.WriteAllTextAsync(foreign, "x");
        Offer(Bunny());

        var ex = await ShouldExactly.ThrowAsync<InvalidOperationException>(() => _sync.RunAsync(CancellationToken.None));

        ex.Message.ShouldContain(".jellyshare", Case.Sensitive);
        File.Exists(foreign).ShouldBeTrue();
        File.Exists(Path.Combine(MoviesRoot, ".jellyshare")).ShouldBeFalse();
    }

    [Fact]
    public async Task Sync_ExistingEmptyRoot_IsAdopted()
    {
        Directory.CreateDirectory(MoviesRoot);
        Offer(Bunny());

        await _sync.RunAsync(CancellationToken.None);

        File.Exists(Path.Combine(MoviesRoot, ".jellyshare")).ShouldBeTrue();
        MoviePlaceholder(BunnyDir).ShouldBeTrue();
    }

    [Fact]
    public async Task Reconcile_RootOverlapsLibraryFolder_Refuses()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        _library.Locations.Add(_tmp.Sub("ph"));

        await ShouldExactly.ThrowAsync<InvalidOperationException>(() => _sync.ReconcileAsync(["movie:tmdb:10378"], CancellationToken.None));
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
        File.Exists(BunnyStrm).ShouldBeTrue();
        _scans.Reported.ShouldContain(Path.GetFullPath(Path.Combine(MoviesRoot, BunnyDir)));
        var reconcile = _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);
        (await Task.WhenAny(reconcile, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBeSameAs(reconcile);
        run.IsCompleted.ShouldBeFalse();

        releaseImages.SetResult();
        var report = await run;
        report.ImagesWritten.ShouldBe(1);
        File.Exists(Path.Combine(MoviesRoot, BunnyDir, "poster.jpg")).ShouldBeTrue();
    }

    [Fact]
    public async Task Sync_ImageDownloads_RunInParallel_AtMostFour()
    {
        var items = Enumerable.Range(1, 12).Select(i => TestItems.Movie("M" + i, tmdb: (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), id: "m" + i) with { Year = 2000, ImageTypes = ["Primary", "Backdrop"] }).ToArray();
        _client.BeforeImage = () => Task.Delay(30);
        Offer(items);

        await _sync.RunAsync(CancellationToken.None);

        _client.MaxConcurrentImages.ShouldBeInRange(2, 4);
    }

    [Fact]
    public async Task Sync_MissingImage_IsRetriedOnALaterSync()
    {
        Offer(Bunny() with { ImageTypes = ["Primary"] });
        await _sync.RunAsync(CancellationToken.None);
        var poster = Path.Combine(MoviesRoot, BunnyDir, "poster.jpg");
        File.Exists(poster).ShouldBeFalse();

        _client.Images[(_alice.Id, "bunny", "Primary")] = [9];
        var report = await _sync.RunAsync(CancellationToken.None);

        report.Written.ShouldBe(0);
        report.ImagesWritten.ShouldBe(1);
        File.Exists(poster).ShouldBeTrue();
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
        folders.ShouldBe(SyncService.MaxImageRetriesPerRun);
    }

    [Fact]
    public async Task Sync_TryStartBackground_StartsOnce_ReportsRunning_ThenLastReport()
    {
        Offer(Bunny());
        var release = new TaskCompletionSource();
        _client.BeforeFetch = () => release.Task;

        _sync.TryStartBackground().ShouldBeTrue();
        await WaitUntil(() => _sync.IsRunning);
        _sync.TryStartBackground().ShouldBeFalse();

        release.SetResult();
        await WaitUntil(() => !_sync.IsRunning);
        _sync.LastReport.ShouldNotBeNull();
        _sync.LastError.ShouldBeNull();
        MoviePlaceholder(BunnyDir).ShouldBeTrue();
    }

    [Fact]
    public async Task Sync_BackgroundFailure_IsRecordedAsLastError_AndClearedBySuccess()
    {
        _config.Settings.RelayBaseOverride = "http://10.0.0.2:8096";
        Offer(Bunny());

        _sync.TryStartBackground().ShouldBeTrue();
        await WaitUntil(() => !_sync.IsRunning && _sync.LastError is not null);
        _sync.LastError.ShouldNotBeNull().ShouldContain("RelayBaseOverride", Case.Sensitive);

        _config.Settings.RelayBaseOverride = null;
        await _sync.RunAsync(CancellationToken.None);
        _sync.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Sync_QueuedBackgroundRun_RunsAgainAfterTheCurrentOne()
    {
        Offer(Bunny());
        var release = new TaskCompletionSource();
        _client.BeforeFetch = () => release.Task;
        var first = _sync.RunAsync(CancellationToken.None);
        await WaitUntil(() => _sync.IsRunning);

        _sync.TryStartBackground(queueIfRunning: true).ShouldBeFalse();
        release.SetResult();
        await first;

        await WaitUntil(() => _client.Fetches.Count >= 2 && !_sync.IsRunning);
        _client.Fetches.Count.ShouldBe(2);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("condition not reached in time");
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
        _client.Entered.ShouldBe(1);
        release.SetResult();
        await Task.WhenAll(a, b);

        maxInside.ShouldBe(1);
        _client.Fetches.Count.ShouldBe(2);
    }
}
