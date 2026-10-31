using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyShare.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary><see cref="ILocalItemSource"/> backed by the Jellyfin library.</summary>
public sealed class JellyfinLocalItemSource : ILocalItemSource
{
    private static readonly ImageType[] SharedImageTypes = { ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb };

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLocalItemSource"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    public JellyfinLocalItemSource(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    public IEnumerable<LocalItem> GetItems(IReadOnlyCollection<Guid> libraryIds)
    {
        if (libraryIds.Count == 0)
        {
            return Array.Empty<LocalItem>();
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            AncestorIds = libraryIds.ToArray(),
            IsVirtualItem = false,
            Recursive = true,
        });

        // Series and media streams are only looked up for items that pass the share filters (see LocalItem.Factory).
        var series = new Lazy<Dictionary<Guid, BaseItem>>(() =>
        {
            var seriesIds = items.OfType<Episode>()
                .Select(e => e.SeriesId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            return seriesIds.Length == 0
                ? new Dictionary<Guid, BaseItem>()
                : _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = seriesIds }).ToDictionary(s => s.Id);
        });

        var result = new List<LocalItem>(items.Count);
        foreach (var item in items)
        {
            switch (item)
            {
                case Movie movie:
                    result.Add(new LocalItem(movie.Id, "Movie", movie.Path, null, () => ToCatalogItem(movie, "Movie", null)));
                    break;
                case Episode episode:
                    var seriesId = episode.SeriesId == Guid.Empty ? (Guid?)null : episode.SeriesId;
                    result.Add(new LocalItem(
                        episode.Id,
                        "Episode",
                        episode.Path,
                        seriesId,
                        () =>
                        {
                            series.Value.TryGetValue(episode.SeriesId, out var parent);
                            return ToCatalogItem(episode, "Episode", parent);
                        }));
                    break;
            }
        }

        return result;
    }
    /// <inheritdoc />
    public string? GetImagePath(Guid itemId, string imageType)
    {
        if (!Enum.TryParse<ImageType>(imageType, true, out var type) || !SharedImageTypes.Contains(type))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var info = item.ImageInfos?.FirstOrDefault(i => i.Type == type && i.IsLocalFile);
        return info?.Path;
    }

    private CatalogItem ToCatalogItem(BaseItem item, string type, BaseItem? series)
    {
        var episode = item as Episode;
        var streams = _mediaSourceManager.GetMediaStreams(item.Id);
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = streams
            .Where(s => s.Type == MediaStreamType.Audio)
            .Select(s => new AudioTrack(s.Language, s.Codec, s.Channels, s.Title ?? s.DisplayTitle))
            .ToArray();
        var subtitles = streams
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .Select(s => new SubtitleTrack(s.Language, s.Codec, s.Title ?? s.DisplayTitle))
            .ToArray();
        var bitRate = item.TotalBitrate ?? video?.BitRate;

        return new CatalogItem(
            item.Id.ToString("N"),
            type,
            item.Name,
            item.ProductionYear,
            CopyIds(item.ProviderIds),
            episode is null ? null : (episode.SeriesName ?? series?.Name),
            episode is null || series is null ? null : CopyIds(series.ProviderIds),
            episode?.ParentIndexNumber,
            episode?.IndexNumber,
            item.RunTimeTicks,
            item.Overview,
            new MediaInfo(item.Container, video?.Codec, video?.Width, video?.Height, bitRate, audio, subtitles),
            SharedImageTypes
                .Where(t => item.ImageInfos?.Any(i => i.Type == t && i.IsLocalFile) == true)
                .Select(t => t.ToString())
                .ToArray(),
            item.DateModified,
            episode is null || episode.SeriesId == Guid.Empty ? null : episode.SeriesId.ToString("N"));
    }

    private static Dictionary<string, string> CopyIds(Dictionary<string, string>? ids)
    {
        var copy = new Dictionary<string, string>();
        if (ids is null)
        {
            return copy;
        }

        foreach (var (key, value) in ids)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                copy[key] = value;
            }
        }

        return copy;
    }
}
