using JellyShare.Application.Features.Catalog;
using JellyShare.Domain.Interfaces.Catalog;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Federation;
using JellyShare.Domain.Models.Requests.Federation;
using JellyShare.Domain.Results;

namespace JellyShare.Application.Features.Federation;

/// <summary>
/// Serves the catalog of the share the token belongs to. The ETag is known before any catalog entry is built, so a
/// matching <c>If-None-Match</c> (304) costs one library read and no per-item queries.
/// </summary>
public sealed class GetCatalogQueryHandler : IQueryHandler<GetCatalogQuery, CatalogResult>
{
    /// <summary>The error for a missing, unknown or disabled share token (401, body <c>{"error":"unauthorized"}</c>).</summary>
    public static readonly Error Unauthorized = Error.Unauthorized("Federation.Unauthorized", "unauthorized");

    private readonly IShareTokenService _tokens;
    private readonly ISettingsProvider _config;
    private readonly CatalogBuilder _catalog;

    /// <summary>Initializes a new instance of the <see cref="GetCatalogQueryHandler"/> class.</summary>
    /// <param name="tokens">The share token verifier.</param>
    /// <param name="config">The settings (shares).</param>
    /// <param name="catalog">The catalog builder.</param>
    public GetCatalogQueryHandler(IShareTokenService tokens, ISettingsProvider config, CatalogBuilder catalog)
    {
        _tokens = tokens;
        _config = config;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public Task<Result<CatalogResult>> Handle(GetCatalogQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var share = _tokens.Verify(request.Token, _config.Current.Shares);
        if (share is null)
        {
            return Task.FromResult(Result.Failure<CatalogResult>(Unauthorized));
        }

        var plan = _catalog.Plan(share, MediaTypes.ParseOrLegacy(request.Types));
        var etag = "\"" + plan.Etag + "\"";
        var result = IfNoneMatchContains(request.IfNoneMatch, etag)
            ? new CatalogResult(etag, null)
            : new CatalogResult(etag, plan.Build());
        return Task.FromResult(Result.Success(result));
    }

    /// <summary>Checks <c>If-None-Match</c> values (comma lists, weak validators, <c>*</c>) against an ETag.</summary>
    /// <param name="headers">The header values.</param>
    /// <param name="etag">The quoted ETag.</param>
    /// <returns><c>true</c> when one matches.</returns>
    public static bool IfNoneMatchContains(IEnumerable<string?> headers, string etag)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in headers)
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
