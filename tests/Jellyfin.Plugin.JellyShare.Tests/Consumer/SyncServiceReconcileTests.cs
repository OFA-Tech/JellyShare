using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Consumer;
using Jellyfin.Plugin.JellyShare.Keys;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Tests.Keys;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Consumer;

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
        _config.Current.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Current.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _config.Current.Peers.Add(_alice);
        var index = new LocalIndex(_library, _config, FakeLocalLibrary.Exists(p => !_missing.Contains(p)));
        _sync = new SyncService(
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
        Assert.True(MoviePlaceholder(BunnyDir));
        _scans.Reported.Clear();

        // Bunny's placeholder was deleted by hand: a targeted reconcile must not recreate it.
        Directory.Delete(Path.Combine(MoviesRoot, BunnyDir), true);
        _missing.Add(SintelPath);
        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        Assert.True(MoviePlaceholder(SintelDir));
        Assert.False(MoviePlaceholder(BunnyDir));
        Assert.Equal(2, report.Written);
        Assert.Equal(1, report.Entries);
        Assert.Equal([Path.Combine(MoviesRoot, SintelDir)], _scans.Reported);
        Assert.Single(_client.Fetches);
    }

    [Fact]
    public async Task Reconcile_LocalBack_DeletesPlaceholderOnly()
    {
        _missing.Add(SintelPath);
        AddLocalSintel();
        Offer(Bunny(), Sintel());
        await _sync.RunAsync(CancellationToken.None);
        Assert.True(MoviePlaceholder(SintelDir));
        _scans.Reported.Clear();

        _missing.Clear();
        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        Assert.False(MoviePlaceholder(SintelDir));
        Assert.True(MoviePlaceholder(BunnyDir));
        Assert.Equal(2, report.Deleted);
        Assert.Equal([Path.Combine(MoviesRoot, SintelDir)], _scans.Reported);
    }

    [Fact]
    public async Task Reconcile_UnknownKey_ChangesNothing()
    {
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.Clear();

        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        Assert.Equal(0, report.Entries);
        Assert.Equal(0, report.Written + report.Deleted);
        Assert.Empty(_scans.Reported);
    }

    [Fact]
    public async Task Reconcile_WithoutMergedIndex_ChangesNothing()
    {
        AddLocalSintel();

        var report = await _sync.ReconcileAsync(["movie:tmdb:45745"], CancellationToken.None);

        Assert.Equal(0, report.Entries);
        Assert.False(Directory.Exists(MoviesRoot) && Directory.EnumerateDirectories(MoviesRoot).Any());
    }

    [Fact]
    public async Task Reconcile_EpisodeBecomesLocal_KeepsSeriesAndOtherEpisodes()
    {
        Offer(Pioneer(1, 1), Pioneer(1, 2));
        await _sync.RunAsync(CancellationToken.None);
        var series = Path.Combine(ShowsRoot, PioneerDir);
        Assert.True(Directory.Exists(series));
        Assert.True(File.Exists(Path.Combine(series, "tvshow.nfo")));

        AddLocalPioneer(1, 1);
        var key = MediaKey.For(Pioneer(1, 1)).First();
        var report = await _sync.ReconcileAsync([key], CancellationToken.None);

        Assert.Equal(2, report.Deleted);
        Assert.True(File.Exists(Path.Combine(series, "tvshow.nfo")));
        Assert.Single(Directory.GetFiles(series, "*.strm", SearchOption.AllDirectories));
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

        Assert.False(run.IsCompleted);
        Assert.False(reconcile.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(run, reconcile);
    }
}
