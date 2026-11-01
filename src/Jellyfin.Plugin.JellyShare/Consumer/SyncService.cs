using System.Text.Json;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Keys;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>Outcome of a sync run.</summary>
/// <param name="Offered">Merged titles offered by peers.</param>
/// <param name="Unmatched">Peer items without usable provider ids (including <see cref="Conflicts"/>).</param>
/// <param name="SkippedLocal">Offered titles skipped because the owner has them locally.</param>
/// <param name="Written">Placeholder files written.</param>
/// <param name="Deleted">Placeholder files deleted.</param>
/// <param name="PeerErrors">One <c>"{peer}: {error}"</c> line per failing peer.</param>
public sealed record SyncReport(
    int Offered,
    int Unmatched,
    int SkippedLocal,
    int Written,
    int Deleted,
    IReadOnlyList<string> PeerErrors)
{
    /// <summary>Gets entries skipped because their placeholder path was unsafe.</summary>
    public int Skipped { get; init; }

    /// <summary>Gets peer items dropped because their provider ids conflict.</summary>
    public int Conflicts { get; init; }

    /// <summary>Gets the directories reported for a targeted scan in this run.</summary>
    public int Scanned { get; init; }

    /// <summary>Gets the directories left for the next run (batch limit).</summary>
    public int PendingScans { get; init; }

    /// <summary>Gets the images downloaded.</summary>
    public int ImagesWritten { get; init; }

    /// <summary>Gets local files not checked within the time budget (counted as present).</summary>
    public int UncheckedLocalFiles { get; init; }

    /// <summary>Gets local files that are missing (their titles may get placeholders).</summary>
    public int MissingLocalFiles { get; init; }

    /// <summary>Gets when the run finished.</summary>
    public DateTimeOffset FinishedUtc { get; init; }
}

/// <summary>Pulls peer catalogs, merges them and maintains the placeholder folders.</summary>
public sealed class SyncService
{
    /// <summary>A failing peer's last good catalog is used for at most this long.</summary>
    public static readonly TimeSpan FailureGrace = TimeSpan.FromDays(7);

    /// <summary>Maximum directories reported for scanning per run.</summary>
    public const int MaxScansPerRun = 200;

    private const string PendingScansFile = "pending-scans.json";
    private const string PeerStateFile = "peer-state.json";

    private readonly IConfigProvider _config;
    private readonly IPeerClient _client;
    private readonly ILocalIndex _localIndex;
    private readonly IScanReporter _scans;
    private readonly IRelayBase _relayBase;
    private readonly Func<string> _dataDir;
    private readonly TimeProvider _clock;
    private readonly ILogger<SyncService> _logger;
    private readonly PlaceholderWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="localIndex">The local index.</param>
    /// <param name="scans">The scan reporter.</param>
    /// <param name="relayBase">The relay base URL.</param>
    /// <param name="dataDir">Returns the plugin data folder.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SyncService(
        IConfigProvider config,
        IPeerClient client,
        ILocalIndex localIndex,
        IScanReporter scans,
        IRelayBase relayBase,
        Func<string> dataDir,
        TimeProvider clock,
        ILogger<SyncService> logger)
    {
        _config = config;
        _client = client;
        _localIndex = localIndex;
        _scans = scans;
        _relayBase = relayBase;
        _dataDir = dataDir;
        _clock = clock;
        _logger = logger;
        _writer = new PlaceholderWriter(logger);
    }

    /// <summary>Gets the report of the last finished run, if any.</summary>
    public SyncReport? LastReport { get; private set; }

    /// <summary>Gets the path of the persisted merged index.</summary>
    public string MergedIndexPath => Path.Combine(_dataDir(), MergedIndexStore.FileName);

    /// <summary>Runs one sync. Concurrent calls are serialised (never overlap).</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The report.</returns>
    public async Task<SyncReport> RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var report = await RunCoreAsync(ct).ConfigureAwait(false);
            LastReport = report;
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
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncReport> RunCoreAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        if (RootsOverlap(cfg.MoviesPlaceholderPath, cfg.ShowsPlaceholderPath))
        {
            // Each planner owns every .strm/.nfo under its root, so shared roots would delete each other's files.
            throw new InvalidOperationException("JellyShare: the movie and show placeholder folders must be separate, non-nested folders");
        }

        var now = _clock.GetUtcNow();
        var dataDir = _dataDir();
        Directory.CreateDirectory(dataDir);

        var stateStore = new PeerStateStore(Path.Combine(dataDir, PeerStateFile));
        var states = stateStore.Load();
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
            foreach (var item in catalog.Items)
            {
                if (item.Type is "Movie" or "Episode")
                {
                    sources.Add((peer.Id, item));
                }
            }
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
        var movies = index.Entries.Where(e => e.Sources[0].Item.Type == "Movie").ToList();
        var episodes = index.Entries.Where(e => e.Sources[0].Item.Type == "Episode").ToList();
        var skippedLocal = movies.Count(e => e.Keys.Any(local.MovieKeys.Contains))
            + episodes.Count(e => e.Keys.Any(local.EpisodeKeys.Contains));

        var movieResult = await ApplyAsync(movies, local.MovieKeys, null, cfg.MoviesPlaceholderPath, relay, contributing, ct).ConfigureAwait(false);
        var showResult = await ApplyAsync(episodes, local.EpisodeKeys, local.Series, cfg.ShowsPlaceholderPath, relay, contributing, ct).ConfigureAwait(false);

        MergedIndexStore.Save(Path.Combine(dataDir, MergedIndexStore.FileName), index.Entries, now);
        stateStore.Save(states);

        var (scanned, pending) = ReportScans(dataDir, movieResult.Write.ChangedDirs.Concat(showResult.Write.ChangedDirs));

        return new SyncReport(
            index.Entries.Count,
            index.UnmatchedCount,
            skippedLocal,
            movieResult.Write.Written + showResult.Write.Written,
            movieResult.Write.Deleted + showResult.Write.Deleted,
            errors)
        {
            Skipped = movieResult.Skipped + showResult.Skipped,
            Conflicts = index.ConflictCount,
            Scanned = scanned,
            PendingScans = pending,
            ImagesWritten = movieResult.Write.ImagesWritten + showResult.Write.ImagesWritten,
            UncheckedLocalFiles = local.UncheckedFiles,
            MissingLocalFiles = local.MissingFiles,
            FinishedUtc = _clock.GetUtcNow(),
        };
    }

    private async Task<CatalogResponse?> FetchAsync(
        RemotePeer peer,
        PeerState state,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken ct)
    {
        var etag = state.LastGoodCatalog is null ? null : state.Etag;
        var result = await _client.FetchCatalogAsync(peer, etag, ct).ConfigureAwait(false);

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

    private static void MarkSuccess(PeerState state, DateTimeOffset now)
    {
        state.LastSuccessUtc = now;
        state.FailingSinceUtc = null;
        state.LastError = null;
    }

    private async Task<(PlaceholderWriteResult Write, int Skipped)> ApplyAsync(
        IReadOnlyList<MergedEntry> entries,
        ISet<string> localKeys,
        IReadOnlyDictionary<string, LocalSeries>? localSeries,
        string root,
        string relay,
        IReadOnlyDictionary<Guid, RemotePeer> peers,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            _logger.LogWarning("JellyShare sync: a placeholder root is not configured");
            return (new PlaceholderWriteResult(0, 0, 0, 0, []), 0);
        }

        var existing = PlaceholderWriter.ScanExisting(root);
        var diff = PlaceholderPlanner.Plan(entries, localKeys, existing, relay, localSeries);
        var imageSources = ImageSources(entries, localSeries);

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

        var result = await _writer.ApplyAsync(diff, root, Fetch, ct).ConfigureAwait(false);
        return (result, diff.Skipped);
    }

    private static Dictionary<string, List<(Guid PeerId, string ItemId, string[]? Types)>> ImageSources(
        IReadOnlyList<MergedEntry> entries,
        IReadOnlyDictionary<string, LocalSeries>? localSeries)
    {
        var map = new Dictionary<string, List<(Guid, string, string[]?)>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var first = entry.Sources[0].Item;
            var isEpisode = first.Type == "Episode";
            var local = isEpisode ? LocalSeries.Find(localSeries, PathNaming.SeriesIds(entry)) : null;
            var dir = PathNaming.ForEntry(entry, local).RelativeDir;
            var slash = dir.IndexOf('/', StringComparison.Ordinal);
            var top = slash < 0 ? dir : dir[..slash];
            if (!map.TryGetValue(top, out var list))
            {
                list = new List<(Guid, string, string[]?)>();
                map[top] = list;
            }

            foreach (var (peerId, item) in entry.Sources)
            {
                var id = isEpisode ? item.SeriesId : item.Id;
                if (!string.IsNullOrWhiteSpace(id) && !list.Any(c => c.Item1 == peerId && c.Item2 == id))
                {
                    list.Add((peerId, id, isEpisode ? null : item.ImageTypes));
                }
            }
        }

        return map;
    }

    private (int Scanned, int Pending) ReportScans(string dataDir, IEnumerable<string> changed)
    {
        var path = Path.Combine(dataDir, PendingScansFile);
        var queue = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                queue.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("JellyShare sync: pending scan list unreadable, ignoring it");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = queue.Concat(changed).Where(p => !string.IsNullOrWhiteSpace(p) && seen.Add(p)).ToList();
        var now = ordered.Take(MaxScansPerRun).ToList();
        var rest = ordered.Skip(MaxScansPerRun).ToList();

        foreach (var dir in now)
        {
            try
            {
                _scans.ReportChanged(dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("JellyShare sync: scan request failed: {Error}", ex.Message);
            }
        }

        if (rest.Count > 0 || File.Exists(path))
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(rest));
        }

        return (now.Count, rest.Count);
    }
}
