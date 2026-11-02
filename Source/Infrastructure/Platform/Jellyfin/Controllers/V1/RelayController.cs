using System.Diagnostics.CodeAnalysis;
using System.Net;
using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Relay;
using JellyShare.Jellyfin.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Controllers.V1;

/// <summary>
/// The consumer's loopback relay. Placeholder <c>.strm</c> files point here; Jellyfin itself (its ffmpeg and its
/// static-stream proxy) opens the URL, and the relay streams the title from the best peer, sending the peer token
/// as a header. Clients never get this URL (see <see cref="PlaybackInfoFilter"/>).
/// </summary>
[Route("JellyShare/v1")]
public class RelayController : ControllerBase
{
    /// <summary>Default time a peer gets to answer with headers before the next peer is tried.</summary>
    public static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Default time a committed peer may send no bytes before the response is aborted.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private const string ExpectedCancellation = "A peer timeout or stall is an expected cancellation, not an error: no stack trace is wanted.";

    private const string RedactedLogging = "Only the exception message is logged, with the peer token redacted; the exception object itself could carry the token.";

    // A request that passed through a reverse proxy on this host would otherwise look like loopback.
    // X-Original-* are what ASP.NET's forwarded-headers middleware leaves after consuming X-Forwarded-*.
    // A proxy that sends none of these cannot be told apart (README, "Security notes").
    private static readonly string[] _forwardingHeaders =
    {
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port", "X-Real-IP", "Forwarded",
        "Via", "X-Original-For", "X-Original-Host", "X-Original-Proto", "CF-Connecting-IP", "True-Client-IP",
    };

    private readonly ISettingsProvider _config;
    private readonly IConsumerIndex _index;
    private readonly IPeerClient _client;
    private readonly RelaySessions _sessions;
    private readonly ILogger<RelayController> _logger;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _idleTimeout;

    /// <summary>Initializes a new instance of the <see cref="RelayController"/> class.</summary>
    /// <param name="config">The plugin configuration (peers).</param>
    /// <param name="index">The merged index and peer states.</param>
    /// <param name="client">The peer client.</param>
    /// <param name="sessions">The active playbacks.</param>
    /// <param name="logger">The logger.</param>
    public RelayController(ISettingsProvider config, IConsumerIndex index, IPeerClient client, RelaySessions sessions, ILogger<RelayController> logger)
        : this(config, index, client, sessions, logger, DefaultHeaderTimeout, DefaultIdleTimeout)
    {
    }

    internal RelayController(
        ISettingsProvider config,
        IConsumerIndex index,
        IPeerClient client,
        RelaySessions sessions,
        ILogger<RelayController> logger,
        TimeSpan headerTimeout,
        TimeSpan idleTimeout)
    {
        _idleTimeout = idleTimeout;
        _config = config;
        _index = index;
        _client = client;
        _sessions = sessions;
        _logger = logger;
        _headerTimeout = headerTimeout;
    }

    /// <summary>
    /// Streams a title from the first peer that answers 200/206, forwarding <c>Range</c>. Loopback only.
    /// Peers are tried in <see cref="SourceSelector"/> order (the peer of an active playback first); a peer that
    /// answers anything else, times out or cannot be reached is skipped, but only before the first byte was sent.
    /// </summary>
    /// <param name="key">The canonical key of the title.</param>
    /// <returns>The stream, or 403/404/503.</returns>
    [HttpGet("play/{key}")]
    [HttpHead("play/{key}")]
    [AllowAnonymous]
    public async Task<IActionResult> Play(string key)
    {
        if (!IsLocalRequest(HttpContext))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse("forbidden"));
        }

        var entry = _index.Find(key);
        if (entry is null)
        {
            return NotFound(new ErrorResponse("unknown title"));
        }

        var sources = PreferActive(entry.CanonicalKey, SourceSelector.Order(entry, _config.Current.Peers, _index.PeerStates()));
        var range = RelayRange.Forwardable(Request.Headers.Range.ToString());
        var aborted = HttpContext.RequestAborted;
        var isHead = HttpMethods.IsHead(Request.Method);

        List<RemotePeer>? full = null;
        foreach (var source in sources)
        {
            // The slot is taken before the request, so a second title asked meanwhile finds the peer full.
            if (!StreamLimitGate.TryClaim(source.Peer, entry.CanonicalKey, _sessions, out var claimed))
            {
                (full ??= new()).Add(source.Peer);
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            cts.CancelAfter(_headerTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _client.OpenStreamAsync(source.Peer, source.Item, range, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                Unclaim(claimed, entry.CanonicalKey, source.Peer);
                return new EmptyResult();
            }
            catch (OperationCanceledException)
            {
                Unclaim(claimed, entry.CanonicalKey, source.Peer);
                LogTimedOut(source.Peer, entry.CanonicalKey);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                // PeerClient redacts the token from connection errors.
                Unclaim(claimed, entry.CanonicalKey, source.Peer);
                LogPeerFailed(source.Peer, entry.CanonicalKey, ex);
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status != StatusCodes.Status200OK && status != StatusCodes.Status206PartialContent)
                {
                    Unclaim(claimed, entry.CanonicalKey, source.Peer);
                    _logger.LogDebug("JellyShare relay: peer {Peer} answered {Status} for {Key}", source.Peer.Name, status, entry.CanonicalKey);
                    continue;
                }

                // Committed to this peer: from here on no other peer is tried.
                cts.CancelAfter(Timeout.InfiniteTimeSpan);
                if (_sessions.Touch(entry.CanonicalKey, source.Peer.Id) || claimed)
                {
                    _logger.LogInformation("JellyShare relay: playing {Key} from peer {Peer}", entry.CanonicalKey, source.Peer.Name);
                }

                CopyHeaders(response, Response, status);
                if (!isHead)
                {
                    // Stop-oldest releases the title: the copy must end too, or it keeps pulling from the peer.
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _sessions.StopToken(entry.CanonicalKey));
                    await CopyBodyAsync(response, source, entry.CanonicalKey, stop.Token).ConfigureAwait(false);
                }

                return new EmptyResult();
            }
        }

        if (aborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }

        if (full?.Count == sources.Count)
        {
            _logger.LogInformation("JellyShare relay: stream limit reached for {Key} on peers {Peers}", entry.CanonicalKey, string.Join(", ", full.Select(p => p.Name)));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse("stream limit reached"));
        }

        _logger.LogWarning("JellyShare relay: no peer could serve {Key} ({Count} tried)", entry.CanonicalKey, sources.Count);
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse("no peer available"));
    }

    /// <summary>
    /// Checks that a request comes straight from this host: loopback address and no proxy forwarding headers.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns><c>true</c> when the relay may answer.</returns>
    public static bool IsLocalRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (!IPAddress.IsLoopback(ip))
        {
            return false;
        }

        return !_forwardingHeaders.Any(context.Request.Headers.ContainsKey);
    }

    private static void CopyHeaders(HttpResponseMessage from, HttpResponse to, int status)
    {
        to.StatusCode = status;
        var content = from.Content.Headers;
        if (content.ContentType is not null)
        {
            to.ContentType = content.ContentType.ToString();
        }

        if (content.ContentLength is { } length)
        {
            to.ContentLength = length;
        }

        if (content.TryGetValues("Content-Range", out var contentRange))
        {
            to.Headers.ContentRange = string.Join(",", contentRange);
        }

        if (from.Headers.AcceptRanges.Count > 0)
        {
            to.Headers.AcceptRanges = string.Join(",", from.Headers.AcceptRanges);
        }
    }

    // Only the request that made the claim undoes it; a claim made by another request for the same title stays.
    private void Unclaim(bool claimed, string key, RemotePeer peer)
    {
        if (claimed)
        {
            _sessions.CancelReservation(key, peer.Id);
        }
    }

    private IReadOnlyList<RelaySource> PreferActive(string key, IReadOnlyList<RelaySource> ordered)
    {
        var active = _sessions.ActivePeer(key);
        if (active is null)
        {
            return ordered;
        }

        return ordered.Where(s => s.Peer.Id == active).Concat(ordered.Where(s => s.Peer.Id != active)).ToList();
    }

    private async Task CopyBodyAsync(HttpResponseMessage response, RelaySource source, string key, CancellationToken ct)
    {
        var aborted = HttpContext.RequestAborted;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    // A peer that stops sending must not hold the request (and Jellyfin's ffmpeg) forever.
                    idle.CancelAfter(_idleTimeout);
                    var read = await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (read == 0)
                    {
                        break;
                    }

                    await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && idle.IsCancellationRequested)
        {
            LogStalled(source.Peer, key);
            HttpContext.Abort();
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            LogReleased(source.Peer, key);
            HttpContext.Abort();
        }
        catch (OperationCanceledException)
        {
            // The client went away; the linked token already cancelled the upstream read.
        }
        catch (Exception) when (aborted.IsCancellationRequested)
        {
            // The client went away while we wrote: not a peer failure.
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            LogStreamBroke(source.Peer, key, ex);
            HttpContext.Abort();
        }
    }

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = ExpectedCancellation)]
    private void LogTimedOut(RemotePeer peer, string key)
        => _logger.LogDebug("JellyShare relay: peer {Peer} timed out for {Key}", peer.Name, key);

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = ExpectedCancellation)]
    private void LogStalled(RemotePeer peer, string key)
        => _logger.LogDebug("JellyShare relay: peer {Peer} stalled for {Key}, aborting", peer.Name, key);

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = ExpectedCancellation)]
    private void LogReleased(RemotePeer peer, string key)
        => _logger.LogDebug("JellyShare relay: playback of {Key} from peer {Peer} was stopped, aborting", key, peer.Name);

    private static string Redacted(RemotePeer peer, Exception ex)
        => string.IsNullOrEmpty(peer.Token) ? ex.Message : ex.Message.Replace(peer.Token, "***", StringComparison.Ordinal);

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = RedactedLogging)]
    private void LogPeerFailed(RemotePeer peer, string key, Exception ex)
        => _logger.LogDebug("JellyShare relay: peer {Peer} failed for {Key}: {Error}", peer.Name, key, Redacted(peer, ex));

    [SuppressMessage("Major Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter.", Justification = RedactedLogging)]
    private void LogStreamBroke(RemotePeer peer, string key, Exception ex)
        => _logger.LogDebug("JellyShare relay: stream from peer {Peer} broke for {Key}: {Error}", peer.Name, key, Redacted(peer, ex));
}
