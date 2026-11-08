using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Models.Admin;
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
    private readonly IAdminLibraries _libraries;
    private static readonly TimeSpan _librariesTtl = TimeSpan.FromSeconds(30);

    private readonly Func<string, bool> _fileExists;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(Guid Share, string Types), CatalogResponse> _cache = new();
    private Tuple<DateTimeOffset, IReadOnlyList<LibraryInfo>>? _librariesCache;

    /// <summary>Initializes a new instance of the <see cref="CatalogBuilder"/> class.</summary>
    /// <param name="source">The local item source.</param>
    /// <param name="identity">The local server identity.</param>
    /// <param name="config">The configuration provider.</param>
    /// <param name="libraries">The libraries on this server, to resolve a share saved before media types existed.</param>
    /// <param name="fileExists">Existence check for item files; defaults to <see cref="File.Exists(string)"/>.</param>
    /// <param name="clock">The clock for the short library-list cache; defaults to <see cref="TimeProvider.System"/>.</param>
    public CatalogBuilder(
        ILocalItemSource source,
        IServerIdentity identity,
        ISettingsProvider config,
        IAdminLibraries libraries,
        Func<string, bool>? fileExists = null,
        TimeProvider? clock = null)
    {
        _source = source;
        _identity = identity;
        _config = config;
        _libraries = libraries;
        _fileExists = fileExists ?? File.Exists;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Builds the catalog for a share (served from the cache when nothing changed).</summary>
    /// <param name="share">The share.</param>
    /// <param name="requested">The media types the peer asks for.</param>
    /// <returns>The catalog response including its ETag.</returns>
    public CatalogResponse Build(PeerShare share, IReadOnlySet<MediaType> requested) => Plan(share, requested).Build();

    /// <summary>
    /// Lists the share's items and computes the catalog ETag from the served media types and the items' ids and
    /// modification times only, so a conditional request that ends in 304 never builds a catalog entry (no media
    /// stream queries). The returned <see cref="CatalogPlan.Build"/> reuses the catalog last built for the share and
    /// types when its ETag is the same.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <param name="requested">The media types the peer asks for; the catalog serves those the share offers and this server can read.</param>
    /// <returns>The ETag and a builder for the body.</returns>
    public CatalogPlan Plan(PeerShare share, IReadOnlySet<MediaType> requested)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(requested);
        var served = Offered(share);
        served.IntersectWith(requested);
        var servedKey = MediaTypes.Format(served);
        var cfg = _config.Current;
        var included = served.Count == 0 ? new List<LocalItem>() : _source.GetItems(served).Where(l => IsServable(l, cfg)).ToList();
        included.Sort((a, b) => string.CompareOrdinal(a.Id.ToString("N"), b.Id.ToString("N")));
        var etag = ComputeEtag(servedKey, included);
        return new CatalogPlan(etag, () =>
        {
            if (_cache.TryGetValue((share.Id, servedKey), out var cached) && cached.Etag == etag)
            {
                return cached;
            }

            var items = included.Select(l => l.CreateItem()).ToList();
            items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            var built = new CatalogResponse(ProtocolInfo.Version, _identity.ServerName, etag, items);
            _cache[(share.Id, servedKey)] = built;
            return built;
        });
    }

    /// <summary>
    /// Checks whether an item (or a series owning an episode) is part of the share's catalog, by looking up only that
    /// item (or the series' episodes) among the media types the share offers: same rules as the catalog (not a
    /// placeholder or <c>.strm</c>, file exists), without reading the library.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <param name="itemId">The item or series id.</param>
    /// <returns><c>true</c> when the share exposes the item or one of the series' episodes.</returns>
    public bool IsExposed(PeerShare share, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(share);
        var offered = Offered(share);
        if (offered.Count == 0 || itemId == Guid.Empty)
        {
            return false;
        }

        var cfg = _config.Current;
        return _source.GetExposureCandidates(itemId, offered)
            .Any(local => (local.Id == itemId || local.SeriesId == itemId) && IsServable(local, cfg));
    }

    private HashSet<MediaType> Offered(PeerShare share)
    {
        var libraries = share.UseDefaultTypes is null ? Libraries() : Array.Empty<LibraryInfo>();
        var offered = MediaTypes.EffectiveShare(share, _config.Current, libraries).ToHashSet();
        offered.IntersectWith(MediaTypes.Supported);
        return offered;
    }

    // Reading the library list hits the disk; IsExposed runs on every image and stream request of a legacy share.
    private IReadOnlyList<LibraryInfo> Libraries()
    {
        var now = _clock.GetUtcNow();
        if (_librariesCache is { } cached && now - cached.Item1 < _librariesTtl)
        {
            return cached.Item2;
        }

        var list = _libraries.List();
        _librariesCache = Tuple.Create(now, list);
        return list;
    }

    private bool IsServable(LocalItem local, JellyShareSettings cfg)
    {
        var path = local.Path;
        return !string.IsNullOrWhiteSpace(path)
            && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            && !PlaceholderPaths.IsPlaceholder(path, cfg)
            && _fileExists(path);
    }

    private static string ComputeEtag(string servedTypes, IReadOnlyList<LocalItem> items)
    {
        var sb = new StringBuilder(servedTypes).Append('\n');
        foreach (var item in items)
        {
            sb.Append(item.Id.ToString("N")).Append('|').Append(item.DateModified.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
