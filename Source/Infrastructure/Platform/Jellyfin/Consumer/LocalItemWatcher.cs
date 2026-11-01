using System.Collections.Concurrent;
using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Placeholders;
using JellyShare.Jellyfin.Provider;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Consumer;

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

    /// <summary>The sweep candidate list is rebuilt (one library read) at least this often.</summary>
    public static readonly TimeSpan CandidateRefreshInterval = TimeSpan.FromMinutes(10);

    private const int MaxAttempts = 3;

    private const int NotChecked = 0;
    private const int PresentState = 1;
    private const int MissingState = 2;
    private const int UnknownState = 3;
    private const int InFlight = 4;
    private const int Stuck = 5;

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
    private readonly ConcurrentDictionary<string, byte> _probing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TaskCompletionSource, byte> _passes = new();
    private readonly ConcurrentDictionary<string, string> _quarantine = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FilePresence> _late = new(StringComparer.Ordinal);
    private readonly HashSet<string> _slowWarned = new(StringComparer.Ordinal);

    private int _disposed;
    private int _windowEvents;
    private bool _running;
    private IDisposable? _subscription;
    private ITimer? _processTimer;
    private ITimer? _sweepTimer;
    private int _cursor;
    private int _dirty;
    private List<Candidate>? _candidates;
    private IReadOnlyDictionary<string, LocalSeries> _series = new Dictionary<string, LocalSeries>();
    private (DateTime WriteUtc, long Length) _indexStamp;
    private DateTimeOffset _builtUtc;

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

    /// <summary>Gets the number of timer-started passes still running.</summary>
    internal int RunningPasses => _passes.Count;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
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

        // In-flight passes observe the cancelled token; wait for them, bounded by the host's stop token.
        var running = _passes.Keys.Select(p => p.Task).ToArray();
        if (running.Length > 0)
        {
            try
            {
                await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("JellyShare watcher: stopped before {Count} background passes finished", running.Length);
            }
        }
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

        // _cts is deliberately not disposed: a timer callback racing with Dispose may still read its token.
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
    /// One presence sweep pass: stat the cached file paths of local movies/episodes whose keys appear in the merged index,
    /// under a time budget, and reconcile the titles whose presence changed. The candidate list (one library read) is only
    /// rebuilt when the merged index changes, after library events, or every <see cref="CandidateRefreshInterval"/>.
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
            var cfg = _config.Current;
            var candidates = EnsureCandidates(cfg);
            var n = candidates.Count;
            if (n == 0)
            {
                return new SweepResult(0, 0, 0, false);
            }

            var start = _cursor % n;
            var ordered = new Candidate[n];
            for (var i = 0; i < n; i++)
            {
                ordered[i] = candidates[(start + i) % n];
            }

            var states = await ProbeAsync(ordered, ct).ConfigureAwait(false);

            var examined = 0;
            while (examined < n && states[examined] is not (NotChecked or Stuck))
            {
                examined++;
            }

            // Move past an item whose check is still running (it counts as present), so a stuck path never pins the cursor.
            _cursor = (start + Math.Min(n, examined + 1)) % n;

            var seen = new Dictionary<string, (bool Present, bool Unchecked, bool Missing)>(StringComparer.Ordinal);
            var presentMovies = new HashSet<string>(StringComparer.Ordinal);
            var presentEpisodes = new HashSet<string>(StringComparer.Ordinal);
            int unverified = 0, missing = 0;
            for (var i = 0; i < n; i++)
            {
                var state = states[i];
                if (state == MissingState)
                {
                    missing++;
                }
                else
                {
                    if (state != PresentState)
                    {
                        unverified++;
                    }

                    (ordered[i].Type == "Movie" ? presentMovies : presentEpisodes).UnionWith(ordered[i].Keys);
                }

                foreach (var key in ordered[i].Keys)
                {
                    seen.TryGetValue(key, out var info);
                    info = state switch
                    {
                        NotChecked or InFlight or Stuck => info with { Unchecked = true },
                        MissingState => info with { Missing = true },
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
            var fullSync = changed.Count > StormThreshold;
            if (fullSync)
            {
                _logger.LogInformation("JellyShare sweep: presence of {Count} titles changed, running a full sync", changed.Count);
                await _sync.RunAsync(ct).ConfigureAwait(false);
            }
            else if (changed.Count > 0)
            {
                var local = new LocalIndexResult(presentMovies, presentEpisodes, _series, unverified, missing);
                var report = await _sync.ReconcileAsync(changed, local, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "JellyShare sweep: presence of {Count} titles changed, {Written} placeholder files written, {Deleted} deleted",
                    changed.Count,
                    report.Written,
                    report.Deleted);
            }

            _missingKeys.Clear();
            _missingKeys.UnionWith(nowMissing);
            return new SweepResult(n, examined, changed.Count, fullSync);
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private async Task<int[]> ProbeAsync(Candidate[] ordered, CancellationToken ct)
    {
        var n = ordered.Length;
        var states = new int[n];
        var current = -1;
        var stop = 0;
        var probe = _probe;
        var probing = _probing;
        var quarantine = _quarantine;
        var late = _late;

        // A fresh worker per pass. A check stuck on a hung mount is abandoned (its thread finishes when the OS call
        // returns) and its library location is quarantined: nothing under it is probed (counted as present, never as
        // missing) until that stuck check returns, so one dead mount costs at most one thread and never blocks others.
        var worker = Task.Factory.StartNew(
            () =>
            {
                for (var i = 0; i < n && Volatile.Read(ref stop) == 0; i++)
                {
                    var candidate = ordered[i];
                    var path = candidate.Path;
                    if (late.TryRemove(path, out var known))
                    {
                        Volatile.Write(ref states[i], ToState(known));
                        continue;
                    }

                    if (quarantine.ContainsKey(candidate.Location) || !probing.TryAdd(path, 0))
                    {
                        Volatile.Write(ref states[i], InFlight);
                        continue;
                    }

                    Volatile.Write(ref current, i);
                    FilePresence presence;
                    try
                    {
                        presence = probe(path);
                    }
                    catch (Exception)
                    {
                        presence = FilePresence.Unknown;
                    }
                    finally
                    {
                        probing.TryRemove(path, out _);
                        quarantine.TryRemove(new KeyValuePair<string, string>(candidate.Location, path));
                    }

                    if (Volatile.Read(ref stop) != 0)
                    {
                        // Arrived after the pass gave up on it: keep it for the next pass.
                        late[path] = presence;
                    }

                    Volatile.Write(ref states[i], ToState(presence));
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var timedOut = false;
        try
        {
            await worker.WaitAsync(_budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        finally
        {
            Volatile.Write(ref stop, 1);
        }

        var snapshot = new int[n];
        for (var i = 0; i < n; i++)
        {
            snapshot[i] = Volatile.Read(ref states[i]);
        }

        if (timedOut && !worker.IsCompleted)
        {
            var stuck = Volatile.Read(ref current);
            if (stuck >= 0 && snapshot[stuck] == NotChecked)
            {
                snapshot[stuck] = Stuck;
                var candidate = ordered[stuck];
                quarantine[candidate.Location] = candidate.Path;
                if (!probing.ContainsKey(candidate.Path))
                {
                    // The check returned between the timeout and here.
                    quarantine.TryRemove(new KeyValuePair<string, string>(candidate.Location, candidate.Path));
                }
                else if (_slowWarned.Add(candidate.Location))
                {
                    _logger.LogWarning(
                        "JellyShare sweep: a file check under {Location} took longer than {Budget} s (slow or hung mount); titles there are treated as present until it answers",
                        candidate.Location,
                        _budget.TotalSeconds);
                }
            }
        }

        return snapshot;
    }

    private static int ToState(FilePresence presence) => presence switch
    {
        FilePresence.Present => PresentState,
        FilePresence.Missing => MissingState,
        _ => UnknownState,
    };

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static string LocationOf(string path, IReadOnlyList<string> locations)
    {
        var normalized = Normalize(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? best = null;
        foreach (var location in locations)
        {
            if (location.Length > 0
                && normalized.Length > location.Length
                && normalized[location.Length] == '/'
                && normalized.StartsWith(location, comparison)
                && (best is null || location.Length > best.Length))
            {
                best = location;
            }
        }

        if (best is not null)
        {
            return best;
        }

        var slash = normalized.LastIndexOf('/');
        return slash > 0 ? normalized[..slash] : normalized;
    }

    private List<Candidate> EnsureCandidates(PluginConfiguration cfg)
    {
        var info = new FileInfo(_sync.MergedIndexPath);
        if (!info.Exists)
        {
            _candidates = [];
            _indexStamp = default;
            _missingKeys.Clear();
            return _candidates;
        }

        var stamp = (info.LastWriteTimeUtc, info.Length);
        var now = _clock.GetUtcNow();
        var dirty = Interlocked.Exchange(ref _dirty, 0) != 0;
        if (_candidates is not null && !dirty && stamp == _indexStamp && now - _builtUtc < CandidateRefreshInterval)
        {
            return _candidates;
        }

        var file = MergedIndexStore.Load(info.FullName);
        var canonicalOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in file?.Entries ?? [])
        {
            if (entry.Sources.Count == 0)
            {
                continue;
            }

            foreach (var key in entry.Keys)
            {
                canonicalOf.TryAdd(key, entry.CanonicalKey);
            }
        }

        var candidates = new List<Candidate>();
        IReadOnlyDictionary<string, LocalSeries> series = new Dictionary<string, LocalSeries>();
        if (canonicalOf.Count > 0)
        {
            var snapshot = _library.Read();
            series = LocalIndex.BuildSeries(snapshot.Series, cfg);
            var locations = snapshot.Locations.Where(l => !string.IsNullOrWhiteSpace(l)).Select(Normalize).ToList();
            foreach (var media in snapshot.Media)
            {
                if (string.IsNullOrWhiteSpace(media.Path)
                    || media.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                    || PlaceholderPaths.IsPlaceholder(media.Path, cfg))
                {
                    continue;
                }

                var keys = LocalIndex.KeysFor(media).Where(canonicalOf.ContainsKey).ToArray();
                if (keys.Length > 0)
                {
                    candidates.Add(new Candidate(media.Type, media.Path, keys, keys.Select(k => canonicalOf[k]).Distinct().ToArray(), LocationOf(media.Path, locations)));
                }
            }
        }

        // Seed "missing" from the placeholders on disk: a title that has a placeholder is treated as previously missing,
        // so a present local copy next to a leftover placeholder (restart, dropped event) is reconciled away.
        var placeholders = ReadPlaceholderKeys(cfg);
        _missingKeys.Clear();
        foreach (var candidate in candidates)
        {
            if (candidate.Canonicals.Any(placeholders.Contains))
            {
                _missingKeys.UnionWith(candidate.Keys);
            }
        }

        _candidates = candidates;
        _series = series;
        _indexStamp = stamp;
        _builtUtc = now;
        return candidates;
    }

    private HashSet<string> ReadPlaceholderKeys(PluginConfiguration cfg)
    {
        const string Marker = "/JellyShare/v1/play/";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in new[] { cfg.MoviesPlaceholderPath, cfg.ShowsPlaceholderPath })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var strm in Directory.EnumerateFiles(root, "*.strm", options))
                {
                    try
                    {
                        var content = File.ReadAllText(strm).Trim();
                        var at = content.LastIndexOf(Marker, StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            keys.Add(Uri.UnescapeDataString(content[(at + Marker.Length)..]));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("JellyShare sweep: placeholder folder unreadable: {Error}", ex.GetType().Name);
            }
        }

        return keys;
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
                Volatile.Write(ref _dirty, 1);
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
            else
            {
                // Given up on the event; let the next sweep re-read the library instead.
                Volatile.Write(ref _dirty, 1);
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
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _cts.IsCancellationRequested)
            {
                return;
            }

            var token = _cts.Token;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _passes[done] = 0;
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
                    finally
                    {
                        _passes.TryRemove(done, out _);
                        done.TrySetResult();
                    }
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Timer callbacks must never throw (shutdown races).
            _logger.LogDebug(ex, "JellyShare watcher: background pass not started");
        }
    }

    private sealed record Candidate(string Type, string Path, string[] Keys, string[] Canonicals, string Location);
}
