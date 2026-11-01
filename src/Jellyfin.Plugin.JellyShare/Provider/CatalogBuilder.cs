using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyShare.Configuration;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Protocol;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>A local movie or episode together with the data needed to filter it.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Type"><c>Movie</c> or <c>Episode</c>.</param>
/// <param name="Path">The file path of the item.</param>
/// <param name="SeriesId">For episodes, the id of the owning series.</param>
/// <param name="Factory">Creates the catalog entry. Only called for items that pass the share filters.</param>
public sealed record LocalItem(Guid Id, string Type, string? Path, Guid? SeriesId, Func<CatalogItem> Factory)
{
    /// <summary>Gets the item's last modification time (what the catalog ETag is computed from).</summary>
    public DateTime DateModified { get; init; }

    /// <summary>Creates the catalog entry for the item (may query the library; call only for included items).</summary>
    /// <returns>The catalog entry.</returns>
    public CatalogItem CreateItem() => Factory();
}

/// <summary>What the stream endpoint needs to know about a library item.</summary>
/// <param name="Type">The item type, e.g. <c>Movie</c>, <c>Episode</c> or <c>Series</c>.</param>
/// <param name="Name">The display name (for the usage log).</param>
/// <param name="Path">The file path, if any.</param>
/// <param name="Bitrate">The total bitrate in bits per second, when known.</param>
/// <param name="RunTimeTicks">The runtime in ticks, when known.</param>
public sealed record StreamInfo(string Type, string Name, string? Path, long? Bitrate, long? RunTimeTicks = null);

/// <summary>Reads local library content, abstracted so the catalog logic stays unit-testable.</summary>
public interface ILocalItemSource
{
    /// <summary>Lists the movies and episodes (recursively) under the given libraries.</summary>
    /// <param name="libraryIds">The ids of the libraries to read.</param>
    /// <returns>The local items.</returns>
    IEnumerable<LocalItem> GetItems(IReadOnlyCollection<Guid> libraryIds);

    /// <summary>
    /// Gets what decides whether one id is exposed, without reading the whole library: the movie or episode with that
    /// id, or the episodes of the series with that id, as far as they are in the given libraries.
    /// </summary>
    /// <param name="itemId">The item or series id.</param>
    /// <param name="libraryIds">The shared libraries.</param>
    /// <returns>The candidates (not yet filtered for placeholders or missing files).</returns>
    IEnumerable<LocalItem> GetExposureCandidates(Guid itemId, IReadOnlyCollection<Guid> libraryIds);

    /// <summary>Gets the local image file of an item as Jellyfin recorded it.</summary>
    /// <param name="itemId">The item (or series) id.</param>
    /// <param name="imageType">One of <c>Primary</c>, <c>Backdrop</c>, <c>Logo</c>, <c>Thumb</c>.</param>
    /// <returns>The file path, or <c>null</c> when the item has no such local image.</returns>
    string? GetImagePath(Guid itemId, string imageType);

    /// <summary>Gets the file path, name and bitrate of an item without building a catalog entry.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The stream info, or <c>null</c> when the item does not exist.</returns>
    StreamInfo? GetStreamInfo(Guid itemId);
}

/// <summary>Provides the current plugin configuration at request time.</summary>
public interface IConfigProvider
{
    /// <summary>Gets the current configuration.</summary>
    PluginConfiguration Current { get; }
}

/// <summary><see cref="IConfigProvider"/> reading <see cref="Plugin.Instance"/> on every access.</summary>
public sealed class PluginConfigProvider : IConfigProvider
{
    /// <inheritdoc />
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}

/// <summary>A share's catalog ETag, known before any catalog entry is built.</summary>
/// <param name="Etag">The ETag (lowercase hex SHA-256 over item ids and modification times).</param>
/// <param name="Build">Builds (or returns the cached) catalog for this ETag.</param>
public sealed record CatalogPlan(string Etag, Func<CatalogResponse> Build);

/// <summary>Builds the catalog a share exposes to its peer.</summary>
public class CatalogBuilder
{
    private readonly ILocalItemSource _source;
    private readonly IServerIdentity _identity;
    private readonly IConfigProvider _config;
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
        IConfigProvider config,
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

    private bool IsServable(LocalItem local, PluginConfiguration cfg)
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
        foreach (var local in _source.GetItems(share.LibraryIds.ToArray()))
        {
            if (IsServable(local, cfg))
            {
                yield return local;
            }
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
