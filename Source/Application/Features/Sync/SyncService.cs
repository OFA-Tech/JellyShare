using System.Diagnostics.CodeAnalysis;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Storage;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Storage;
using JellyShare.Domain.Models.Sync;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Sync;

/// <summary>Pulls peer catalogs, merges them and maintains the placeholder folders.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The gate never exposes a wait handle, so it holds nothing to release; disposing it at host shutdown would race a background run.")]
public sealed class SyncService : ISyncRunner
{
    /// <summary>A failing peer's last good catalog is used for at most this long.</summary>
    public static readonly TimeSpan FailureGrace = TimeSpan.FromDays(7);

    /// <summary>Maximum directories reported for scanning per run.</summary>
    public const int MaxScansPerRun = 200;

    private const string TypeOnlyLogging = "Only the exception type is logged, never its message (paths, peer URLs, tokens) or stack.";

    private readonly ISettingsProvider _config;
    private readonly IPeerClient _client;
    private readonly ILocalIndex _localIndex;
    private readonly IScanReporter _scans;
    private readonly IRelayBase _relayBase;
    private readonly IPeerStateStore _peerStates;
    private readonly IMergedIndexStore _mergedIndex;
    private readonly ISyncReportStore _reports;
    private readonly IPlaceholderLedgerStore _ledgerStore;
    private readonly IPendingScanStore _pendingScans;
    private readonly IPlaceholderFiles _writer;
    private readonly TimeProvider _clock;
    private readonly ILogger<SyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lastLock = new();
    private bool _lastLoaded;
    private readonly object _backgroundLock = new();
    private readonly Dictionary<string, int> _retryCursor = new(StringComparer.Ordinal);
    private int _active;
    private bool _queued;
    private Task? _background;
    private List<MergedEntry>? _merged;
    private FileStamp _mergedStamp;
    private PlaceholderLedger? _ledger;
    private FileStamp _ledgerStamp;

    /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="localIndex">The local index.</param>
    /// <param name="scans">The scan reporter.</param>
    /// <param name="relayBase">The relay base URL.</param>
    /// <param name="peerStates">The persisted peer states.</param>
    /// <param name="mergedIndex">The persisted merged index.</param>
    /// <param name="reports">The persisted last sync report.</param>
    /// <param name="ledgerStore">The persisted placeholder ledger.</param>
    /// <param name="pendingScans">The persisted pending scan list.</param>
    /// <param name="placeholders">The placeholder roots on disk.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SyncService(
        ISettingsProvider config,
        IPeerClient client,
        ILocalIndex localIndex,
        IScanReporter scans,
        IRelayBase relayBase,
        IPeerStateStore peerStates,
        IMergedIndexStore mergedIndex,
        ISyncReportStore reports,
        IPlaceholderLedgerStore ledgerStore,
        IPendingScanStore pendingScans,
        IPlaceholderFiles placeholders,
        TimeProvider clock,
        ILogger<SyncService> logger)
    {
        _config = config;
        _client = client;
        _localIndex = localIndex;
        _scans = scans;
        _relayBase = relayBase;
        _peerStates = peerStates;
        _mergedIndex = mergedIndex;
        _reports = reports;
        _ledgerStore = ledgerStore;
        _pendingScans = pendingScans;
        _writer = placeholders;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Gets the report of the last finished run, if any (read from the data folder after a restart).</summary>
    public SyncReport? LastReport
    {
        get
        {
            lock (_lastLock)
            {
                if (field is null && !_lastLoaded)
                {
                    _lastLoaded = true;
                    try
                    {
                        field = _reports.Load();
                    }
                    catch (InvalidOperationException)
                    {
                        _lastLoaded = false;
                    }
                }

                return field;
            }
        }

        private set
        {
            lock (_lastLock)
            {
                field = value;
                _lastLoaded = true;
            }
        }
    }

    /// <summary>Maximum folders per full sync whose missing images are fetched again.</summary>
    public const int MaxImageRetriesPerRun = 50;

    /// <summary>Folders whose images are downloaded at the same time.</summary>
    public const int ImageParallelism = 4;

    /// <summary>Gets a value indicating whether a sync is running or waiting for the lock.</summary>
    public bool IsRunning => Volatile.Read(ref _active) > 0 || _background is { IsCompleted: false };

    /// <summary>Gets the error of the last sync when it failed (cleared by a successful sync).</summary>
    public string? LastError
    {
        get
        {
            lock (_lastLock)
            {
                return field;
            }
        }

        private set
        {
            lock (_lastLock)
            {
                field = value;
            }
        }
    }

    /// <summary>
    /// Starts a sync in the background unless one is already running. With <paramref name="queueIfRunning"/>, a running
    /// sync is followed by one more (for changes made while it runs, such as a removed peer).
    /// </summary>
    /// <param name="queueIfRunning">Run once more after the current sync when one is running.</param>
    /// <returns><c>true</c> when a sync was started; <c>false</c> when one was already running.</returns>
    public bool TryStartBackground(bool queueIfRunning = false)
    {
        lock (_backgroundLock)
        {
            if (IsRunning)
            {
                _queued |= queueIfRunning;
                return false;
            }

            _background = Task.Run(BackgroundRunAsync);
            return true;
        }
    }

    private async Task BackgroundRunAsync()
    {
        try
        {
            await RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyShare sync failed: {Error}", ex.Message);
        }
    }

    /// <summary>Runs one sync. Concurrent calls are serialised (never overlap).</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The report.</returns>
    public async Task<SyncReport> RunAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        try
        {
            SyncReport report;
            IReadOnlyList<ImageJob> jobs;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                (report, jobs) = await RunCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            // Placeholders are written and scanned; images follow without holding the lock.
            var (images, scanned, pending) = await FetchImagesAsync(jobs, ct).ConfigureAwait(false);
            report = report with
            {
                ImagesWritten = images,
                Scanned = report.Scanned + scanned,
                PendingScans = pending ?? report.PendingScans,
                FinishedUtc = _clock.GetUtcNow(),
            };
            LastReport = report;
            SetError(null);
            try
            {
                _reports.Save(report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogReportNotPersisted(ex);
            }

            _logger.LogInformation(
                "JellyShare sync: offered={Offered} unmatched={Unmatched} conflicts={Conflicts} skippedLocal={SkippedLocal} written={Written} deleted={Deleted} skipped={Skipped} images={Images} uncheckedLocal={Unchecked} scans={Scans} pending={Pending} peerErrors={Errors}",
                report.Offered,
                report.Unmatched,
                report.Conflicts,
                report.SkippedLocal,
                report.Written,
                report.Deleted,
                report.Skipped,
                report.ImagesWritten,
                report.UncheckedLocalFiles,
                report.Scanned,
                report.PendingScans,
                report.PeerErrors.Count);
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            SetError(ex.Message);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            lock (_backgroundLock)
            {
                if (_queued && Volatile.Read(ref _active) == 0)
                {
                    _queued = false;
                    _background = Task.Run(BackgroundRunAsync, CancellationToken.None);
                }
            }
        }
    }

    private void SetError(string? error) => LastError = error;

    private async Task<(int Images, int Scanned, int? Pending)> FetchImagesAsync(IReadOnlyList<ImageJob> jobs, CancellationToken ct)
    {
        var images = 0;
        var dirs = new List<string>();
        foreach (var job in jobs)
        {
            var (count, changed) = await _writer.DownloadImagesAsync(job.Root, job.Tops, job.Fetch, ImageParallelism, ct, job.FolderArt).ConfigureAwait(false);
            images += count;
            dirs.AddRange(changed);
        }

        if (dirs.Count == 0)
        {
            return (images, 0, null);
        }

        // Folders that got an image are scanned again so Jellyfin picks the artwork up now, not at the next sync.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnqueueScans(dirs);
            var (scanned, pending) = ReportScans();
            return (images, scanned, pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-evaluates only the titles that have any of <paramref name="keys"/>, from the persisted merged index (the last
    /// good peer catalogs): no peer catalog fetch. Local presence is re-checked (file exists) for just those titles,
    /// then the same planner, writer and scan reporter as <see cref="RunAsync"/> write or delete only those titles'
    /// placeholders. Serialised with <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="keys">Keys of the titles to re-evaluate.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What was done.</returns>
    public Task<ReconcileReport> ReconcileAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
        => ReconcileAsync(keys, null, ct);

    /// <summary>
    /// Like <see cref="ReconcileAsync(IReadOnlyCollection{string}, CancellationToken)"/>, but with local presence already
    /// known by the caller (the presence sweep), so the library is not read again.
    /// </summary>
    /// <param name="keys">Keys of the titles to re-evaluate.</param>
    /// <param name="local">Local presence covering at least the affected titles; <c>null</c> to check it here.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>What was done.</returns>
    public async Task<ReconcileReport> ReconcileAsync(IReadOnlyCollection<string> keys, LocalIndexResult? local, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ReconcileReport report;
        IReadOnlyList<ImageJob> jobs;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (report, jobs) = await ReconcileCoreAsync(keys, local, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var (_, scanned, _) = await FetchImagesAsync(jobs, ct).ConfigureAwait(false);
        return report with { Scanned = report.Scanned + scanned };
    }

    private async Task<(ReconcileReport Report, IReadOnlyList<ImageJob> Images)> ReconcileCoreAsync(IReadOnlyCollection<string> keys, LocalIndexResult? known, CancellationToken ct)
    {
        var cfg = _config.Current;
        EnsureRootsSeparate(cfg);

        var merged = MergedEntries();
        if (merged is null || keys.Count == 0)
        {
            return (new ReconcileReport(0, 0, 0, 0, 0), []);
        }

        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        var affected = merged.Where(e => e.Keys.Any(wanted.Contains)).ToList();
        if (affected.Count == 0)
        {
            return (new ReconcileReport(0, 0, 0, 0, 0), []);
        }

        var entryKeys = new HashSet<string>(affected.SelectMany(e => e.Keys), StringComparer.Ordinal);
        var local = known ?? await _localIndex.BuildForKeysAsync(entryKeys, LibraryScopes.ForEntries(affected), ct).ConfigureAwait(false);
        var contributing = cfg.Peers
            .Where(p => p.Enabled && p.Id != Guid.Empty)
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var relay = _relayBase.Current;
        var movies = affected.Where(e => MediaTypes.Of(e.Sources[0].Item.Type) == MediaType.Movies).ToList();
        var episodes = affected.Where(e => MediaTypes.Of(e.Sources[0].Item.Type) == MediaType.Shows).ToList();
        var movieResult = await ApplyAsync(movies, local.MovieKeys, null, cfg.MoviesPlaceholderPath, relay, contributing, ct, targeted: true).ConfigureAwait(false);
        var showResult = await ApplyAsync(episodes, local.EpisodeKeys, local.Series, cfg.ShowsPlaceholderPath, relay, contributing, ct, targeted: true).ConfigureAwait(false);
        var (scanned, _) = ReportScans();

        var report = new ReconcileReport(
            affected.Count,
            movieResult.Write.Written + showResult.Write.Written,
            movieResult.Write.Deleted + showResult.Write.Deleted,
            scanned,
            local.MissingFiles);
        _logger.LogInformation(
            "JellyShare reconcile: entries={Entries} written={Written} deleted={Deleted} scans={Scans}",
            report.Entries,
            report.Written,
            report.Deleted,
            report.Scanned);
        return (report, Jobs(movieResult.Images, showResult.Images));
    }

    private static List<ImageJob> Jobs(params ImageJob?[] jobs) => jobs.OfType<ImageJob>().ToList();

    private void EnsureRootsSeparate(JellyShareSettings cfg)
    {
        if (RelayBase.OverrideError(cfg.RelayBaseOverride) is { } relayError)
        {
            // .strm files pointing elsewhere would always be refused by the relay (403).
            throw new InvalidOperationException(relayError);
        }

        // Every .strm/.nfo the plugin wrote under a root is its own to delete, so a root must never be (or contain, or
        // sit inside) one of the owner's library folders, and must be the plugin's own folder.
        var locations = _localIndex.LibraryLocations();
        var roots = MediaTypes.Receivable.Select(t => (Type: t, Root: PlaceholderPaths.RootOf(cfg, t))).ToList();
        foreach (var (type, root) in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var location in locations)
            {
                if (string.IsNullOrWhiteSpace(location) || roots.Any(r => SamePath(location, r.Root)))
                {
                    continue;
                }

                if (RootsOverlap(root, location))
                {
                    throw new InvalidOperationException(
                        $"JellyShare: the {Label(type)} placeholder folder '{root}' overlaps the library folder '{location}'. "
                        + "Choose a new, empty folder outside your media folders.");
                }
            }

            if (_writer.RootProblem(root) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
        }

        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                if (RootsOverlap(roots[i].Root, roots[j].Root))
                {
                    // Each planner owns every .strm/.nfo under its root, so shared roots would delete each other's files.
                    throw new InvalidOperationException(
                        $"JellyShare: the {Label(roots[i].Type)} and {Label(roots[j].Type)} placeholder folders must be separate, non-nested folders");
                }
            }
        }
    }

    private static string Label(MediaType type) => type switch
    {
        MediaType.Movies => "movie",
        MediaType.Shows => "show",
        MediaType.Music => "music",
        _ => "audiobook",
    };

    private async Task<(SyncReport Report, IReadOnlyList<ImageJob> Images)> RunCoreAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        EnsureRootsSeparate(cfg);

        var now = _clock.GetUtcNow();
        var states = _peerStates.Load();
        var peers = cfg.Peers
            .Where(p => p.Id != Guid.Empty)
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .ToList();
        foreach (var stale in states.Keys.Where(id => peers.All(p => p.Id != id)).ToList())
        {
            states.Remove(stale);
        }

        var errors = new List<string>();
        var sources = new List<(Guid PeerId, CatalogItem Item)>();
        var contributing = new Dictionary<Guid, RemotePeer>();
        foreach (var peer in peers.Where(p => p.Enabled).OrderBy(p => p.Priority).ThenBy(p => p.Id))
        {
            if (!states.TryGetValue(peer.Id, out var state))
            {
                state = new PeerState();
                states[peer.Id] = state;
            }

            var catalog = await FetchAsync(peer, state, now, errors, ct).ConfigureAwait(false);
            if (catalog is null)
            {
                continue;
            }

            contributing[peer.Id] = peer;
            // Also applied to a cached catalog: it may predate a change of what this peer is accepted for.
            sources.AddRange(catalog.Items.Where(item => MediaTypes.IsReceived(peer, item.Type)).Select(item => (peer.Id, item)));
        }

        var index = MergeIndex.Build(sources);
        var local = await _localIndex.BuildAsync(ct).ConfigureAwait(false);
        if (local.UncheckedFiles > 0)
        {
            _logger.LogWarning(
                "JellyShare sync: {Count} local files could not be checked in time (slow or hung mount); they were treated as present",
                local.UncheckedFiles);
        }

        var relay = _relayBase.Current;
        var skippedLocal = 0;
        var withoutFolder = new List<MediaType>();
        var results = new List<(PlaceholderWriteResult Write, int Skipped, ImageJob? Images)>();
        foreach (var type in MediaTypes.Receivable)
        {
            var entries = index.Entries.Where(e => MediaTypes.Of(e.Sources[0].Item.Type) == type).ToList();
            var root = PlaceholderPaths.RootOf(cfg, type);
            if (string.IsNullOrWhiteSpace(root))
            {
                if (entries.Count > 0)
                {
                    withoutFolder.Add(type);
                }

                continue;
            }

            var keys = local.KeysOf(type);
            skippedLocal += entries.Count(e => PlaceholderPlanner.IsLocal(e, keys));
            results.Add(await ApplyAsync(entries, keys, type == MediaType.Shows ? local.Series : null, root, relay, contributing, ct).ConfigureAwait(false));
        }

        if (withoutFolder.Count > 0)
        {
            _logger.LogWarning("JellyShare sync: no placeholder folder is set for {Types}; those titles were skipped", MediaTypes.Format(withoutFolder));
        }

        _mergedIndex.Save(index.Entries, now);
        _merged = index.Entries.ToList();
        _mergedStamp = _mergedIndex.Stamp();
        _peerStates.Save(states);

        var (scanned, pending) = ReportScans();

        var report = new SyncReport(
            index.Entries.Count,
            index.UnmatchedCount,
            skippedLocal,
            results.Sum(r => r.Write.Written),
            results.Sum(r => r.Write.Deleted),
            errors)
        {
            Skipped = results.Sum(r => r.Skipped),
            SkippedTypesWithoutFolder = withoutFolder,
            Conflicts = index.ConflictCount,
            Scanned = scanned,
            PendingScans = pending,
            UncheckedLocalFiles = local.UncheckedFiles,
            MissingLocalFiles = local.MissingFiles,
            FinishedUtc = _clock.GetUtcNow(),
        };
        return (report, Jobs([.. results.Select(r => r.Images)]));
    }

    /// <param name="Root">The full placeholder root.</param>
    /// <param name="Tops">The image folders (relative).</param>
    /// <param name="Fetch">Fetches an image of a folder.</param>
    /// <param name="FolderArt"><c>folder.jpg</c> in album/book folders instead of a poster and fanart per title folder.</param>
    private sealed record ImageJob(string Root, IReadOnlyList<string> Tops, PlaceholderImageFetcher Fetch, bool FolderArt);

    private async Task<CatalogResponse?> FetchAsync(
        RemotePeer peer,
        PeerState state,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken ct)
    {
        var etag = state.LastGoodCatalog is null ? null : state.Etag;
        PeerFetchResult result;
        try
        {
            result = await _client.FetchCatalogAsync(peer, etag, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // One flaky peer must never fail the whole sync: record it like any other fetch failure.
            result = new PeerFetchResult(false, false, null, "connection failed: " + ex.GetType().Name);
        }


        if (result.Ok && result.Catalog is not null)
        {
            state.LastGoodCatalog = result.Catalog;
            state.Etag = string.IsNullOrEmpty(result.Catalog.Etag) ? null : result.Catalog.Etag;
            MarkSuccess(state, now);
            return result.Catalog;
        }

        string error;
        if (result.NotModified && state.LastGoodCatalog is not null)
        {
            MarkSuccess(state, now);
            return state.LastGoodCatalog;
        }
        else if (result.NotModified)
        {
            error = "not modified without a cached catalog";
            state.Etag = null;
        }
        else
        {
            error = string.IsNullOrWhiteSpace(result.Error) ? "unknown error" : result.Error;
        }

        state.LastError = error;
        state.FailingSinceUtc ??= now;
        errors.Add((string.IsNullOrWhiteSpace(peer.Name) ? peer.Id.ToString() : peer.Name) + ": " + error);
        _logger.LogWarning("JellyShare sync: peer {Peer} failed: {Error}", peer.Name, error);

        if (state.LastGoodCatalog is not null && now - state.FailingSinceUtc.Value <= FailureGrace)
        {
            return state.LastGoodCatalog;
        }

        return null;
    }

    private static bool RootsOverlap(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var x = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)) + Path.DirectorySeparatorChar;
        var y = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return x.StartsWith(y, comparison) || y.StartsWith(x, comparison);
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            comparison);
    }

    private static void MarkSuccess(PeerState state, DateTimeOffset now)
    {
        state.LastSuccessUtc = now;
        state.FailingSinceUtc = null;
        state.LastError = null;
    }

    private async Task<(PlaceholderWriteResult Write, int Skipped, ImageJob? Images)> ApplyAsync(
        List<MergedEntry> entries,
        ISet<string> localKeys,
        IReadOnlyDictionary<string, LocalSeries>? localSeries,
        string root,
        string relay,
        Dictionary<Guid, RemotePeer> peers,
        CancellationToken ct,
        bool targeted = false)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            _logger.LogWarning("JellyShare sync: a placeholder root is not configured");
            return (new PlaceholderWriteResult(0, 0, 0, 0, []), 0, null);
        }

        if (entries.Count == 0 && targeted)
        {
            return (new PlaceholderWriteResult(0, 0, 0, 0, []), 0, null);
        }

        var rootFull = Path.GetFullPath(root);
        var ledger = Ledger();
        var records = ledger.For(rootFull);
        if (records is null)
        {
            // First run with a ledger: what is on disk now counts as plugin-written.
            records = PlaceholderLedger.Seed(_writer.ScanExisting(rootFull));
            ledger.Set(rootFull, records);
        }

        bool Present(string rel) => _writer.Exists(rootFull, rel);

        // "Existing" is what the plugin wrote and is still there: compared by recorded hash, never by reading the
        // file, so files Jellyfin rewrites (NFO saver) are left alone and files it adds are never deleted.
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        var vanished = new List<string>();
        PlaceholderDiff diff;
        if (targeted)
        {
            // Only the given entries' own files may be written or deleted (and only those are looked at). The shared
            // tvshow.nfo is left to full syncs unless it is missing (other episodes of the series may depend on it).
            var owned = PlaceholderPlanner.Plan(entries, new HashSet<string>(), new Dictionary<string, string>(), relay, localSeries)
                .Writes
                .Select(w => w.RelativePath)
                .Where(p => !p.EndsWith("/tvshow.nfo", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var path in owned)
            {
                if (records.TryGetValue(path, out var hash))
                {
                    if (Present(path))
                    {
                        existing[path] = hash;
                    }
                    else
                    {
                        vanished.Add(path);
                    }
                }
            }

            diff = PlaceholderPlanner.Plan(entries, localKeys, existing, relay, localSeries);
            diff = diff with
            {
                Writes = diff.Writes
                    .Where(w => !(w.RelativePath.EndsWith("/tvshow.nfo", StringComparison.Ordinal) && Present(w.RelativePath)))
                    .ToList(),
            };
        }
        else
        {
            foreach (var (path, hash) in records)
            {
                if (Present(path))
                {
                    existing[path] = hash;
                }
                else
                {
                    vanished.Add(path);
                }
            }

            diff = PlaceholderPlanner.Plan(entries, localKeys, existing, relay, localSeries);
        }

        void Record(PlaceholderWriteResult done)
        {
            foreach (var path in vanished.Concat(done.DeletedPaths))
            {
                records.Remove(path);
            }

            foreach (var (path, hash) in done.WrittenPaths)
            {
                records[path] = hash;
            }

            SaveLedger(ledger);
        }

        var imageSources = ImageSources(entries, localSeries);
        var folderArt = entries.Count > 0 && IsAudio(entries[0]);

        async Task<Stream?> Fetch(string topDir, string imageType, CancellationToken token)
        {
            if (!imageSources.TryGetValue(topDir, out var candidates))
            {
                return null;
            }

            foreach (var (peerId, itemId, types) in candidates)
            {
                if (!peers.TryGetValue(peerId, out var peer) || (types is not null && !types.Contains(imageType)))
                {
                    continue;
                }

                var stream = await _client.GetImageAsync(peer, itemId, imageType, token).ConfigureAwait(false);
                if (stream is not null)
                {
                    return stream;
                }
            }

            return null;
        }

        PlaceholderWriteResult result;
        try
        {
            // Images are fetched later, without the sync lock (see FetchImagesAsync).
            result = await _writer.ApplyAsync(diff, root, null, ct).ConfigureAwait(false);
        }
        catch (PlaceholderWriteException ex)
        {
            // Persist what already changed on disk before failing, so those folders are still scanned later.
            Record(ex.Partial);
            EnqueueScans(ex.Partial.ChangedDirs);
            throw;
        }

        Record(result);
        EnqueueScans(result.ChangedDirs);
        // Audio art goes in the album/book folder (the .strm's own folder), video art in the title's top folder.
        var newFolders = folderArt
            ? result.WrittenPaths.Keys.Where(p => p.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).Select(p => p[..p.LastIndexOf('/')]).Distinct(StringComparer.Ordinal).ToList()
            : result.NewStrmTops;
        var tops = new List<string>(newFolders);
        if (!targeted)
        {
            // Folders still missing an image (peer had none, or a download failed) get a bounded retry each full sync,
            // rotating through them so one stubborn folder never starves the rest.
            var missing = imageSources.Keys
                .Where(t => !newFolders.Contains(t) && _writer.MissesImages(rootFull, t, folderArt))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (missing.Count > 0)
            {
                _retryCursor.TryGetValue(rootFull, out var start);
                start %= missing.Count;
                var take = Math.Min(MaxImageRetriesPerRun, missing.Count);
                tops.AddRange(Enumerable.Range(0, take).Select(i => missing[(start + i) % missing.Count]));
                _retryCursor[rootFull] = start + take;
            }
        }

        return (result, diff.Skipped, tops.Count == 0 ? null : new ImageJob(rootFull, tops, Fetch, folderArt));
    }

    private static bool IsAudio(MergedEntry entry) => entry.Sources[0].Item.Type is "Audio" or "AudioBook";

    private static Dictionary<string, List<(Guid PeerId, string ItemId, string[]? Types)>> ImageSources(
        IReadOnlyList<MergedEntry> entries,
        IReadOnlyDictionary<string, LocalSeries>? localSeries)
    {
        var map = new Dictionary<string, List<(Guid, string, string[]?)>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var first = entry.Sources[0].Item;
            var isEpisode = first.Type == "Episode";
            var isAudio = IsAudio(entry);
            var local = isEpisode ? LocalSeries.Find(localSeries, PathNaming.SeriesIds(entry)) : null;
            var dir = PathNaming.ForEntry(entry, local).RelativeDir;
            var slash = dir.IndexOf('/', StringComparison.Ordinal);
            var top = isAudio || slash < 0 ? dir : dir[..slash];
            if (!map.TryGetValue(top, out var list))
            {
                list = new List<(Guid, string, string[]?)>();
                map[top] = list;
            }

            foreach (var (peerId, item) in entry.Sources)
            {
                // Audio art is the album's (or the book folder's) Primary image.
                var id = isEpisode ? item.SeriesId : item.Id;
                if (isAudio)
                {
                    id = item.AlbumId ?? item.Id;
                }
                if (!string.IsNullOrWhiteSpace(id) && !list.Any(c => c.Item1 == peerId && c.Item2 == id))
                {
                    list.Add((peerId, id, isEpisode ? null : item.ImageTypes));
                }
            }
        }

        return map;
    }

    /// <summary>Gets how often the merged index file was parsed (test hook).</summary>
    internal int MergedIndexLoads { get; private set; }

    private List<MergedEntry>? MergedEntries()
    {
        // The full sync keeps what it saved in memory; the file is parsed again only when it changed on disk.
        var stamp = _mergedIndex.Stamp();
        if (_merged is null || stamp != _mergedStamp)
        {
            MergedIndexLoads++;
            _merged = _mergedIndex.Load()?.ToEntries().ToList();
            _mergedStamp = stamp;
        }

        return _merged;
    }

    private PlaceholderLedger Ledger()
    {
        // Kept in memory; re-read only when the file changed or vanished behind our back.
        var stamp = _ledgerStore.Stamp();
        if (_ledger is null || stamp != _ledgerStamp)
        {
            _ledger = _ledgerStore.Load();
            _ledgerStamp = stamp;
        }

        return _ledger;
    }

    private void SaveLedger(PlaceholderLedger ledger)
    {
        _ledgerStore.Save(ledger);
        _ledger = ledger;
        _ledgerStamp = _ledgerStore.Stamp();
    }

    private static List<string> Distinct(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return paths.Where(p => !string.IsNullOrWhiteSpace(p) && seen.Add(p)).ToList();
    }

    private void EnqueueScans(IEnumerable<string> dirs)
    {
        var added = dirs.ToList();
        if (added.Count == 0)
        {
            return;
        }

        _pendingScans.Write(Distinct(_pendingScans.Read().Concat(added)));
    }

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = TypeOnlyLogging)]
    private void LogReportNotPersisted(Exception ex)
        => _logger.LogWarning("JellyShare sync: could not persist the sync report: {Error}", ex.GetType().Name);

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = TypeOnlyLogging)]
    private void LogScanRequestFailed(Exception ex)
        => _logger.LogWarning("JellyShare sync: scan request failed, will retry: {Error}", ex.GetType().Name);

    private (int Scanned, int Pending) ReportScans()
    {
        if (!_pendingScans.Exists())
        {
            return (0, 0);
        }

        var ordered = Distinct(_pendingScans.Read());
        var batch = ordered.Take(MaxScansPerRun).ToList();
        var rest = ordered.Skip(MaxScansPerRun).ToList();
        var scanned = 0;

        foreach (var dir in batch)
        {
            try
            {
                _scans.ReportChanged(dir);
                scanned++;
            }
            catch (Exception ex)
            {
                // Keep it queued for the next run.
                rest.Add(dir);
                LogScanRequestFailed(ex);
            }
        }

        _pendingScans.Write(rest);
        return (scanned, rest.Count);
    }
}
