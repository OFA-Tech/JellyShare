using JellyShare.Application.Features.Sync;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.UnitTests.Sync;

public sealed class SyncServiceReconcileTests : IDisposable
{
    private const string BunnyDir = "Big Buck Bunny (2008) [tmdbid-10378]";
    private const string SintelDir = "Sintel (2010) [tmdbid-45745]";
    private const string SintelPath = "/media/Movies/Sintel.mkv";
    private const string PioneerDir = "Pioneer One [tvdbid-153021]";

    private readonly TempDir _tmp = new();
    private readonly ConsumerConfig _config = new();
    private readonly FakePeerClient _client = new();
    private readonly FakeLocalLibrary _library = new();
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly FakeScans _scans = new();
    private readonly RemotePeer _alice = new() { Id = TestItems.PeerA, Name = "alice", BaseUrl = "http://alice", Token = "t" };
    private readonly SyncService _sync;

    public SyncServiceReconcileTests()
    {
        _config.Settings.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Settings.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _config.Settings.Peers.Add(_alice);
        var index = new LocalIndex(_library, _config, FakeLocalLibrary.Exists(p => !_missing.Contains(p)));
        _sync = TestSync.Create(
            _config, _client, index, _scans, new FixedRelay(), () => _tmp.Sub("data"), new MutableClock(), NullLogger<SyncService>.Instance);
    }

    private string MoviesRoot => _config.Current.MoviesPlaceholderPath;

    private string ShowsRoot => _config.Current.ShowsPlaceholderPath;

    public void Dispose() => _tmp.Dispose();

    private static CatalogItem Bunny() => TestItems.Movie("Big Buck Bunny", tmdb: "10378", id: "bunny") with { Year = 2008 };

    private static CatalogItem Sintel() => TestItems.Movie("Sintel", tmdb: "45745", id: "sintel") with { Year = 2010 };

    private static CatalogItem Pioneer(int s, int e) =>
        TestItems.Episode("Pioneer One", new() { ["Tvdb"] = "153021" }, s, e, $"p{s}{e}");

    private void Offer(params CatalogItem[] items) => _client.Catalogs[_alice.Id] = _ => FakePeerClient.Ok(items);

    private bool MoviePlaceholder(string dir) => Directory.Exists(Path.Combine(MoviesRoot, dir));

    private void AddLocalSintel()
        => _library.Media.Add(new LocalMedia("Movie", SintelPath, new Dictionary<string, string> { ["Tmdb"] = "45745" }, null, null, null));

    private void AddLocalPioneer(int s, int e)
        => _library.Media.Add(new LocalMedia(
            "Episode",
            $"/media/Shows/Pioneer One/S{s}E{e}.mkv",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Tvdb"] = "153021" },
            s,
            e));

    [Fact]
    public async Task Reconcile_LocalMissing_WritesOnlyAffectedEntries()
    {
        AddLocalSintel();
        Offer(Bunny(), Sintel());
        await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        _scans.Reported.Clear();

        // Bunny's placeholder was deleted by hand: a targeted reconcile must not recreate it.
        Directory.Delete(Path.Combine(MoviesRoot, BunnyDir), true);
        _missing.Add(SintelPath);
        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        MoviePlaceholder(SintelDir).ShouldBeTrue();
        MoviePlaceholder(BunnyDir).ShouldBeFalse();
        report.Written.ShouldBe(2);
        report.Entries.ShouldBe(1);
        _scans.Reported.ShouldBe([Path.Combine(MoviesRoot, SintelDir)]);
        _client.Fetches.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Reconcile_LocalBack_DeletesPlaceholderOnly()
    {
        _missing.Add(SintelPath);
        AddLocalSintel();
        Offer(Bunny(), Sintel());
        await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(SintelDir).ShouldBeTrue();
        _scans.Reported.Clear();

        _missing.Clear();
        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        MoviePlaceholder(SintelDir).ShouldBeFalse();
        MoviePlaceholder(BunnyDir).ShouldBeTrue();
        report.Deleted.ShouldBe(2);
        _scans.Reported.ShouldBe([Path.Combine(MoviesRoot, SintelDir)]);
    }

    [Fact]
    public async Task Reconcile_UnknownKey_ChangesNothing()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.Clear();

        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        report.Entries.ShouldBe(0);
        (report.Written + report.Deleted).ShouldBe(0);
        _scans.Reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_WithoutMergedIndex_ChangesNothing()
    {
        AddLocalSintel();

        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        report.Entries.ShouldBe(0);
        (Directory.Exists(MoviesRoot) && Directory.EnumerateDirectories(MoviesRoot).Any()).ShouldBeFalse();
    }

    [Fact]
    public async Task Reconcile_EpisodeBecomesLocal_KeepsSeriesAndOtherEpisodes()
    {
        Offer(Pioneer(1, 1), Pioneer(1, 2));
        await _sync.RunAsync(CancellationToken.None);
        var series = Path.Combine(ShowsRoot, PioneerDir);
        Directory.Exists(series).ShouldBeTrue();
        File.Exists(Path.Combine(series, "tvshow.nfo")).ShouldBeTrue();

        AddLocalPioneer(1, 1);
        var key = MediaKey.For(Pioneer(1, 1)).First();
        var report = await _sync.ReconcileAsync([key], CancellationToken.None);

        report.Deleted.ShouldBe(2);
        File.Exists(Path.Combine(series, "tvshow.nfo")).ShouldBeTrue();
        Directory.GetFiles(series, "*.strm", SearchOption.AllDirectories).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Reconcile_TouchesOnlyAffectedTitles_NoFullLibraryRead_NoPlaceholderReads_NoIndexReparse()
    {
        AddLocalSintel();
        Offer(Bunny(), Sintel(), Pioneer(1, 1));
        await _sync.RunAsync(CancellationToken.None);
        var fullReads = _library.Reads;
        var loads = _sync.MergedIndexLoads;
        var reads = new List<string>();
        JellyShare.Data.Files.Placeholders.PlaceholderWriter.ReadObserver.Value = reads.Add;

        _missing.Add(SintelPath);
        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        report.Written.ShouldBe(2);
        MoviePlaceholder(SintelDir).ShouldBeTrue();
        _library.Reads.ShouldBe(fullReads);
        _library.ScopedReads.ShouldBe(1);
        _library.LastScope!.MovieIds["tmdb"].ShouldContain("45745");
        _library.LastScope.SeriesIds.ShouldBeEmpty();
        reads.ShouldBeEmpty();
        _sync.MergedIndexLoads.ShouldBe(loads);
    }

    [Fact]
    public async Task Reconcile_MergedIndexReplacedOnDisk_IsReloaded()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        var path = Path.Combine(_tmp.Sub("data"), MergedIndexStore.FileName);
        MergedIndexStore.Save(path, Array.Empty<MergedEntry>(), DateTimeOffset.UnixEpoch);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var report = await _sync.ReconcileAsync(["movie:tmdb:10378"], CancellationToken.None);

        report.Entries.ShouldBe(0);
    }

    [Fact]
    public async Task Reconcile_Episode_ScopesSeriesQueryToItsSeriesIds()
    {
        Offer(Pioneer(1, 1), Pioneer(1, 2));
        await _sync.RunAsync(CancellationToken.None);
        AddLocalPioneer(1, 1);

        var report = await _sync.ReconcileAsync([MediaKey.For(Pioneer(1, 1)).First()], CancellationToken.None);

        report.Deleted.ShouldBe(2);
        _library.LastScope!.SeriesIds["tvdb"].ShouldContain("153021");
        _library.LastScope.MovieIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_WaitsForRunningSync()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        _client.BeforeFetch = async () =>
        {
            entered.TrySetResult();
            await gate.Task;
        };

        var run = _sync.RunAsync(CancellationToken.None);
        await entered.Task;
        var reconcile = _sync.ReconcileAsync(["movie:tmdb:10378"], CancellationToken.None);
        await Task.Delay(200);

        run.IsCompleted.ShouldBeFalse();
        reconcile.IsCompleted.ShouldBeFalse();

        gate.SetResult();
        await Task.WhenAll(run, reconcile);
    }
}
