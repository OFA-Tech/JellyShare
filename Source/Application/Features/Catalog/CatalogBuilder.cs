using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Application.Features.Catalog;

/// <summary>A share's catalog ETag, known before any catalog entry is built.</summary>
/// <param name="Etag">The ETag (lowercase hex SHA-256 over item ids and modification times).</param>
/// <param name="Build">Builds (or returns the cached) catalog for this ETag.</param>
public sealed record CatalogPlan(string Etag, Func<CatalogResponse> Build);

/// <summary>Builds the catalog a share exposes to its peer.</summary>
public class CatalogBuilder
{
    private readonly ILocalItemSource _source;
    private readonly IServerIdentity _identity;
    private readonly ISettingsProvider _config;
    private readonly Func<string, bool> _fileExists;
    private readonly ConcurrentDictionary<Guid, CatalogResponse> _cache = new();

    /// <summary>Initializes a new instance of the <see cref="CatalogBuilder"/> class.</summary>
    /// <param name="source">The local item source.</param>
    /// <param name="identity">The local server identity.</param>
    /// <param name="config">The configuration provider.</param>
    /// <param name="fileExists">Existence check for item files; defaults to <see cref="File.Exists(string)"/>.</param>
    public CatalogBuilder(
        ILocalItemSource source,
        IServerIdentity identity,
        ISettingsProvider config,
        Func<string, bool>? fileExists = null)
    {
        _source = source;
        _identity = identity;
        _config = config;
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>Builds the catalog for a share (served from the cache when nothing changed).</summary>
    /// <param name="share">The share.</param>
    /// <returns>The catalog response including its ETag.</returns>
    public CatalogResponse Build(PeerShare share) => Plan(share).Build();

    /// <summary>
    /// Lists the share's items and computes the catalog ETag from their ids and modification times only, so a
    /// conditional request that ends in 304 never builds a catalog entry (no media stream queries). The returned
    /// <see cref="CatalogPlan.Build"/> reuses the catalog last built for the share when its ETag is the same.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <returns>The ETag and a builder for the body.</returns>
    public CatalogPlan Plan(PeerShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        var included = Included(share).ToList();
        included.Sort((a, b) => string.CompareOrdinal(a.Id.ToString("N"), b.Id.ToString("N")));
        var etag = ComputeEtag(included);
        return new CatalogPlan(etag, () =>
        {
            if (_cache.TryGetValue(share.Id, out var cached) && cached.Etag == etag)
            {
                return cached;
            }

            var items = included.Select(l => l.CreateItem()).ToList();
            items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            var built = new CatalogResponse(ProtocolInfo.Version, _identity.ServerName, etag, items);
            _cache[share.Id] = built;
            return built;
        });
    }

    /// <summary>
    /// Checks whether an item (or a series owning an episode) is part of the share's catalog, by looking up only that
    /// item (or the series' episodes) in the shared libraries: same rules as the catalog (not a placeholder or
    /// <c>.strm</c>, file exists), without reading the library.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <param name="itemId">The item or series id.</param>
    /// <returns><c>true</c> when the share exposes the item or one of the series' episodes.</returns>
    public bool IsExposed(PeerShare share, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (share.LibraryIds.Count == 0 || itemId == Guid.Empty)
        {
            return false;
        }

        var cfg = _config.Current;
        return _source.GetExposureCandidates(itemId, share.LibraryIds.ToArray())
            .Any(local => (local.Id == itemId || local.SeriesId == itemId) && IsServable(local, cfg));
    }

    private bool IsServable(LocalItem local, JellyShareSettings cfg)
    {
        var path = local.Path;
        return !string.IsNullOrWhiteSpace(path)
            && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            && !PlaceholderPaths.IsPlaceholder(path, cfg)
            && _fileExists(path);
    }

    private IEnumerable<LocalItem> Included(PeerShare share)
    {
        if (share.LibraryIds.Count == 0)
        {
            yield break;
        }

        var cfg = _config.Current;
        foreach (var local in _source.GetItems(share.LibraryIds.ToArray()).Where(l => IsServable(l, cfg)))
        {
            yield return local;
        }
    }

    private static string ComputeEtag(IReadOnlyList<LocalItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append(item.Id.ToString("N")).Append('|').Append(item.DateModified.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
