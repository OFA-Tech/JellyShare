using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShare.Provider;

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
    private readonly ILogger<FederationController> _logger;

    /// <summary>Initializes a new instance of the <see cref="FederationController"/> class.</summary>
    /// <param name="identity">The local server identity.</param>
    /// <param name="catalog">The catalog builder.</param>
    /// <param name="source">The local item source (used for image paths).</param>
    /// <param name="config">The plugin configuration provider.</param>
    /// <param name="tokens">The share token verifier.</param>
    /// <param name="limiter">The per-share concurrent stream limiter.</param>
    /// <param name="logger">The logger.</param>
    public FederationController(
        IServerIdentity identity,
        CatalogBuilder catalog,
        ILocalItemSource source,
        IConfigProvider config,
        ShareTokenService tokens,
        StreamLimiter limiter,
        ILogger<FederationController> logger)
    {
        _limiter = limiter;
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

        var catalog = _catalog.Build(share);
        var etag = "\"" + catalog.Etag + "\"";
        Response.Headers.ETag = etag;
        if (IfNoneMatchContains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(catalog);
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

    /// <summary>Streams the file of a movie or episode in the calling peer's catalog, with Range support.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The file, or 401/404/409/429/503.</returns>
    [HttpGet("stream/{itemId}")]
    [HttpHead("stream/{itemId}")]
    
    [AllowAnonymous]
    public IActionResult Stream(string itemId)
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

        var overCap = CheckBitrateCap(share, info);
        if (overCap is not null)
        {
            return overCap;
        }

        if (!_limiter.TryAcquire(share.Id, id, share.MaxStreams, out var isNewSession))
        {
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse("stream limit reached"));
        }

        if (!IsReadable(info.Path))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse("file unavailable"));
        }

        if (isNewSession)
        {
            _logger.LogInformation("Stream started for share {Share}: {Item}", share.Name, info.Name);
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

    // Placeholder until Task 12: a capped stream service replaces the 409 so the provider transcodes instead.
    private ObjectResult? CheckBitrateCap(PeerShare share, StreamInfo info)
    {
        if (share.MaxBitrate is { } cap && info.Bitrate is { } bitrate && bitrate > cap)
        {
            return StatusCode(StatusCodes.Status409Conflict, new ErrorResponse("over cap"));
        }

        return null;
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
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
