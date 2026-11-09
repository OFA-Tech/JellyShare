using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Federation;

namespace JellyShare.Domain.Models.Requests.Federation;

/// <summary>Gets the catalog the calling peer may see (<c>GET catalog</c>).</summary>
/// <param name="Token">The presented share token (header value; may be empty).</param>
/// <param name="IfNoneMatch">The <c>If-None-Match</c> header values.</param>
/// <param name="Types">The <c>types</c> query value (comma-separated media types); <c>null</c> from a peer that does not send it.</param>
public sealed record GetCatalogQuery(string? Token, IReadOnlyList<string?> IfNoneMatch, string? Types = null) : IQuery<CatalogResult>
{
    /// <inheritdoc />
    public override string ToString() => "GetCatalogQuery { Token = *** }";
}
