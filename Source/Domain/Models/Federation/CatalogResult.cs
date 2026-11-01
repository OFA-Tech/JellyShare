using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Models.Federation;

/// <summary>The outcome of a catalog request that passed authentication.</summary>
/// <param name="Etag">The quoted ETag of the share's catalog (always sent as the <c>ETag</c> header).</param>
/// <param name="Catalog">The catalog body, or <c>null</c> when <c>If-None-Match</c> matched (304 Not Modified).</param>
public sealed record CatalogResult(string Etag, CatalogResponse? Catalog)
{
    /// <summary>Gets a value indicating whether the caller's copy is current (304).</summary>
    public bool NotModified => Catalog is null;
}