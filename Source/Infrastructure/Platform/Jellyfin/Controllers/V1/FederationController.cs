using System.Diagnostics.CodeAnalysis;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Federation;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Interfaces.Catalog;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Federation;
using JellyShare.Domain.Models.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Controllers.V1;

/// <summary>
/// Provider-side federation API consumed by peer servers.
/// </summary>
[ApiController]
[Route("JellyShare/v1")]
public class FederationController : JellyShareControllerBase
{
    /// <summary>Name of the request header carrying the share token.</summary>
    public const string TokenHeader = ProtocolInfo.TokenHeader;

    private const string RawHeaders = "The token and If-None-Match headers are read where they are checked; binding them as action parameters would change every action's signature for no behaviour change.";

    private static readonly string[] _imageTypes = { "Primary", "Backdrop", "Logo", "Thumb" };
    private static readonly FileExtensionContentTypeProvider _contentTypes = new();
    private static readonly FileExtensionContentTypeProvider _mediaContentTypes = CreateMediaContentTypes();

    private readonly CatalogBuilder _catalog;
    private readonly ILocalItemSource _source;
    private readonly ISettingsProvider _config;
    private readonly IShareTokenService _tokens;
    private readonly StreamLimiter _limiter;
    private readonly CappedStreamService _capped;
    private readonly ILogger<FederationController> _logger;

    /// <summary>Initializes a new instance of the <see cref="FederationController"/> class.</summary>
    /// <param name="sender">Dispatches the ping and catalog use cases.</param>
    /// <param name="catalog">The catalog builder (exposure checks for images and streams).</param>
    /// <param name="source">The local item source (used for image paths).</param>
    /// <param name="config">The plugin configuration provider.</param>
    /// <param name="tokens">The share token verifier.</param>
    /// <param name="limiter">The per-share concurrent stream limiter.</param>
    /// <param name="capped">The capped (transcoded) stream service.</param>
    /// <param name="logger">The logger.</param>
    public FederationController(
        ISender sender,
        CatalogBuilder catalog,
        ILocalItemSource source,
        ISettingsProvider config,
        IShareTokenService tokens,
        StreamLimiter limiter,
        CappedStreamService capped,
        ILogger<FederationController> logger)
        : base(sender)
    {
        _limiter = limiter;
        _capped = capped;
        _logger = logger;
        _catalog = catalog;
        _source = source;
        _config = config;
        _tokens = tokens;
    }

    /// <summary>Anonymous liveness and protocol-version probe.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The protocol version and server name.</returns>
    [HttpGet("ping")]
    [AllowAnonymous]
    [Produces("application/json")]
    public async Task<ActionResult<PingResponse>> Ping(CancellationToken ct = default)
    {
        var result = await Sender.Send(new PingQuery(), ct).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : ErrorResult(result.Error);
    }

    /// <summary>Gets the catalog the calling peer may see.</summary>
    /// <param name="types">Comma-separated media types the peer wants; absent means movies and shows.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The catalog, 304 when <c>If-None-Match</c> matches, or 401.</returns>
    [HttpGet("catalog")]
    [AllowAnonymous]
    [Produces("application/json")]
    [SuppressMessage("Minor Code Smell", "S6932:Use model binding instead of accessing the raw request data", Justification = RawHeaders)]
    public async Task<IActionResult> Catalog([FromQuery] string? types = null, CancellationToken ct = default)
    {
        var query = new GetCatalogQuery(Request.Headers[TokenHeader].ToString(), Request.Headers.IfNoneMatch.ToArray(), types);
        var result = await Sender.Send(query, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return ErrorResult(result.Error);
        }

        Response.Headers.ETag = result.Value.Etag;
        return result.Value.NotModified
            ? StatusCode(StatusCodes.Status304NotModified)
            : Ok(result.Value.Catalog);
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

        var canonicalType = _imageTypes.FirstOrDefault(t => string.Equals(t, imageType, StringComparison.OrdinalIgnoreCase));
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

        if (!_contentTypes.TryGetContentType(path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(path, contentType);
    }

    /// <summary>
    /// Streams the file of a movie, episode, track or audiobook in the calling peer's catalog, with Range support. When
    /// the share has a bitrate cap below a video's bitrate, serves a live MPEG-TS transcode within the cap instead (no
    /// Range); audio is never capped.
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
        var isAudio = info?.Type is "Audio" or "AudioBook";
        if (info is null
            || (info.Type != "Movie" && info.Type != "Episode" && !isAudio)
            || string.IsNullOrEmpty(info.Path))
        {
            return NotFound();
        }

        EncoderSettings? encoder = null;
        double? startSeconds = null;
        var cap = share.MaxBitrate;

        // Audio is served as the file whatever the cap: it is small, and a transcode would break seeking for no gain.
        var overCap = !isAudio && CappedStreamArgs.IsOverCap(cap, info.Bitrate);
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

        if (!_limiter.TryAcquireLease(share.Id, info.TitleId ?? id, share.MaxStreams, out var lease))
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
            return new CappedStreamResult(_capped, encoder, info.Path, cap!.Value, startSeconds, lease, slot);
        }

        if (!_mediaContentTypes.TryGetContentType(info.Path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(info.Path, contentType, enableRangeProcessing: true);
    }

    private static FileExtensionContentTypeProvider CreateMediaContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".mp3"] = "audio/mpeg";
        provider.Mappings[".flac"] = "audio/flac";
        provider.Mappings[".m4a"] = "audio/mp4";
        provider.Mappings[".m4b"] = "audio/mp4";
        provider.Mappings[".ogg"] = "audio/ogg";
        provider.Mappings[".opus"] = "audio/ogg";
        provider.Mappings[".wav"] = "audio/wav";
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

    [SuppressMessage("Minor Code Smell", "S6932:Use model binding instead of accessing the raw request data", Justification = RawHeaders)]
    private PeerShare? Authenticate()
    {
        var presented = Request.Headers[TokenHeader].ToString();
        return _tokens.Verify(presented, _config.Current.Shares);
    }

    private ObjectResult Unauthorized401() => ErrorResult(GetCatalogQueryHandler.Unauthorized);
}
