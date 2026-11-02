using System.Globalization;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Application.Features.Sync;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.UnitTests.Sync;

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

    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

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
        _config.Settings.MoviesPlaceholderPath = _tmp.Sub("ph", "Movies");
        _config.Settings.ShowsPlaceholderPath = _tmp.Sub("ph", "Shows");
        _config.Settings.Peers.Add(_alice);
        var index = new LocalIndex(_library, _config, FakeLocalLibrary.Exists(p => !_missing.Contains(p)));
        _sync = TestSync.Create(
            _config, _client, index, _scans, new FixedRelay(), () => _tmp.Sub("data"), _clock, NullLogger<SyncService>.Instance);
        _watcher = NewWatcher(null);
    }

    private string MoviesRoot => _config.Current.MoviesPlaceholderPath;

    public void Dispose() => _tmp.Dispose();

    private static CatalogItem Bunny() => TestItems.Movie("Big Buck Bunny", tmdb: "10378", id: "bunny") with { Year = 2008 };

    private static CatalogItem Sintel() => TestItems.Movie("Sintel", tmdb: "45745", id: "sintel") with { Year = 2010 };

    private static CatalogItem Numbered(int n) => TestItems.Movie("Movie " + n, tmdb: (100000 + n).ToString(CultureInfo.InvariantCulture), id: "m" + n) with { Year = 2000 };

    private static LocalMedia LocalMovie(string path, string tmdb)
        => new("Movie", path, new Dictionary<string, string> { ["Tmdb"] = tmdb }, null, null, null);

    private LocalItemWatcher NewWatcher(TimeSpan? budget, Func<string, FilePresence>? probe = null)
        => new(
            _sync,
            new MergedIndexStore(() => _tmp.Sub("data")),
            new PlaceholderWriter(),
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
        MoviePlaceholder(SintelDir).ShouldBeFalse();
        _scans.Reported.Clear();
        await _watcher.StartAsync(CancellationToken.None);

        _missing.Add(SintelPath);
        _events.Raise(new LocalItemEvent(true, LocalMovie(SintelPath, "45745")));
        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        MoviePlaceholder(SintelDir).ShouldBeTrue();
        batch.Keys.ShouldBe(1);
        batch.FullSync.ShouldBeFalse();
        _scans.Reported.ShouldBe([Path.Combine(MoviesRoot, SintelDir)]);
        _client.Fetches.ShouldHaveSingleItem();
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

        MoviePlaceholder(SintelDir).ShouldBeFalse();
        _scans.Reported.ShouldBeEmpty();
        _client.Fetches.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task LocalAdded_RemovesPlaceholder()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(SintelDir).ShouldBeTrue();
        _scans.Reported.Clear();
        await _watcher.StartAsync(CancellationToken.None);

        AddLocalSintel();
        _events.Raise(new LocalItemEvent(false, LocalMovie(SintelPath, "45745")));
        await _watcher.ProcessPendingAsync(CancellationToken.None);

        MoviePlaceholder(SintelDir).ShouldBeFalse();
        _scans.Reported.ShouldBe([Path.Combine(MoviesRoot, SintelDir)]);
        _client.Fetches.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task LocalAdded_IdsNotReadyYet_IsResolvedWhenTheWindowIsProcessed()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(SintelDir).ShouldBeTrue();
        await _watcher.StartAsync(CancellationToken.None);

        // At event time the new item has no provider ids; the metadata refresh adds them before the window runs.
        var noIds = new LocalMedia("Movie", SintelPath, new Dictionary<string, string>(), null, null, null);
        LocalMedia? current = noIds;
        _events.Raise(new LocalItemEvent(false, noIds, () => current));
        current = LocalMovie(SintelPath, "45745");
        AddLocalSintel();
        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        batch.Keys.ShouldBe(1);
        MoviePlaceholder(SintelDir).ShouldBeFalse();
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
            batch.Keys.ShouldBe(0);
        }

        calls.ShouldBe(3);
        MoviePlaceholder(SintelDir).ShouldBeTrue();
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

        batch.Keys.ShouldBe(0);
        batch.FullSync.ShouldBeFalse();
        MoviePlaceholder(SintelDir).ShouldBeTrue();
        _scans.Reported.ShouldBeEmpty();
        _client.Fetches.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Storm_Over500_DelegatesToFullSync()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);

        for (var i = 0; i < 501; i++)
        {
            _events.Raise(new LocalItemEvent(true, LocalMovie("/media/Movies/m" + i + "/m.mkv", (100000 + i).ToString(CultureInfo.InvariantCulture))));
        }

        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        batch.FullSync.ShouldBeTrue();
        _client.Fetches.Count.ShouldBe(2);

        // The next window starts empty.
        var next = await _watcher.ProcessPendingAsync(CancellationToken.None);
        next.FullSync.ShouldBeFalse();
        _client.Fetches.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Exactly500Events_AreNotAStorm()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);

        for (var i = 0; i < 500; i++)
        {
            _events.Raise(new LocalItemEvent(true, LocalMovie("/media/Movies/m" + i + "/m.mkv", (100000 + i).ToString(CultureInfo.InvariantCulture))));
        }

        var batch = await _watcher.ProcessPendingAsync(CancellationToken.None);

        batch.FullSync.ShouldBeFalse();
        _client.Fetches.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StartStop_SubscribesStartsTimersAndCleansUp()
    {
        await _watcher.StartAsync(CancellationToken.None);

        _events.Active.ShouldBe(1);
        _timers.Timers.Count.ShouldBe(2);
        _timers.Timers.ShouldContain(t => t.Period == TimeSpan.FromSeconds(15));
        _timers.Timers.ShouldContain(t => t.Period == TimeSpan.FromSeconds(60));

        await _watcher.StopAsync(CancellationToken.None);

        _events.Active.ShouldBe(0);
        foreach (var t in _timers.Timers)
        {
            t.Disposed.ShouldBeTrue();
        }
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

        batch.Keys.ShouldBe(0);
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
        await File.WriteAllTextAsync(blocker, "a file where the folder must go");
        _events.Raise(new LocalItemEvent(true, LocalMovie(SintelPath, "45745")));

        var failed = await _watcher.ProcessPendingAsync(CancellationToken.None);
        failed.Failed.ShouldBeTrue();

        File.Delete(blocker);
        var retried = await _watcher.ProcessPendingAsync(CancellationToken.None);

        retried.Failed.ShouldBeFalse();
        MoviePlaceholder(SintelDir).ShouldBeTrue();
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

        MoviePlaceholder(SintelDir).ShouldBeTrue();
        result.Changed.ShouldBe(1);
        _scans.Reported.ShouldBe([Path.Combine(MoviesRoot, SintelDir)]);

        // Written once: a second pass with the same state changes nothing.
        _scans.Reported.Clear();
        var again = await _watcher.SweepAsync(CancellationToken.None);
        again.Changed.ShouldBe(0);
        _scans.Reported.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_FileBack_RemovesPlaceholder()
    {
        AddLocalSintel();
        _missing.Add(SintelPath);
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(SintelDir).ShouldBeTrue();
        await _watcher.SweepAsync(CancellationToken.None);
        _scans.Reported.Clear();

        _missing.Clear();
        var result = await _watcher.SweepAsync(CancellationToken.None);

        MoviePlaceholder(SintelDir).ShouldBeFalse();
        result.Changed.ShouldBe(1);
        _scans.Reported.ShouldBe([Path.Combine(MoviesRoot, SintelDir)]);
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

        _probed.ShouldBe([SintelPath]);
        result.Candidates.ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_WithoutMergedIndex_DoesNothing()
    {
        AddLocalSintel();

        var result = await _watcher.SweepAsync(CancellationToken.None);

        result.Candidates.ShouldBe(0);
        _probed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_ManyMissing_DelegatesToFullSync()
    {
        var items = new List<CatalogItem>();
        for (var i = 0; i < 501; i++)
        {
            items.Add(Numbered(i));
            _library.Media.Add(LocalMovie("/media/Movies/m" + i + "/m.mkv", (100000 + i).ToString(CultureInfo.InvariantCulture)));
        }

        Offer(items.ToArray());
        await _sync.RunAsync(CancellationToken.None);
        _client.Fetches.ShouldHaveSingleItem();

        for (var i = 0; i < 501; i++)
        {
            _missing.Add("/media/Movies/m" + i + "/m.mkv");
        }

        var result = await _watcher.SweepAsync(CancellationToken.None);

        result.FullSync.ShouldBeTrue();
        _client.Fetches.Count.ShouldBe(2);
        MoviePlaceholder("Movie 7 (2000) [tmdbid-100007]").ShouldBeTrue();
    }

    private void OfferNumbered(params int[] tmdb)
        => Offer(tmdb.Select(t => TestItems.Movie("T" + t, tmdb: t.ToString(CultureInfo.InvariantCulture), id: "t" + t) with { Year = 2000 }).ToArray());

    [Fact]
    public async Task Sweep_RespectsTimeBudget()
    {
        using var release = new ManualResetEventSlim(false);
        const string slow = "/media/Movies/Slow/Slow.mkv";
        _library.Media.Add(LocalMovie(slow, "1"));
        _library.Media.Add(LocalMovie("/media/Movies/Gone/Gone.mkv", "2"));
        OfferNumbered(1, 2);
        await _sync.RunAsync(CancellationToken.None);
        var watcher = NewWatcher(TimeSpan.FromMilliseconds(150), p =>
        {
            if (p == slow)
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return FilePresence.Present;
        });

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var first = await watcher.SweepAsync(CancellationToken.None);

            (sw.Elapsed < TimeSpan.FromSeconds(10)).ShouldBeTrue();
            first.Examined.ShouldBe(0);
            first.Candidates.ShouldBe(2);
            first.Skipped.ShouldBeFalse();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Sweep_HungProbe_DoesNotBlockOtherPaths()
    {
        using var release = new ManualResetEventSlim(false);
        const string slow = "/media/Movies/Slow/Slow.mkv";
        const string gone = "/media/Movies/Gone/Gone.mkv";
        _library.Media.Add(LocalMovie(slow, "1"));
        _library.Media.Add(LocalMovie(gone, "2"));
        OfferNumbered(1, 2);
        await _sync.RunAsync(CancellationToken.None);
        var slowCalls = 0;
        var watcher = NewWatcher(TimeSpan.FromMilliseconds(150), p =>
        {
            if (p == slow)
            {
                Interlocked.Increment(ref slowCalls);
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return _missing.Contains(p) ? FilePresence.Missing : FilePresence.Present;
        });

        try
        {
            await watcher.SweepAsync(CancellationToken.None);
            _missing.Add(gone);

            // The cursor moved past the stuck item and the stuck path is not probed again while its probe hangs.
            var second = await watcher.SweepAsync(CancellationToken.None);

            second.Skipped.ShouldBeFalse();
            second.Changed.ShouldBe(1);
            MoviePlaceholder("T2 (2000) [tmdbid-2]").ShouldBeTrue();
            Volatile.Read(ref slowCalls).ShouldBe(1);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Sweep_HungMount_QuarantinesOnlyItsLocation()
    {
        using var release = new ManualResetEventSlim(false);
        const string gone = "/media/Movies/Gone/g.mkv";
        _library.Locations.Add("/mnt/dead/Movies");
        _library.Locations.Add("/media/Movies");
        for (var i = 1; i <= 5; i++)
        {
            _library.Media.Add(LocalMovie("/mnt/dead/Movies/D" + i + "/d.mkv", i.ToString(CultureInfo.InvariantCulture)));
        }

        _library.Media.Add(LocalMovie(gone, "9"));
        OfferNumbered(1, 2, 3, 4, 5, 9);
        await _sync.RunAsync(CancellationToken.None);
        var deadProbes = 0;
        var watcher = NewWatcher(TimeSpan.FromMilliseconds(100), p =>
        {
            if (p.StartsWith("/mnt/dead/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref deadProbes);
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return _missing.Contains(p) ? FilePresence.Missing : FilePresence.Present;
        });

        try
        {
            await watcher.SweepAsync(CancellationToken.None);
            _missing.Add(gone);
            for (var i = 0; i < 8; i++)
            {
                ((await watcher.SweepAsync(CancellationToken.None)).Skipped).ShouldBeFalse();
            }

            MoviePlaceholder("T9 (2000) [tmdbid-9]").ShouldBeTrue();
            Volatile.Read(ref deadProbes).ShouldBe(1);
            MoviePlaceholder("T2 (2000) [tmdbid-2]").ShouldBeFalse();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Sweep_QuarantinedLocation_RecoversWhenProbeReturns()
    {
        using var release = new ManualResetEventSlim(false);
        _library.Locations.Add("/mnt/dead/Movies");
        for (var i = 1; i <= 3; i++)
        {
            _library.Media.Add(LocalMovie("/mnt/dead/Movies/D" + i + "/d.mkv", i.ToString(CultureInfo.InvariantCulture)));
        }

        OfferNumbered(1, 2, 3);
        await _sync.RunAsync(CancellationToken.None);
        var deadProbes = 0;
        var watcher = NewWatcher(TimeSpan.FromMilliseconds(100), p =>
        {
            Interlocked.Increment(ref deadProbes);
            release.Wait(TimeSpan.FromSeconds(30));
            return _missing.Contains(p) ? FilePresence.Missing : FilePresence.Present;
        });

        try
        {
            await watcher.SweepAsync(CancellationToken.None);
            await watcher.SweepAsync(CancellationToken.None);
            Volatile.Read(ref deadProbes).ShouldBe(1);

            _missing.Add("/mnt/dead/Movies/D2/d.mkv");
            release.Set();
            for (var i = 0; i < 100 && !MoviePlaceholder("T2 (2000) [tmdbid-2]"); i++)
            {
                await Task.Delay(30);
                await watcher.SweepAsync(CancellationToken.None);
            }

            MoviePlaceholder("T2 (2000) [tmdbid-2]").ShouldBeTrue();
            (Volatile.Read(ref deadProbes) > 1).ShouldBeTrue();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Sweep_CandidatesCachedBetweenPasses()
    {
        AddLocalSintel();
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        var reads = _library.Reads;

        await _watcher.SweepAsync(CancellationToken.None);
        await _watcher.SweepAsync(CancellationToken.None);
        _library.Reads.ShouldBe(reads + 1);

        // A change found by the sweep is reconciled without reading the library again.
        _missing.Add(SintelPath);
        var result = await _watcher.SweepAsync(CancellationToken.None);
        result.Changed.ShouldBe(1);
        MoviePlaceholder(SintelDir).ShouldBeTrue();
        _library.Reads.ShouldBe(reads + 1);
    }

    [Fact]
    public async Task Sweep_MergedIndexChanged_RebuildsCandidates()
    {
        AddLocalSintel();
        Offer(Bunny());
        await _sync.RunAsync(CancellationToken.None);
        ((await _watcher.SweepAsync(CancellationToken.None)).Candidates).ShouldBe(0);

        Offer(Bunny(), Sintel());
        _clock.Now += TimeSpan.FromMinutes(1);
        await _sync.RunAsync(CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(_tmp.Sub("data"), MergedIndexStore.FileName), DateTime.UtcNow.AddMinutes(5));

        ((await _watcher.SweepAsync(CancellationToken.None)).Candidates).ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_RefreshesCandidatesPeriodically()
    {
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        ((await _watcher.SweepAsync(CancellationToken.None)).Candidates).ShouldBe(0);

        // Added without a usable event: picked up by the periodic refresh.
        AddLocalSintel();
        ((await _watcher.SweepAsync(CancellationToken.None)).Candidates).ShouldBe(0);
        _timers.Now += LocalItemWatcher.CandidateRefreshInterval + TimeSpan.FromSeconds(1);
        var result = await _watcher.SweepAsync(CancellationToken.None);

        result.Candidates.ShouldBe(1);
        MoviePlaceholder(SintelDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweep_PresentLocalWithLeftoverPlaceholder_RemovesIt()
    {
        AddLocalSintel();
        _missing.Add(SintelPath);
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        MoviePlaceholder(SintelDir).ShouldBeTrue();

        // A fresh watcher (plugin restart) that never saw the file missing.
        _missing.Clear();
        var result = await NewWatcher(null).SweepAsync(CancellationToken.None);

        result.Changed.ShouldBe(1);
        MoviePlaceholder(SintelDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweep_EventPlaceholderThenLocalBackWithoutUsableEvent_RemovesIt()
    {
        AddLocalSintel();
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        await _watcher.StartAsync(CancellationToken.None);
        await _watcher.SweepAsync(CancellationToken.None);

        // Removed: the event path writes the placeholder.
        _library.Media.Clear();
        _missing.Add(SintelPath);
        _events.Raise(new LocalItemEvent(true, LocalMovie(SintelPath, "45745")));
        await _watcher.ProcessPendingAsync(CancellationToken.None);
        MoviePlaceholder(SintelDir).ShouldBeTrue();

        // Back, but its ItemAdded event never had usable ids.
        _missing.Clear();
        AddLocalSintel();
        await _watcher.SweepAsync(CancellationToken.None);

        MoviePlaceholder(SintelDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Fire_AfterDispose_DoesNotThrow()
    {
        await _watcher.StartAsync(CancellationToken.None);
        _watcher.Dispose();

        foreach (var timer in _timers.Timers)
        {
            timer.Fire();
        }
    }

    [Fact]
    public async Task StopAsync_CancelsAndAwaitsInFlightSweep()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        AddLocalSintel();
        Offer(Sintel());
        await _sync.RunAsync(CancellationToken.None);
        var watcher = NewWatcher(TimeSpan.FromSeconds(30), _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return FilePresence.Present;
        });

        try
        {
            await watcher.StartAsync(CancellationToken.None);
            _timers.Timers.Last(t => t.Period == TimeSpan.FromSeconds(60)).Fire();
            entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            watcher.RunningPasses.ShouldBe(1);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var stopToken = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await watcher.StopAsync(stopToken.Token);

            (sw.Elapsed < TimeSpan.FromSeconds(5)).ShouldBeTrue();
            watcher.RunningPasses.ShouldBe(0);
        }
        finally
        {
            release.Set();
        }
    }
}
