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

/// <summary>Builds the catalog a share exposes to its peer.</summary>
public class CatalogBuilder
{
    private static readonly TimeSpan IndexTtl = TimeSpan.FromSeconds(15);

    private readonly ILocalItemSource _source;
    private readonly IServerIdentity _identity;
    private readonly IConfigProvider _config;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<Guid, (string Key, DateTime Expires, HashSet<Guid> Ids)> _index = new();
    private readonly ConcurrentDictionary<Guid, object> _locks = new();

    /// <summary>Initializes a new instance of the <see cref="CatalogBuilder"/> class.</summary>
    /// <param name="source">The local item source.</param>
    /// <param name="identity">The local server identity.</param>
    /// <param name="config">The configuration provider.</param>
    /// <param name="fileExists">Existence check for item files; defaults to <see cref="File.Exists(string)"/>.</param>
    /// <param name="utcNow">Clock used for the index cache; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public CatalogBuilder(
        ILocalItemSource source,
        IServerIdentity identity,
        IConfigProvider config,
        Func<string, bool>? fileExists = null,
        Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
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
        var items = Included(share).Select(l => l.CreateItem()).ToList();
        items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
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
        if (TryGetIndex(share.Id, key, out var ids))
        {
            return ids.Contains(itemId);
        }

        // Single-flight: concurrent callers for the same share wait for one rebuild instead of each running a full query.
        lock (_locks.GetOrAdd(share.Id, _ => new object()))
        {
            if (TryGetIndex(share.Id, key, out ids))
            {
                return ids.Contains(itemId);
            }

            ids = new HashSet<Guid>();
            foreach (var local in Included(share))
            {
                ids.Add(local.Id);
                if (local.SeriesId is { } seriesId && seriesId != Guid.Empty)
                {
                    ids.Add(seriesId);
                }
            }

            _index[share.Id] = (key, _utcNow() + IndexTtl, ids);
            return ids.Contains(itemId);
        }
    }

    private bool TryGetIndex(Guid shareId, string key, out HashSet<Guid> ids)
    {
        if (_index.TryGetValue(shareId, out var cached) && cached.Key == key && cached.Expires > _utcNow())
        {
            ids = cached.Ids;
            return true;
        }

        ids = null!;
        return false;
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
            var path = local.Path;
            if (string.IsNullOrWhiteSpace(path)
                || path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || PlaceholderPaths.IsPlaceholder(path, cfg)
                || !_fileExists(path))
            {
                continue;
            }

            yield return local;
        }
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
