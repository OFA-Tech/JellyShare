using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

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

    private readonly IServerIdentity _identity;
    private readonly CatalogBuilder _catalog;
    private readonly ILocalItemSource _source;
    private readonly IConfigProvider _config;
    private readonly ShareTokenService _tokens;

    /// <summary>Initializes a new instance of the <see cref="FederationController"/> class.</summary>
    /// <param name="identity">The local server identity.</param>
    /// <param name="catalog">The catalog builder.</param>
    /// <param name="source">The local item source (used for image paths).</param>
    /// <param name="config">The plugin configuration provider.</param>
    /// <param name="tokens">The share token verifier.</param>
    public FederationController(
        IServerIdentity identity,
        CatalogBuilder catalog,
        ILocalItemSource source,
        IConfigProvider config,
        ShareTokenService tokens)
    {
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
