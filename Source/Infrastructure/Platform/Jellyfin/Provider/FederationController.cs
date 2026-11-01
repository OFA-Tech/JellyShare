using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Provider;

/// <summary>
/// Provider-side federation API consumed by peer servers.
/// </summary>
[ApiController]
[Route("JellyShare/v1")]
public class FederationController : ControllerBase
{
    /// <summary>Name of the request header carrying the share token.</summary>
    public const string TokenHeader = "X-JellyShare-Token";

    private static readonly string[] ImageTypes = { "Primary", "Backdrop", "Logo", "Thumb" };
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly FileExtensionContentTypeProvider VideoContentTypes = CreateVideoContentTypes();

    private readonly IServerIdentity _identity;
    private readonly CatalogBuilder _catalog;
    private readonly ILocalItemSource _source;
    private readonly IConfigProvider _config;
    private readonly ShareTokenService _tokens;
    private readonly StreamLimiter _limiter;
    private readonly CappedStreamService _capped;
    private readonly ILogger<FederationController> _logger;

    /// <summary>Initializes a new instance of the <see cref="FederationController"/> class.</summary>
    /// <param name="identity">The local server identity.</param>
    /// <param name="catalog">The catalog builder.</param>
    /// <param name="source">The local item source (used for image paths).</param>
    /// <param name="config">The plugin configuration provider.</param>
    /// <param name="tokens">The share token verifier.</param>
    /// <param name="limiter">The per-share concurrent stream limiter.</param>
    /// <param name="capped">The capped (transcoded) stream service.</param>
    /// <param name="logger">The logger.</param>
    public FederationController(
        IServerIdentity identity,
        CatalogBuilder catalog,
        ILocalItemSource source,
        IConfigProvider config,
        ShareTokenService tokens,
        StreamLimiter limiter,
        CappedStreamService capped,
        ILogger<FederationController> logger)
    {
        _limiter = limiter;
        _capped = capped;
        _logger = logger;
        _identity = identity;
        _catalog = catalog;
        _source = source;
        _config = config;
        _tokens = tokens;
    }

    /// <summary>Anonymous liveness and protocol-version probe.</summary>
    /// <returns>The protocol version and server name.</returns>
    [HttpGet("ping")]
    [AllowAnonymous]
    [Produces("application/json")]
    public ActionResult<PingResponse> Ping()
    {
        return new PingResponse(ProtocolInfo.Version, _identity.ServerName);
    }

    /// <summary>Gets the catalog the calling peer may see.</summary>
    /// <returns>The catalog, 304 when <c>If-None-Match</c> matches, or 401.</returns>
    [HttpGet("catalog")]
    [AllowAnonymous]
    [Produces("application/json")]
    public IActionResult Catalog()
    {
        var share = Authenticate();
        if (share is null)
        {
            return Unauthorized401();
        }

        // The ETag is known before any catalog entry is built, so a 304 costs one library read and no per-item queries.
        var plan = _catalog.Plan(share);
        var etag = "\"" + plan.Etag + "\"";
        Response.Headers.ETag = etag;
        if (IfNoneMatchContains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(plan.Build());
    }

    /// <summary>Gets an image of an item in the calling peer's catalog.</summary>
    /// <param name="itemId">The item id (or the id of a series owning a shared episode).</param>
    /// <param name="imageType">One of Primary, Backdrop, Logo, Thumb.</param>
    /// <returns>The image file, or 400/401/404.</returns>
    [HttpGet("image/{itemId}/{imageType}")]
    [AllowAnonymous]
    public IActionResult Image(string itemId, string imageType)
    {
        var share = Authenticate();
        if (share is null)
        {
            return Unauthorized401();
        }

        var canonicalType = ImageTypes.FirstOrDefault(t => string.Equals(t, imageType, StringComparison.OrdinalIgnoreCase));
        if (canonicalType is null)
        {
            return StatusCode(StatusCodes.Status400BadRequest, new ErrorResponse("unknown image type"));
        }

        if (!Guid.TryParse(itemId, out var id) || !_catalog.IsExposed(share, id))
        {
            return NotFound();
        }

        var path = _source.GetImagePath(id, canonicalType);
        if (string.IsNullOrEmpty(path) || !IsReadable(path))
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(path, contentType);
    }

    /// <summary>
    /// Streams the file of a movie or episode in the calling peer's catalog, with Range support. When the share has
    /// a bitrate cap below the file's bitrate, serves a live MPEG-TS transcode within the cap instead (no Range).
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="start">Start offset in seconds for a capped transcode (ignored for the original file).</param>
    /// <returns>The file or transcode, or 400/401/404/409/429/503.</returns>
    [HttpGet("stream/{itemId}")]
    [HttpHead("stream/{itemId}")]
    
    [AllowAnonymous]
    public IActionResult Stream(string itemId, [FromQuery] string? start = null)
    {
        var share = Authenticate();
        if (share is null)
        {
            return Unauthorized401();
        }

        if (!Guid.TryParse(itemId, out var id) || !_catalog.IsExposed(share, id))
        {
            return NotFound();
        }

        var info = _source.GetStreamInfo(id);
        if (info is null
            || (info.Type != "Movie" && info.Type != "Episode")
            || string.IsNullOrEmpty(info.Path))
        {
            return NotFound();
        }

        EncoderSettings? encoder = null;
        double? startSeconds = null;
        var cap = share.MaxBitrate;
        // An unknown (missing or non-positive) bitrate cannot be shown to be within the cap, so it is transcoded too.
        var overCap = cap is { } c && (info.Bitrate is not { } bitrate || bitrate <= 0 || bitrate > c);
        if (overCap)
        {
            if (!CappedStreamArgs.TryParseStart(start, info.RunTimeTicks, out startSeconds))
            {
                return StatusCode(StatusCodes.Status400BadRequest, new ErrorResponse("invalid start"));
            }

            encoder = _capped.Prepare(cap!.Value);
            if (encoder is null)
            {
                // No usable ffmpeg or a cap too low to fit audio + video: the consumer tries another peer.
                return StatusCode(StatusCodes.Status409Conflict, new ErrorResponse("over cap"));
            }
        }

        if (!_limiter.TryAcquireLease(share.Id, id, share.MaxStreams, out var lease))
        {
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse("stream limit reached"));
        }

        // The session stays held until the response has been sent (Kestrel runs this when the request ends).
        // Abort below runs first when nothing is served; releasing is idempotent.
        Response.RegisterForDispose(lease);
        if (!IsReadable(info.Path))
        {
            lease.Abort();
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse("file unavailable"));
        }

        TranscodeSlot? slot = null;
        if (encoder is not null && !HttpMethods.IsHead(Request.Method))
        {
            slot = _capped.TryReserve(share.Id, id, _config.Current.MaxConcurrentTranscodes);
            if (slot is null)
            {
                lease.Abort();
                Response.Headers.RetryAfter = "30";
                return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse("transcode limit reached"));
            }

            Response.RegisterForDispose(slot);
        }

        if (lease.IsNewSession)
        {
            _logger.LogInformation("Stream started for share {Share}: {Item}{Mode}", share.Name, info.Name, encoder is null ? string.Empty : " (capped transcode)");
        }

        if (encoder is not null)
        {
            return _capped.CreateResult(encoder, info.Path, cap!.Value, startSeconds, lease, slot);
        }

        if (!VideoContentTypes.TryGetContentType(info.Path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(info.Path, contentType, enableRangeProcessing: true);
    }

    private static FileExtensionContentTypeProvider CreateVideoContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".mkv"] = "video/x-matroska";
        provider.Mappings[".m4v"] = "video/x-m4v";
        provider.Mappings[".ts"] = "video/mp2t";
        provider.Mappings[".m2ts"] = "video/mp2t";
        provider.Mappings[".ogv"] = "video/ogg";
        return provider;
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private PeerShare? Authenticate()
    {
        var presented = Request.Headers[TokenHeader].ToString();
        return _tokens.Verify(presented, _config.Current.Shares);
    }

    private ObjectResult Unauthorized401()
        => StatusCode(StatusCodes.Status401Unauthorized, new ErrorResponse("unauthorized"));

    private bool IfNoneMatchContains(string etag)
    {
        foreach (var header in Request.Headers.IfNoneMatch)
        {
            foreach (var part in (header ?? string.Empty).Split(','))
            {
                var candidate = part.Trim();
                if (candidate.StartsWith("W/", StringComparison.Ordinal))
                {
                    candidate = candidate[2..];
                }

                if (candidate == etag || candidate == "*")
                {
                    return true;
                }
            }
        }

        return false;
    }
}
