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
/// <param name="Item">The catalog entry for the item.</param>
public sealed record LocalItem(Guid Id, string Type, string? Path, CatalogItem Item);

/// <summary>Reads local library content, abstracted so the catalog logic stays unit-testable.</summary>
public interface ILocalItemSource
{
    /// <summary>Lists the movies and episodes (recursively) under the given libraries.</summary>
    /// <param name="libraryIds">The ids of the libraries to read.</param>
    /// <returns>The local items.</returns>
    IEnumerable<LocalItem> GetItems(IReadOnlyCollection<Guid> libraryIds);

    /// <summary>Gets the local image file of an item as Jellyfin recorded it.</summary>
    /// <param name="itemId">The item (or series) id.</param>
    /// <param name="imageType">One of <c>Primary</c>, <c>Backdrop</c>, <c>Logo</c>, <c>Thumb</c>.</param>
    /// <returns>The file path, or <c>null</c> when the item has no such local image.</returns>
    string? GetImagePath(Guid itemId, string imageType);
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

/// <summary>Builds the catalog a share exposes to its peer.</summary>
public class CatalogBuilder
{
    private static readonly TimeSpan IndexTtl = TimeSpan.FromSeconds(15);

    private readonly ILocalItemSource _source;
    private readonly IServerIdentity _identity;
    private readonly IConfigProvider _config;
    private readonly Func<string, bool> _fileExists;
    private readonly ConcurrentDictionary<Guid, (string Key, DateTime Expires, HashSet<Guid> Ids)> _index = new();

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

    /// <summary>Builds the catalog for a share.</summary>
    /// <param name="share">The share.</param>
    /// <returns>The catalog response including its ETag.</returns>
    public CatalogResponse Build(PeerShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        var items = Collect(share);
        return new CatalogResponse(ProtocolInfo.Version, _identity.ServerName, ComputeEtag(items), items);
    }

    /// <summary>
    /// Checks whether an item (or a series owning an episode) is part of the share's catalog.
    /// Uses a short-lived cache so bulk image fetches do not rebuild the catalog per request.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <param name="itemId">The item or series id.</param>
    /// <returns><c>true</c> when the share exposes the item or one of the series' episodes.</returns>
    public bool IsExposed(PeerShare share, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(share);
        var key = string.Join(',', share.LibraryIds.OrderBy(i => i));
        var now = DateTime.UtcNow;
        if (_index.TryGetValue(share.Id, out var cached) && cached.Key == key && cached.Expires > now)
        {
            return cached.Ids.Contains(itemId);
        }

        var ids = new HashSet<Guid>();
        foreach (var item in Collect(share))
        {
            if (Guid.TryParse(item.Id, out var id))
            {
                ids.Add(id);
            }

            if (item.SeriesId is not null && Guid.TryParse(item.SeriesId, out var seriesId))
            {
                ids.Add(seriesId);
            }
        }

        _index[share.Id] = (key, now + IndexTtl, ids);
        return ids.Contains(itemId);
    }

    private List<CatalogItem> Collect(PeerShare share)
    {
        if (share.LibraryIds.Count == 0)
        {
            return new List<CatalogItem>();
        }

        var cfg = _config.Current;
        var result = new List<CatalogItem>();
        foreach (var local in _source.GetItems(share.LibraryIds.ToArray()))
        {
            var path = local.Path;
            if (string.IsNullOrWhiteSpace(path)
                || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || PlaceholderPaths.IsPlaceholder(path, cfg)
                || !_fileExists(path))
            {
                continue;
            }

            result.Add(local.Item);
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    private static string ComputeEtag(IReadOnlyList<CatalogItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            sb.Append(item.Id).Append('|').Append(item.DateModified.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
