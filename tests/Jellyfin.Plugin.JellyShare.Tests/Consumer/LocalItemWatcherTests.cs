using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Consumer;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Tests.Keys;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Consumer;

internal sealed class FakeItemEvents : ILocalItemEvents
{
    private Action<LocalItemEvent>? _handler;

    public int Active { get; private set; }

    public IDisposable Subscribe(Action<LocalItemEvent> handler)
    {
        _handler += handler;
        Active++;
        return new Unsubscribe(this, handler);
    }

    public void Raise(LocalItemEvent e) => _handler?.Invoke(e);

    private sealed class Unsubscribe : IDisposable
    {
        private readonly FakeItemEvents _owner;
        private readonly Action<LocalItemEvent> _handler;
        private bool _done;

        public Unsubscribe(FakeItemEvents owner, Action<LocalItemEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _owner._handler -= _handler;
            _owner.Active--;
        }
    }
}

internal sealed class RecordingTimeProvider : TimeProvider
{
    public List<RecordedTimer> Timers { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new RecordedTimer(callback, state, dueTime, period);
        Timers.Add(timer);
        return timer;
    }

    internal sealed class RecordedTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public RecordedTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Period = period;
        }

        public TimeSpan DueTime { get; private set; }

        public TimeSpan Period { get; private set; }

        public bool Disposed { get; private set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class LocalItemWatcherTests : IDisposable
{
    private const string SintelDir = "Sintel (2010) [tmdbid-45745]";
    private const string SintelPath = "/media/Movies/Sintel (2010) [tmdbid-45745]/Sintel (2010).mkv";

    private readonly TempDir _tmp = new();
    private readonly ConsumerConfig _config = new();
    private readonly FakePeerClient _client = new();
    private readonly FakeLocalLibrary _library = new();
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly List<string> _probed = new();
    private readonly FakeScans _scans = new();
    private readonly MutableClock _clock = new();
    private readonly RecordingTimeProvider _timers = new();
    private readonly FakeItemEvents _events = new();
    private readonly RemotePeer _alice = new() { Id = TestItems.PeerA, Name = "alice", BaseUrl = "http://alice", Token = "t" };
    private readonly SyncService _sync;
    private readonly LocalItemWatcher _watcher;

    public LocalItemWatcherTests()
    {
        _config.Current.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Current.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _config.Current.Peers.Add(_alice);
        var index = new LocalIndex(_library, _config, FakeLocalLibrary.Exists(p => !_missing.Contains(p)));
        _sync = new SyncService(
            _config, _client, index, _scans, new FixedRelay(), () => _tmp.Sub("data"), _clock, NullLogger<SyncService>.Instance);
        _watcher = NewWatcher(null);
    }

    private string MoviesRoot => _config.Current.MoviesPlaceholderPath;

    public void Dispose() => _tmp.Dispose();

    private static CatalogItem Bunny() => TestItems.Movie("Big Buck Bunny", tmdb: "10378", id: "bunny") with { Year = 2008 };

    private static CatalogItem Sintel() => TestItems.Movie("Sintel", tmdb: "45745", id: "sintel") with { Year = 2010 };

    private static CatalogItem Numbered(int n) => TestItems.Movie("Movie " + n, tmdb: (100000 + n).ToString(), id: "m" + n) with { Year = 2000 };

    private static LocalMedia LocalMovie(string path, string tmdb)
        => new("Movie", path, new Dictionary<string, string> { ["Tmdb"] = tmdb }, null, null, null);

    private LocalItemWatcher NewWatcher(TimeSpan? budget, Func<string, FilePresence>? probe = null)
        => new(
            _sync,
            _events,
            _library,
            _config,
            _timers,
            NullLogger<LocalItemWatcher>.Instance,
            probe ?? (p =>
            {
                lock (_probed)
                {
                    _probed.Add(p);
                }

                return _missing.Contains(p) ? FilePresence.Missing : FilePresence.Present;
            }),
            budget);

    private void Offer(params CatalogItem[] items) => _client.Catalogs[_alice.Id] = _ => FakePeerClient.Ok(items);

    private bool MoviePlaceholder(string dir) => Directory.Exists(Path.Combine(MoviesRoot, dir));

    private void AddLocalSintel() => _library.Media.Add(LocalMovie(SintelPath, "45745"));

    [Fact]
    public async Task LocalRemoved_WithPeerCopy_WritesPlaceholder()
    {
        AddLocalSintel();
        Offer(Bunny(), Sintel());
        await _sync.RunAsync(CancellationToken.None);
        Assert.False(MoviePlaceholder(SintelDir));
        _scans.Reported.Clear();
        await _watcher.StartAsync(CancellationToken.None);

        _missing.Add(SintelPath);
        _events.Raise(new LocalItemEvent(true, LocalMovie(SintelPath, "45745")));
        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(SintelDir));
        Assert.Equal(1, batch.Keys);
        Assert.False(batch.FullSync);
        Assert.Equal([Path.Combine(MoviesRoot, SintelDir)], _scans.Reported);
        Assert.Single(_client.Fetches);
    }

    [Fact]
    public async Task LocalRemoved_NoPeerCopy_DoesNothing()
    {
        AddLocalSintel();
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.Clear();
        await _watcher.StartAsync(CancellationToken.None);

        _missing.Add(SintelPath);
        _events.Raise(new LocalItemEvent(true, LocalMovie(SintelPath, "45745")));
        await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.False(MoviePlaceholder(SintelDir));
        Assert.Empty(_scans.Reported);
        Assert.Single(_client.Fetches);
    }

    [Fact]
    public async Task LocalAdded_RemovesPlaceholder()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        Assert.True(MoviePlaceholder(SintelDir));
        _scans.Reported.Clear();
        await _watcher.StartAsync(CancellationToken.None);

        AddLocalSintel();
        _events.Raise(new LocalItemEvent(false, LocalMovie(SintelPath, "45745")));
        await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.False(MoviePlaceholder(SintelDir));
        Assert.Equal([Path.Combine(MoviesRoot, SintelDir)], _scans.Reported);
        Assert.Single(_client.Fetches);
    }

    [Fact]
    public async Task LocalAdded_IdsNotReadyYet_IsResolvedWhenTheWindowIsProcessed()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        Assert.True(MoviePlaceholder(SintelDir));
        await _watcher.StartAsync(CancellationToken.None);

        // At event time the new item has no provider ids; the metadata refresh adds them before the window runs.
        var noIds = new LocalMedia("Movie", SintelPath, new Dictionary<string, string>(), null, null, null);
        LocalMedia? current = noIds;
        _events.Raise(new LocalItemEvent(false, noIds, () => current));
        current = LocalMovie(SintelPath, "45745");
        AddLocalSintel();
        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, batch.Keys);
        Assert.False(MoviePlaceholder(SintelDir));
    }

    [Fact]
    public async Task LocalAdded_IdsNeverAppear_IsDroppedAfterRetries()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);
        var noIds = new LocalMedia("Movie", SintelPath, new Dictionary<string, string>(), null, null, null);
        var calls = 0;
        _events.Raise(new LocalItemEvent(false, noIds, () =>
        {
            calls++;
            return noIds;
        }));

        for (var i = 0; i < 6; i++)
        {
            var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);
            Assert.Equal(0, batch.Keys);
        }

        Assert.Equal(3, calls);
        Assert.True(MoviePlaceholder(SintelDir));
    }

    [Fact]
    public async Task PlaceholderEvents_AreIgnored()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.Clear();
        await _watcher.StartAsync(CancellationToken.None);

        var placeholder = Path.Combine(MoviesRoot, SintelDir, "Sintel (2010).strm");
        _events.Raise(new LocalItemEvent(true, LocalMovie(placeholder, "45745")));
        _events.Raise(new LocalItemEvent(false, LocalMovie(placeholder, "45745")));
        _events.Raise(new LocalItemEvent(false, LocalMovie("/media/Movies/Other/Sintel.strm", "45745")));
        _events.Raise(new LocalItemEvent(true, new LocalMedia("Folder", "/media/Movies/Sintel", null, null, null, null)));
        _events.Raise(new LocalItemEvent(true, new LocalMedia("Series", "/media/Shows/Pioneer", null, null, null, null)));
        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, batch.Keys);
        Assert.False(batch.FullSync);
        Assert.True(MoviePlaceholder(SintelDir));
        Assert.Empty(_scans.Reported);
        Assert.Single(_client.Fetches);
    }

    [Fact]
    public async Task Storm_Over500_DelegatesToFullSync()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);

        for (var i = 0; i < 501; i++)
        {
            _events.Raise(new LocalItemEvent(true, LocalMovie("/media/Movies/m" + i + "/m.mkv", (100000 + i).ToString())));
        }

        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.True(batch.FullSync);
        Assert.Equal(2, _client.Fetches.Count);

        // The next window starts empty.
        var next = await _watcher.ProcessPendingAsync(CancellationToken.None);
        Assert.False(next.FullSync);
        Assert.Equal(2, _client.Fetches.Count);
    }

    [Fact]
    public async Task Exactly500Events_AreNotAStorm()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);

        for (var i = 0; i < 500; i++)
        {
            _events.Raise(new LocalItemEvent(true, LocalMovie("/media/Movies/m" + i + "/m.mkv", (100000 + i).ToString())));
        }

        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.False(batch.FullSync);
        Assert.Single(_client.Fetches);
    }

    [Fact]
    public async Task StartStop_SubscribesStartsTimersAndCleansUp()
    {
        await _watcher.StartAsync(CancellationToken.None);

        Assert.Equal(1, _events.Active);
        Assert.Equal(2, _timers.Timers.Count);
        Assert.Contains(_timers.Timers, t => t.Period == TimeSpan.FromSeconds(15));
        Assert.Contains(_timers.Timers, t => t.Period == TimeSpan.FromSeconds(60));

        await _watcher.StopAsync(CancellationToken.None);

        Assert.Equal(0, _events.Active);
        Assert.All(_timers.Timers, t => Assert.True(t.Disposed));
    }

    [Fact]
    public async Task Events_AfterStop_AreNotQueued()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);
        await _watcher.StopAsync(CancellationToken.None);

        _events.Raise(new LocalItemEvent(true, LocalMovie(SintelPath, "45745")));
        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, batch.Keys);
    }

    [Fact]
    public async Task ProcessPending_ReconcileFails_LogsAndKeepsKeysForRetry()
    {
        AddLocalSintel();
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);
        _missing.Add(SintelPath);
        _scans.FailFor = _ => false;
        var blocker = Path.Combine(MoviesRoot, SintelDir);
        File.WriteAllText(blocker, "a file where the folder must go");
        _events.Raise(new LocalItemEvent(true, LocalMovie(SintelPath, "45745")));

        var failed = await _watcher.ProcessPendingAsync(CancellationToken.None);
        Assert.True(failed.Failed);

        File.Delete(blocker);
        var retried = await _watcher.ProcessPendingAsync(CancellationToken.None);

        Assert.False(retried.Failed);
        Assert.True(MoviePlaceholder(SintelDir));
    }

    [Fact]
    public async Task Sweep_LocalFileMissing_WritesPlaceholder()
    {
        AddLocalSintel();
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        _scans.Reported.Clear();

        _missing.Add(SintelPath);
        var result = await _watcher.SweepAsync(CancellationToken.None);

        Assert.True(MoviePlaceholder(SintelDir));
        Assert.Equal(1, result.Changed);
        Assert.Equal([Path.Combine(MoviesRoot, SintelDir)], _scans.Reported);

        // Written once: a second pass with the same state changes nothing.
        _scans.Reported.Clear();
        var again = await _watcher.SweepAsync(CancellationToken.None);
        Assert.Equal(0, again.Changed);
        Assert.Empty(_scans.Reported);
    }

    [Fact]
    public async Task Sweep_FileBack_RemovesPlaceholder()
    {
        AddLocalSintel();
        _missing.Add(SintelPath);
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        Assert.True(MoviePlaceholder(SintelDir));
        await _watcher.SweepAsync(CancellationToken.None);
        _scans.Reported.Clear();

        _missing.Clear();
        var result = await _watcher.SweepAsync(CancellationToken.None);

        Assert.False(MoviePlaceholder(SintelDir));
        Assert.Equal(1, result.Changed);
        Assert.Equal([Path.Combine(MoviesRoot, SintelDir)], _scans.Reported);
    }

    [Fact]
    public async Task Sweep_OnlyChecksKeysInMergedIndex()
    {
        AddLocalSintel();
        _library.Media.Add(LocalMovie("/media/Movies/Other/Other.mkv", "999"));
        _library.Media.Add(new LocalMedia("Movie", "/media/Movies/NoIds/NoIds.mkv", new Dictionary<string, string>(), null, null, null));
        _library.Media.Add(LocalMovie(Path.Combine(MoviesRoot, SintelDir, "Sintel (2010).strm"), "45745"));
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);

        var result = await _watcher.SweepAsync(CancellationToken.None);

        Assert.Equal([SintelPath], _probed);
        Assert.Equal(1, result.Candidates);
    }

    [Fact]
    public async Task Sweep_WithoutMergedIndex_DoesNothing()
    {
        AddLocalSintel();

        var result = await _watcher.SweepAsync(CancellationToken.None);

        Assert.Equal(0, result.Candidates);
        Assert.Empty(_probed);
    }

    [Fact]
    public async Task Sweep_ManyMissing_DelegatesToFullSync()
    {
        var items = new List<CatalogItem>();
        for (var i = 0; i < 501; i++)
        {
            items.Add(Numbered(i));
            _library.Media.Add(LocalMovie("/media/Movies/m" + i + "/m.mkv", (100000 + i).ToString()));
        }

        Offer(items.ToArray());
        await _sync.RunAsync(CancellationToken.None);
        Assert.Single(_client.Fetches);

        for (var i = 0; i < 501; i++)
        {
            _missing.Add("/media/Movies/m" + i + "/m.mkv");
        }

        var result = await _watcher.SweepAsync(CancellationToken.None);

        Assert.True(result.FullSync);
        Assert.Equal(2, _client.Fetches.Count);
        Assert.True(MoviePlaceholder("Movie 7 (2000) [tmdbid-100007]"));
    }

    [Fact]
    public async Task Sweep_RespectsTimeBudget()
    {
        using var release = new ManualResetEventSlim(false);
        const string slow = "/media/Movies/Slow/Slow.mkv";
        const string gone = "/media/Movies/Gone/Gone.mkv";
        _library.Media.Add(LocalMovie(slow, "1"));
        _library.Media.Add(LocalMovie(gone, "2"));
        Offer(
            TestItems.Movie("Slow", tmdb: "1", id: "slow") with { Year = 2000 },
            TestItems.Movie("Gone", tmdb: "2", id: "gone") with { Year = 2000 });
        await _sync.RunAsync(CancellationToken.None);
        _missing.Add(gone);

        var watcher = NewWatcher(
            TimeSpan.FromMilliseconds(150),
            p =>
            {
                if (p == slow)
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                }

                return _missing.Contains(p) ? FilePresence.Missing : FilePresence.Present;
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = await watcher.SweepAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(0, first.Examined);
        Assert.Equal(2, first.Candidates);
        Assert.False(MoviePlaceholder("Gone (2000) [tmdbid-2]"));

        // While the stuck probe is still running no second probe thread is started.
        var skipped = await watcher.SweepAsync(CancellationToken.None);
        Assert.True(skipped.Skipped);

        release.Set();
        SweepResult done = skipped;
        for (var i = 0; i < 100 && done.Skipped; i++)
        {
            await Task.Delay(50);
            done = await watcher.SweepAsync(CancellationToken.None);
        }

        Assert.False(done.Skipped);
        Assert.True(MoviePlaceholder("Gone (2000) [tmdbid-2]"));
    }
}
