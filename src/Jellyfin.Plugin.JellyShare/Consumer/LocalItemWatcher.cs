using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Provider;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>A local library item was added or removed.</summary>
/// <param name="Removed"><c>true</c> for a removal, <c>false</c> for an addition.</param>
/// <param name="Media">The item, as far as its keys are concerned, at the time of the event.</param>
/// <param name="Refresh">
/// Re-reads the item later (returns <c>null</c> if it is gone). Used for added items whose ids or season/episode
/// numbers are not set yet when the event fires.
/// </param>
public sealed record LocalItemEvent(bool Removed, LocalMedia Media, Func<LocalMedia?>? Refresh = null);

/// <summary>Source of library item-added / item-removed notifications.</summary>
public interface ILocalItemEvents
{
    /// <summary>Starts delivering events to <paramref name="handler"/>. Handlers must be quick and must not throw.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A token that stops the delivery when disposed.</returns>
    IDisposable Subscribe(Action<LocalItemEvent> handler);
}

/// <summary>What one processed event window did.</summary>
/// <param name="Events">Accepted events in the window.</param>
/// <param name="Keys">Distinct keys handed to the reconcile.</param>
/// <param name="FullSync">The window was a storm and went to a full sync instead.</param>
/// <param name="Failed">Processing failed; the keys are retried in the next window.</param>
public sealed record WatcherBatch(int Events, int Keys, bool FullSync, bool Failed = false);

/// <summary>What one presence sweep pass did.</summary>
/// <param name="Candidates">Local movies/episodes whose keys appear in the merged index.</param>
/// <param name="Examined">Candidates whose file was checked within the time budget.</param>
/// <param name="Changed">Keys whose presence changed since the last pass.</param>
/// <param name="FullSync">The change set was a storm and went to a full sync instead.</param>
/// <param name="Skipped">The pass did not run (still busy).</param>
public sealed record SweepResult(int Candidates, int Examined, int Changed, bool FullSync, bool Skipped = false);

/// <summary>
/// Keeps placeholders in line with the owner's own files: a vanished local file gets its peer placeholder, a returning
/// one loses it (local always wins). Two inputs: library item-removed/added events (queued, processed every 15 s) and a
/// presence sweep (default every 60 s) that stats the files of local titles a peer also offers — needed because an
/// offline drive produces no library events. Both only reconcile the affected titles; a storm (&gt; 500 events or
/// changed keys) falls back to one full sync.
/// </summary>
public sealed class LocalItemWatcher : IHostedService, IDisposable
{
    /// <summary>Interval between event-window runs.</summary>
    public static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(15);

    /// <summary>Default time budget of one sweep pass.</summary>
    public static readonly TimeSpan DefaultSweepBudget = TimeSpan.FromSeconds(10);

    /// <summary>More events or changed keys than this in one window go to a full sync.</summary>
    public const int StormThreshold = 500;

    private const int MaxAttempts = 3;

    private readonly SyncService _sync;
    private readonly ILocalItemEvents _events;
    private readonly ILocalLibrary _library;
    private readonly IConfigProvider _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<LocalItemWatcher> _logger;
    private readonly Func<string, FilePresence> _probe;
    private readonly TimeSpan _budget;

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);
    private readonly List<(Func<LocalMedia?> Refresh, int Attempts)> _deferred = new();
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly HashSet<string> _missingKeys = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private int _disposed;
    private int _windowEvents;
    private bool _running;
    private IDisposable? _subscription;
    private ITimer? _processTimer;
    private ITimer? _sweepTimer;
    private Task? _probeTask;
    private int _cursor;

    /// <summary>Initializes a new instance of the <see cref="LocalItemWatcher"/> class.</summary>
    /// <param name="sync">The sync service (reconcile and full sync).</param>
    /// <param name="events">The library event source.</param>
    /// <param name="library">The library reader (sweep candidates).</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="clock">The clock used to create the timers.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="probe">File probe; defaults to <see cref="LocalIndex.ProbeFile"/>.</param>
    /// <param name="sweepBudget">Time budget of one sweep pass; defaults to <see cref="DefaultSweepBudget"/>.</param>
    public LocalItemWatcher(
        SyncService sync,
        ILocalItemEvents events,
        ILocalLibrary library,
        IConfigProvider config,
        TimeProvider clock,
        ILogger<LocalItemWatcher> logger,
        Func<string, FilePresence>? probe = null,
        TimeSpan? sweepBudget = null)
    {
        _sync = sync;
        _events = events;
        _library = library;
        _config = config;
        _clock = clock;
        _logger = logger;
        _probe = probe ?? LocalIndex.ProbeFile;
        _budget = sweepBudget ?? DefaultSweepBudget;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }

            _running = true;
            _subscription = _events.Subscribe(OnEvent);
        }

        var sweep = TimeSpan.FromSeconds(Math.Clamp(_config.Current.PresenceSweepSeconds, 15, 3600));
        _processTimer = _clock.CreateTimer(_ => Fire(ProcessPendingAsync), null, ProcessInterval, ProcessInterval);
        _sweepTimer = _clock.CreateTimer(_ => Fire(SweepAsync), null, sweep, sweep);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        IDisposable? subscription;
        lock (_lock)
        {
            _running = false;
            subscription = _subscription;
            _subscription = null;
        }

        subscription?.Dispose();
        _processTimer?.Dispose();
        _sweepTimer?.Dispose();
        _processTimer = null;
        _sweepTimer = null;
        _cts.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The container disposes this singleton once per registration (class and hosted service), after StopAsync.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _processTimer?.Dispose();
        _sweepTimer?.Dispose();
        _subscription?.Dispose();
        _cts.Dispose();
    }

    /// <summary>Processes the queued events: reconcile the affected titles, or one full sync for a storm.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What the window did.</returns>
    public async Task<WatcherBatch> ProcessPendingAsync(CancellationToken ct)
    {
        if (!await _processGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new WatcherBatch(0, 0, false);
        }

        try
        {
            Dictionary<string, int> keys;
            List<(Func<LocalMedia?> Refresh, int Attempts)> deferred;
            int events;
            lock (_lock)
            {
                events = _windowEvents;
                keys = new Dictionary<string, int>(_pending, StringComparer.Ordinal);
                deferred = _deferred.ToList();
                _pending.Clear();
                _deferred.Clear();
                _windowEvents = 0;
            }

            if (events > StormThreshold)
            {
                _logger.LogInformation("JellyShare watcher: {Events} library events in one window, running a full sync", events);
                await _sync.RunAsync(ct).ConfigureAwait(false);
                return new WatcherBatch(events, 0, true);
            }

            if (deferred.Count > 0)
            {
                ResolveDeferred(deferred, keys);
            }

            if (keys.Count == 0)
            {
                return new WatcherBatch(events, 0, false);
            }

            try
            {
                var report = await _sync.ReconcileAsync(keys.Keys.ToList(), ct).ConfigureAwait(false);
                if (report.Written + report.Deleted > 0)
                {
                    _logger.LogInformation(
                        "JellyShare watcher: {Keys} changed titles, {Written} placeholder files written, {Deleted} deleted",
                        keys.Count,
                        report.Written,
                        report.Deleted);
                }

                return new WatcherBatch(events, keys.Count, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "JellyShare watcher: reconciling {Keys} titles failed", keys.Count);
                Requeue(keys);
                return new WatcherBatch(events, keys.Count, false, true);
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <summary>
    /// One presence sweep pass: stat the files of local movies/episodes whose keys appear in the merged index, under a
    /// time budget, and reconcile the titles whose presence changed since the previous pass.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What the pass did.</returns>
    public async Task<SweepResult> SweepAsync(CancellationToken ct)
    {
        if (!await _sweepGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new SweepResult(0, 0, 0, false, true);
        }

        try
        {
            // A probe stuck on a hung mount keeps its thread; never start a second one behind it.
            if (_probeTask is { IsCompleted: false })
            {
                return new SweepResult(0, 0, 0, false, true);
            }
            var file = MergedIndexStore.Load(_sync.MergedIndexPath);
            var indexKeys = new HashSet<string>(StringComparer.Ordinal);
            if (file is not null)
            {
                foreach (var entry in file.Entries.Where(e => e.Sources.Count > 0))
                {
                    indexKeys.UnionWith(entry.Keys);
                }
            }

            var cfg = _config.Current;
            var candidates = new List<(LocalMedia Media, List<string> Keys)>();
            if (indexKeys.Count > 0)
            {
                foreach (var media in _library.Read().Media)
                {
                    if (string.IsNullOrWhiteSpace(media.Path)
                        || media.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                        || PlaceholderPaths.IsPlaceholder(media.Path, cfg))
                    {
                        continue;
                    }

                    var keys = LocalIndex.KeysFor(media).Where(indexKeys.Contains).ToList();
                    if (keys.Count > 0)
                    {
                        candidates.Add((media, keys));
                    }
                }
            }

            var n = candidates.Count;
            if (n == 0)
            {
                _missingKeys.Clear();
                return new SweepResult(0, 0, 0, false);
            }

            var start = _cursor % n;
            var ordered = new (LocalMedia Media, List<string> Keys)[n];
            for (var i = 0; i < n; i++)
            {
                ordered[i] = candidates[(start + i) % n];
            }

            var states = new int[n];
            var stop = 0;
            var probe = _probe;
            var check = Task.Run(
                () =>
                {
                    for (var i = 0; i < n && Volatile.Read(ref stop) == 0; i++)
                    {
                        FilePresence presence;
                        try
                        {
                            presence = probe(ordered[i].Media.Path!);
                        }
                        catch (Exception)
                        {
                            presence = FilePresence.Unknown;
                        }

                        Volatile.Write(ref states[i], presence == FilePresence.Missing ? 2 : 1);
                    }
                },
                CancellationToken.None);
            _probeTask = check;

            try
            {
                await check.WaitAsync(_budget, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Volatile.Write(ref stop, 1);
            }

            var snapshot = new int[n];
            var examined = 0;
            for (var i = 0; i < n; i++)
            {
                snapshot[i] = Volatile.Read(ref states[i]);
                if (snapshot[i] != 0 && examined == i)
                {
                    examined++;
                }
            }

            _cursor = (start + examined) % n;

            var seen = new Dictionary<string, (bool Present, bool Unchecked, bool Missing)>(StringComparer.Ordinal);
            for (var i = 0; i < n; i++)
            {
                foreach (var key in ordered[i].Keys)
                {
                    seen.TryGetValue(key, out var info);
                    info = snapshot[i] switch
                    {
                        0 => info with { Unchecked = true },
                        2 => info with { Missing = true },
                        _ => info with { Present = true },
                    };
                    seen[key] = info;
                }
            }

            var nowMissing = new HashSet<string>(_missingKeys, StringComparer.Ordinal);
            nowMissing.IntersectWith(seen.Keys);
            foreach (var (key, info) in seen)
            {
                if (info.Present)
                {
                    nowMissing.Remove(key);
                }
                else if (info.Missing && !info.Unchecked)
                {
                    nowMissing.Add(key);
                }
            }
            var changed = nowMissing.Except(_missingKeys).Concat(_missingKeys.Intersect(seen.Keys).Except(nowMissing)).ToList();
            if (changed.Count > StormThreshold)
            {
                _logger.LogInformation("JellyShare sweep: presence of {Count} titles changed, running a full sync", changed.Count);
                await _sync.RunAsync(ct).ConfigureAwait(false);
            }
            else if (changed.Count > 0)
            {
                var report = await _sync.ReconcileAsync(changed, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "JellyShare sweep: presence of {Count} titles changed, {Written} placeholder files written, {Deleted} deleted",
                    changed.Count,
                    report.Written,
                    report.Deleted);
            }

            _missingKeys.Clear();
            _missingKeys.UnionWith(nowMissing);
            return new SweepResult(n, examined, changed.Count, changed.Count > StormThreshold);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private void OnEvent(LocalItemEvent e)
    {
        try
        {
            var media = e.Media;
            if (media is null
                || media.Type is not ("Movie" or "Episode")
                || string.IsNullOrWhiteSpace(media.Path)
                || media.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || PlaceholderPaths.IsPlaceholder(media.Path, _config.Current))
            {
                return;
            }

            // A just-added item often has no usable ids / season / episode numbers yet (they are filled in by the
            // metadata refresh that follows). Such an event is re-read when the window is processed.
            var keys = LocalIndex.KeysFor(media);
            var defer = keys.Count == 0 && !e.Removed && e.Refresh is not null;
            if (keys.Count == 0 && !defer)
            {
                return;
            }

            lock (_lock)
            {
                if (!_running)
                {
                    return;
                }

                _windowEvents++;
                if (_windowEvents > StormThreshold)
                {
                    // Only the count matters from here on; don't keep growing the queues.
                    _pending.Clear();
                    _deferred.Clear();
                    return;
                }

                if (defer)
                {
                    _deferred.Add((e.Refresh!, 0));
                    return;
                }

                foreach (var key in keys)
                {
                    _pending.TryAdd(key, 0);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyShare watcher: could not queue a library event");
        }
    }

    private void ResolveDeferred(List<(Func<LocalMedia?> Refresh, int Attempts)> deferred, Dictionary<string, int> keys)
    {
        foreach (var (refresh, attempts) in deferred)
        {
            IReadOnlyList<string> resolved = [];
            try
            {
                var media = refresh();
                if (media is not null
                    && media.Type is "Movie" or "Episode"
                    && !string.IsNullOrWhiteSpace(media.Path)
                    && !PlaceholderPaths.IsPlaceholder(media.Path, _config.Current))
                {
                    resolved = LocalIndex.KeysFor(media);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JellyShare watcher: could not re-read an added item");
            }

            if (resolved.Count > 0)
            {
                foreach (var key in resolved)
                {
                    keys.TryAdd(key, 0);
                }
            }
            else if (attempts + 1 < MaxAttempts)
            {
                lock (_lock)
                {
                    _deferred.Add((refresh, attempts + 1));
                }
            }
        }
    }

    private void Requeue(Dictionary<string, int> keys)
    {
        lock (_lock)
        {
            foreach (var (key, attempts) in keys)
            {
                if (attempts + 1 < MaxAttempts && _windowEvents <= StormThreshold)
                {
                    _pending.TryAdd(key, attempts + 1);
                }
            }
        }
    }

    private void Fire<T>(Func<CancellationToken, Task<T>> action)
    {
        var token = _cts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await action(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "JellyShare watcher: background pass failed");
                }
            },
            CancellationToken.None);
    }
}
