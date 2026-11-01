using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Peers;
using JellyShare.Application.Features.Streaming;
using JellyShare.Application.Storage;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Controllers.V1;

/// <summary>
/// Admin-only endpoints behind the JellyShare configuration page. The whole controller requires an administrator;
/// nothing here is anonymous. Tokens are only ever returned by <see cref="CreateShare"/> (once) and never logged.
/// </summary>
[ApiController]
[Route("JellyShare/v1/admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public class AdminController : ControllerBase
{
    private const int MaxNameLength = 100;
    private const int MaxStreamsLimit = 100;
    private const int MaxTokenLength = 512;
    private const int MaxBitrateBps = 2_000_000_000;

    private static readonly object ConfigLock = new();

    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly ShareTokenService _tokens;
    private readonly StreamLimiter _limiter;
    private readonly IPeerClient _peers;
    private readonly ISyncRunner _sync;
    private readonly IConsumerIndex _consumerIndex;
    private readonly IAdminLibraries _libraries;
    private readonly IServerIdentity _identity;
    private readonly ILogger<AdminController> _logger;

    /// <summary>Initializes a new instance of the <see cref="AdminController"/> class.</summary>
    /// <param name="config">The configuration provider.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="tokens">The share token service.</param>
    /// <param name="limiter">The stream limiter (for active streams).</param>
    /// <param name="peers">The peer client (for connection tests).</param>
    /// <param name="sync">The sync runner.</param>
    /// <param name="consumerIndex">The persisted peer states.</param>
    /// <param name="libraries">The library operations.</param>
    /// <param name="identity">The local server identity.</param>
    /// <param name="logger">The logger.</param>
    public AdminController(
        ISettingsProvider config,
        ISettingsStore store,
        ShareTokenService tokens,
        StreamLimiter limiter,
        IPeerClient peers,
        ISyncRunner sync,
        IConsumerIndex consumerIndex,
        IAdminLibraries libraries,
        IServerIdentity identity,
        ILogger<AdminController> logger)
    {
        _config = config;
        _store = store;
        _tokens = tokens;
        _limiter = limiter;
        _peers = peers;
        _sync = sync;
        _consumerIndex = consumerIndex;
        _libraries = libraries;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>Gets everything the admin page shows.</summary>
    /// <returns>The status.</returns>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var cfg = _config.Current;
        var libraries = _libraries.List();
        var states = _consumerIndex.PeerStates();

        var shares = cfg.Shares
            .Select(s => new ShareView(s.Id, s.Name, s.Enabled, s.LibraryIds.ToList(), s.MaxStreams, s.MaxBitrate, _limiter.Active(s.Id)))
            .ToList();
        var peers = cfg.Peers
            .Select((p, i) => (Peer: p, Index: i))
            .OrderBy(x => x.Peer.Priority)
            .ThenBy(x => x.Index)
            .Select(x => ToView(x.Peer, states))
            .ToList();

        return Ok(new StatusResponse(
            ProtocolInfo.Version,
            _identity.ServerName,
            shares,
            peers,
            _sync.LastReport,
            RootView(cfg.MoviesPlaceholderPath, libraries),
            RootView(cfg.ShowsPlaceholderPath, libraries),
            libraries,
            cfg.SyncIntervalHours)
        {
            SyncRunning = _sync.IsRunning,
            SyncError = _sync.LastError,
        });
    }

    /// <summary>Creates a share and returns its token once.</summary>
    /// <param name="request">The share.</param>
    /// <returns>The created share with the plaintext token.</returns>
    [HttpPost("shares")]
    public IActionResult CreateShare([FromBody] CreateShareRequest request)
    {
        if (request is null)
        {
            return Bad("Missing request body.");
        }

        var error = ValidateShare(request.Name, request.LibraryIds, request.MaxStreams, request.MaxBitrate, out var name, out var libs);
        if (error is not null)
        {
            return Bad(error);
        }

        var (token, hash) = _tokens.Create();
        var share = new PeerShare
        {
            Id = Guid.NewGuid(),
            Name = name,
            TokenHash = hash,
            Enabled = true,
            LibraryIds = libs,
            MaxStreams = request.MaxStreams!.Value,
            MaxBitrate = request.MaxBitrate,
        };

        lock (ConfigLock)
        {
            var cfg = _config.Current;
            cfg.Shares = new List<PeerShare>(cfg.Shares) { share };
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: created share {ShareId} '{Name}'", share.Id, share.Name);
        Response.Headers.CacheControl = "no-store";
        return Ok(new CreatedShareResponse(share.Id, share.Name, token));
    }

    /// <summary>Updates a share's name, libraries, limits and enabled flag.</summary>
    /// <param name="id">The share id.</param>
    /// <param name="request">The new values.</param>
    /// <returns>The updated share.</returns>
    [HttpPut("shares/{id:guid}")]
    public IActionResult UpdateShare(Guid id, [FromBody] UpdateShareRequest request)
    {
        if (request is null)
        {
            return Bad("Missing request body.");
        }

        lock (ConfigLock)
        {
            var cfg = _config.Current;
            var current = cfg.Shares.FirstOrDefault(s => s.Id == id);
            if (current is null)
            {
                return NotFound(new ErrorResponse("Share not found."));
            }

            var error = ValidateShare(request.Name, request.LibraryIds, request.MaxStreams, request.MaxBitrate, out var name, out var libs);
            if (error is not null)
            {
                return Bad(error);
            }

            // Copy-on-write: a request that is reading the old share (catalog, stream limits) never sees a half-updated one.
            var share = new PeerShare
            {
                Id = current.Id,
                Name = name,
                TokenHash = current.TokenHash,
                Enabled = request.Enabled ?? current.Enabled,
                LibraryIds = libs,
                MaxStreams = request.MaxStreams!.Value,
                MaxBitrate = request.MaxBitrate,
            };
            cfg.Shares = cfg.Shares.Select(s => s.Id == id ? share : s).ToList();
            _store.Save(cfg);
            _logger.LogInformation("JellyShare admin: updated share {ShareId} (enabled={Enabled})", share.Id, share.Enabled);
            return Ok(new ShareView(share.Id, share.Name, share.Enabled, share.LibraryIds.ToList(), share.MaxStreams, share.MaxBitrate, _limiter.Active(share.Id)));
        }
    }

    /// <summary>Revokes a share immediately.</summary>
    /// <param name="id">The share id.</param>
    /// <returns>No content, or 404.</returns>
    [HttpDelete("shares/{id:guid}")]
    public IActionResult DeleteShare(Guid id)
    {
        lock (ConfigLock)
        {
            var cfg = _config.Current;
            if (cfg.Shares.All(s => s.Id != id))
            {
                return NotFound(new ErrorResponse("Share not found."));
            }

            cfg.Shares = cfg.Shares.Where(s => s.Id != id).ToList();
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: revoked share {ShareId}", id);
        return NoContent();
    }

    /// <summary>Adds a peer this server receives from.</summary>
    /// <param name="request">The peer.</param>
    /// <returns>The created peer.</returns>
    [HttpPost("peers")]
    public IActionResult AddPeer([FromBody] AddPeerRequest request)
    {
        if (request is null)
        {
            return Bad("Missing request body.");
        }

        var name = CleanName(request.Name);
        var url = NormalizeUrl(request.BaseUrl);
        var token = request.Token?.Trim() ?? string.Empty;
        if (name is null)
        {
            return Bad("Enter a name for the peer.");
        }

        if (url is null)
        {
            return Bad(InvalidUrlMessage);
        }

        if (token.Length == 0)
        {
            return Bad("Enter the token from the invitation.");
        }

        if (TokenError(token) is { } tokenError)
        {
            return Bad(tokenError);
        }

        RemotePeer peer;
        lock (ConfigLock)
        {
            var cfg = _config.Current;
            peer = new RemotePeer
            {
                Id = Guid.NewGuid(),
                Name = name,
                BaseUrl = url,
                Token = token,
                Enabled = request.Enabled ?? true,
                Priority = cfg.Peers.Count == 0 ? 0 : cfg.Peers.Max(p => p.Priority) + 1,
            };
            cfg.Peers = new List<RemotePeer>(cfg.Peers) { peer };
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: added peer {PeerId} '{Name}' at {Url}", peer.Id, peer.Name, peer.BaseUrl);
        return Ok(ToView(peer, _consumerIndex.PeerStates()));
    }

    /// <summary>Updates a peer.</summary>
    /// <param name="id">The peer id.</param>
    /// <param name="request">The new values.</param>
    /// <returns>The updated peer.</returns>
    [HttpPut("peers/{id:guid}")]
    public IActionResult UpdatePeer(Guid id, [FromBody] UpdatePeerRequest request)
    {
        if (request is null)
        {
            return Bad("Missing request body.");
        }

        lock (ConfigLock)
        {
            var cfg = _config.Current;
            var current = cfg.Peers.FirstOrDefault(p => p.Id == id);
            if (current is null)
            {
                return NotFound(new ErrorResponse("Peer not found."));
            }

            var name = CleanName(request.Name);
            var url = NormalizeUrl(request.BaseUrl);
            if (name is null)
            {
                return Bad("Enter a name for the peer.");
            }

            if (url is null)
            {
                return Bad(InvalidUrlMessage);
            }

            var newToken = request.Token?.Trim();
            if (!string.IsNullOrEmpty(newToken) && TokenError(newToken) is { } tokenError)
            {
                return Bad(tokenError);
            }

            // Copy-on-write: a running sync keeps a self-consistent (url, token) pair.
            var peer = new RemotePeer
            {
                Id = current.Id,
                Name = name,
                BaseUrl = url,
                Token = string.IsNullOrEmpty(newToken) ? current.Token : newToken,
                Enabled = request.Enabled ?? current.Enabled,
                Priority = current.Priority,
            };
            cfg.Peers = cfg.Peers.Select(p => p.Id == id ? peer : p).ToList();
            _store.Save(cfg);
            _logger.LogInformation("JellyShare admin: updated peer {PeerId} (enabled={Enabled})", peer.Id, peer.Enabled);
            return Ok(ToView(peer, _consumerIndex.PeerStates()));
        }
    }

    /// <summary>Removes a peer.</summary>
    /// <param name="id">The peer id.</param>
    /// <returns>No content, or 404.</returns>
    [HttpDelete("peers/{id:guid}")]
    public IActionResult DeletePeer(Guid id)
    {
        lock (ConfigLock)
        {
            var cfg = _config.Current;
            if (cfg.Peers.All(p => p.Id != id))
            {
                return NotFound(new ErrorResponse("Peer not found."));
            }

            cfg.Peers = cfg.Peers.Where(p => p.Id != id).ToList();
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: removed peer {PeerId}", id);

        // Its placeholders go with the next sync: start one now (or right after the one running).
        _sync.TryStartBackground(queueIfRunning: true);
        return NoContent();
    }

    /// <summary>Sets the peer priority order.</summary>
    /// <param name="request">The ids, most preferred first.</param>
    /// <returns>No content.</returns>
    [HttpPost("peers/order")]
    public IActionResult ReorderPeers([FromBody] ReorderPeersRequest request)
    {
        if (request?.Ids is null)
        {
            return Bad("Missing peer ids.");
        }

        lock (ConfigLock)
        {
            var cfg = _config.Current;
            var current = cfg.Peers
                .Select((p, i) => (Peer: p, Index: i))
                .OrderBy(x => x.Peer.Priority)
                .ThenBy(x => x.Index)
                .Select(x => x.Peer)
                .ToList();
            var listed = request.Ids
                .Distinct()
                .Select(id => current.FirstOrDefault(p => p.Id == id))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();
            var ordered = listed.Concat(current.Where(p => !listed.Contains(p))).ToList();
            var order = new Dictionary<Guid, int>();
            for (var i = 0; i < ordered.Count; i++)
            {
                order[ordered[i].Id] = i;
            }

            // Copy-on-write: peers are replaced by copies carrying the new priority.
            cfg.Peers = cfg.Peers
                .Select(p => new RemotePeer
                {
                    Id = p.Id,
                    Name = p.Name,
                    BaseUrl = p.BaseUrl,
                    Token = p.Token,
                    Enabled = p.Enabled,
                    Priority = order.TryGetValue(p.Id, out var priority) ? priority : p.Priority,
                })
                .ToList();
            _store.Save(cfg);
        }

        return NoContent();
    }

    /// <summary>Tests a peer URL and token: ping, then catalog fetch.</summary>
    /// <param name="request">The URL and token.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The outcome (always 200; errors are in the body and never contain the token).</returns>
    [HttpPost("peers/test")]
    public async Task<IActionResult> TestPeer([FromBody] TestPeerRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return Bad("Missing request body.");
        }

        var url = NormalizeUrl(request.Url);
        if (url is null)
        {
            return Ok(TestFail(InvalidUrlMessage));
        }

        var token = request.Token?.Trim() ?? string.Empty;
        var peerId = Guid.Empty;
        if (token.Length == 0 && request.PeerId is { } savedId)
        {
            var saved = _config.Current.Peers.FirstOrDefault(p => p.Id == savedId);
            if (saved is not null)
            {
                // The stored token only ever goes to the address it was saved for; a changed address needs the token typed again.
                if (!string.Equals(url, NormalizeUrl(saved.BaseUrl), StringComparison.Ordinal))
                {
                    return Ok(TestFail("The address was changed. Enter the token again to test the new address (the saved token is only used for the saved address)."));
                }

                token = saved.Token ?? string.Empty;
                peerId = saved.Id;
            }
        }

        if (token.Length == 0)
        {
            return Ok(TestFail("Enter the token from the invitation."));
        }

        if (TokenError(token) is { } tokenError)
        {
            return Ok(TestFail(tokenError));
        }

        var ping = await _peers.PingAsync(url, ct).ConfigureAwait(false);
        if (!ping.Ok)
        {
            return Ok(TestFail(Redact(PingMessage(ping), token)));
        }

        var fetch = await _peers.FetchCatalogAsync(
            new RemotePeer { Id = peerId, Name = "test", BaseUrl = url, Token = token },
            null,
            ct).ConfigureAwait(false);
        if (!fetch.Ok || fetch.Catalog is null)
        {
            return Ok(TestFail(Redact(CatalogMessage(fetch.Error), token)));
        }

        return Ok(new TestPeerResponse(
            true,
            string.IsNullOrWhiteSpace(ping.ServerName) ? fetch.Catalog.ServerName : ping.ServerName,
            fetch.Catalog.ProtocolVersion,
            fetch.Catalog.Items.Count,
            null));
    }

    /// <summary>
    /// Starts a sync in the background (not tied to the request: a first sync with many images can take minutes) and
    /// returns at once. The page polls <c>admin/status</c> (<c>syncRunning</c>, <c>lastSync</c>, <c>syncError</c>).
    /// </summary>
    /// <returns>202 with whether a sync was started or one was already running.</returns>
    [HttpPost("sync")]
    public IActionResult Sync()
    {
        var started = _sync.TryStartBackground();
        _logger.LogInformation("JellyShare admin: sync requested ({State})", started ? "started" : "already running");
        return StatusCode(StatusCodes.Status202Accepted, new SyncStartResponse(started, !started));
    }

    /// <summary>Adds one of the plugin's own placeholder roots to a library and refreshes it.</summary>
    /// <param name="request">The library and which root.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The placeholder root state.</returns>
    [HttpPost("libraries/attach")]
    public async Task<IActionResult> Attach([FromBody] AttachLibraryRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return Bad("Missing request body.");
        }

        // The path is never taken from the request: only the plugin's own configured roots can be attached.
        var isMovies = string.Equals(request.Type, "Movies", StringComparison.OrdinalIgnoreCase);
        var isShows = string.Equals(request.Type, "Shows", StringComparison.OrdinalIgnoreCase);
        if (!isMovies && !isShows)
        {
            return Bad("Type must be Movies or Shows.");
        }

        var cfg = _config.Current;
        var root = isMovies ? cfg.MoviesPlaceholderPath : cfg.ShowsPlaceholderPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return Bad("The placeholder folder is not configured.");
        }

        var libraries = _libraries.List();
        var library = libraries.FirstOrDefault(l => l.Id == request.LibraryId);
        if (library is null)
        {
            return NotFound(new ErrorResponse("Library not found."));
        }

        var expected = isMovies ? "movies" : "tvshows";
        if (!string.Equals(library.CollectionType, expected, StringComparison.OrdinalIgnoreCase))
        {
            return Bad($"'{library.Name}' is not a {(isMovies ? "Movies" : "Shows")} library.");
        }

        var holder = libraries.FirstOrDefault(l => l.Locations.Any(loc => SamePath(loc, root)));
        if (holder is not null)
        {
            return holder.Id == library.Id
                ? Ok(RootView(root, libraries))
                : Conflict(new ErrorResponse($"This folder is already part of the library '{holder.Name}'. Remove it there first."));
        }

        if (PlaceholderWriter.RootProblem(root) is { } problem)
        {
            return Conflict(new ErrorResponse(problem));
        }

        try
        {
            // The marker is only created in a new or empty folder (RootProblem above refuses anything else).
            PlaceholderWriter.EnsureRoot(root);
            await _libraries.AddPathAsync(library.Name, root, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "JellyShare admin: could not attach {Root} to {Library}", root, library.Name);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ErrorResponse($"Could not add the placeholder folder to '{library.Name}': {ex.Message}"));
        }

        _logger.LogInformation("JellyShare admin: added {Root} to library '{Library}' and started a refresh", root, library.Name);
        return Ok(RootView(root, _libraries.List()));
    }

    private const string InvalidUrlMessage =
        "The URL is not valid. Use the full address of the other server, for example http://host:8096 or https://host/jellyfin.";

    private static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    private static string? NormalizeUrl(string? url)
    {
        var uri = PeerClient.BuildUri(url, string.Empty);
        return uri is null || !string.IsNullOrEmpty(uri.UserInfo) ? null : uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => p.Trim().Replace('\\', '/').TrimEnd('/');
        return string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static PlaceholderRootView RootView(string path, IReadOnlyList<LibraryInfo> libraries)
    {
        var holder = string.IsNullOrWhiteSpace(path) ? null : libraries.FirstOrDefault(l => l.Locations.Any(loc => SamePath(loc, path)));
        return new PlaceholderRootView(path, holder is not null, holder?.Id, holder?.Name, holder?.AutomaticSeriesGrouping);
    }

    private static PeerView ToView(RemotePeer peer, IReadOnlyDictionary<Guid, PeerState> states)
    {
        states.TryGetValue(peer.Id, out var state);
        return new PeerView(
            peer.Id,
            peer.Name,
            peer.BaseUrl,
            peer.Enabled,
            peer.Priority,
            !string.IsNullOrEmpty(peer.Token),
            state?.LastSuccessUtc,
            state?.FailingSinceUtc,
            state?.LastError,
            state?.LastGoodCatalog?.Items?.Count);
    }

    private static string Redact(string message, string token)
        => string.IsNullOrEmpty(token) ? message : message.Replace(token, "***", StringComparison.Ordinal);

    private static TestPeerResponse TestFail(string error) => new(false, null, null, null, error);

    private static string PingMessage(PeerPingResult ping) => ping.Failure switch
    {
        PingFailure.InvalidUrl => InvalidUrlMessage,
        PingFailure.Unreachable => "Could not reach the server" + (string.IsNullOrWhiteSpace(ping.Detail) ? "." : ": " + ping.Detail),
        PingFailure.Timeout => "The server did not answer in time.",
        PingFailure.Redirect => "The server answered with a redirect, which is refused for security. Use the final address (check http vs https and the path).",
        PingFailure.IncompatibleProtocol => $"Incompatible JellyShare protocol version {ping.ProtocolVersion}: this server speaks {ProtocolInfo.Version}. Update the plugin on the older server.",
        _ => "That address is not a JellyShare server (or the JellyShare plugin is not installed there)."
            + (string.IsNullOrWhiteSpace(ping.Detail) ? string.Empty : " (" + ping.Detail + ")"),
    };

    private static string CatalogMessage(string? error)
    {
        var e = error ?? string.Empty;
        if (e.StartsWith("HTTP 401", StringComparison.Ordinal))
        {
            return "The server rejected the token (HTTP 401). Check that the token is complete and still valid, or ask for a new invitation.";
        }

        if (e.StartsWith("HTTP 403", StringComparison.Ordinal))
        {
            return "The server answered HTTP 403: a reverse proxy or firewall in front of it may be blocking JellyShare requests.";
        }

        if (e.StartsWith("HTTP 3", StringComparison.Ordinal))
        {
            return "The server answered with a redirect, which is refused for security. Use the final address (check http vs https and the path).";
        }

        if (e.StartsWith("timeout", StringComparison.Ordinal))
        {
            return "The server did not answer the catalog request in time.";
        }

        if (e.StartsWith("incompatible protocol", StringComparison.Ordinal))
        {
            return "Incompatible JellyShare protocol (" + e["incompatible protocol".Length..].Trim() + "). Update the plugin on the older server.";
        }

        if (e.StartsWith("invalid catalog", StringComparison.Ordinal))
        {
            return "The server answered, but the catalog is not valid JellyShare data.";
        }

        if (e.StartsWith("connection failed", StringComparison.Ordinal))
        {
            return "Could not connect to the server" + e["connection failed".Length..];
        }

        return "The catalog request failed: " + (string.IsNullOrWhiteSpace(e) ? "unknown error" : e);
    }

    private BadRequestObjectResult Bad(string message) => BadRequest(new ErrorResponse(message));

    private static string? TokenError(string token)
    {
        if (token.Length > MaxTokenLength)
        {
            return $"The token is too long (at most {MaxTokenLength} characters). Check that you pasted only the token.";
        }

        return token.Any(char.IsControl)
            ? "The token contains line breaks or other control characters. Paste only the token."
            : null;
    }

    private string? ValidateShare(string? rawName, List<Guid>? libraryIds, int? maxStreams, int? maxBitrate, out string name, out List<Guid> libs)
    {
        name = string.Empty;
        libs = new List<Guid>();
        var clean = CleanName(rawName);
        if (clean is null)
        {
            return "Enter a name for the invitation (at most 100 characters).";
        }

        if (maxStreams is null or < 1 or > MaxStreamsLimit)
        {
            return $"Max streams must be a number between 1 and {MaxStreamsLimit}.";
        }

        if (maxBitrate is <= 0 or > MaxBitrateBps)
        {
            return $"Max bitrate must be between 0 and {MaxBitrateBps / 1_000_000} Mbps, or empty for no limit.";
        }

        var wanted = (libraryIds ?? new List<Guid>()).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return "Pick at least one library to share.";
        }

        var known = _libraries.List().Select(l => l.Id).ToHashSet();
        if (wanted.Any(id => !known.Contains(id)))
        {
            return "One of the selected libraries does not exist.";
        }

        name = clean;
        libs = wanted;
        return null;
    }
}
