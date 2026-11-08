using Jellyfin.Data.Enums;
using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using ShareMediaType = JellyShare.Domain.Enums.MediaType;

namespace JellyShare.Jellyfin.Adapters;

/// <summary><see cref="ILocalItemSource"/> backed by the Jellyfin library.</summary>
public sealed class JellyfinLocalItemSource : ILocalItemSource
{
    private static readonly ImageType[] _sharedImageTypes = { ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb };

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
    public IEnumerable<LocalItem> GetItems(IReadOnlySet<ShareMediaType> types)
    {
        var kinds = Kinds(types);
        if (kinds.Length == 0)
        {
            return Array.Empty<LocalItem>();
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            IsVirtualItem = false,
            Recursive = true,
        });

        return ToLocalItems(items);
    }

    /// <inheritdoc />
    public IEnumerable<LocalItem> GetExposureCandidates(Guid itemId, IReadOnlySet<ShareMediaType> types)
    {
        var kinds = Kinds(types);
        if (kinds.Length == 0 || itemId == Guid.Empty)
        {
            return Array.Empty<LocalItem>();
        }

        var item = _libraryManager.GetItemById(itemId);
        Guid[] ids;
        switch (item)
        {
            case Movie:
            case Episode:
            case Audio:
                ids = new[] { itemId };
                break;
            case Series:
                // The series' own episodes; the media type is checked below, like for a single item.
                ids = _libraryManager.GetItemIds(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    AncestorIds = new[] { itemId },
                    IsVirtualItem = false,
                    Recursive = true,
                }).ToArray();
                break;
            case MusicAlbum:
                ids = _libraryManager.GetItemIds(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    AncestorIds = new[] { itemId },
                    IsVirtualItem = false,
                    Recursive = true,
                }).ToArray();
                break;
            case Folder:
                // An audiobook's folder: the books directly inside it (their artwork is served under the folder id).
                ids = _libraryManager.GetItemIds(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.AudioBook },
                    ParentId = itemId,
                    IsVirtualItem = false,
                    Recursive = false,
                }).ToArray();
                break;
            default:
                return Array.Empty<LocalItem>();
        }

        if (ids.Length == 0)
        {
            return Array.Empty<LocalItem>();
        }

        var inShare = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = ids,
            IncludeItemTypes = kinds,
            IsVirtualItem = false,
            Recursive = true,
        });
        return ToLocalItems(inShare);
    }

    private static BaseItemKind[] Kinds(IReadOnlySet<ShareMediaType> types)
    {
        var kinds = new List<BaseItemKind>();
        if (types.Contains(ShareMediaType.Movies))
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (types.Contains(ShareMediaType.Shows))
        {
            kinds.Add(BaseItemKind.Episode);
        }

        if (types.Contains(ShareMediaType.Music))
        {
            kinds.Add(BaseItemKind.Audio);
        }

        if (types.Contains(ShareMediaType.Audiobooks))
        {
            kinds.Add(BaseItemKind.AudioBook);
        }

        return kinds.ToArray();
    }

    private List<LocalItem> ToLocalItems(IReadOnlyList<BaseItem> items)
    {
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
                    result.Add(new LocalItem(movie.Id, "Movie", movie.Path, null, () => ToCatalogItem(movie, "Movie", null))
                    {
                        DateModified = movie.DateModified,
                    });
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
                        })
                    {
                        DateModified = episode.DateModified,
                    });
                    break;
                case AudioBook book:
                    result.Add(new LocalItem(book.Id, "AudioBook", book.Path, BookFolderId(book), () => ToCatalogItem(book, "AudioBook", null))
                    {
                        DateModified = book.DateModified,
                    });
                    break;
                case Audio track:
                    result.Add(new LocalItem(track.Id, "Audio", track.Path, track.AlbumEntity?.Id, () => ToCatalogItem(track, "Audio", null))
                    {
                        DateModified = track.DateModified,
                    });
                    break;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public string? GetImagePath(Guid itemId, string imageType)
    {
        if (!Enum.TryParse<ImageType>(imageType, true, out var type) || !_sharedImageTypes.Contains(type))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        return (LocalImage(item, type) ?? LocalImage(ArtworkHolder(item), type))?.Path;
    }

    private static ItemImageInfo? LocalImage(BaseItem? item, ImageType type)
        => item?.ImageInfos?.FirstOrDefault(i => i.Type == type && i.IsLocalFile);

    /// <summary>The album (tracks) or the book's folder (audiobooks) that supplies artwork for audio without its own.</summary>
    private static BaseItem? ArtworkHolder(BaseItem item) => item switch
    {
        AudioBook book => BookFolder(book),
        Audio track => track.AlbumEntity,
        _ => null,
    };

    private static BaseItem? BookFolder(AudioBook book)
    {
        var parent = book.GetParent();
        return parent is null || parent.IsTopParent ? null : parent;
    }

    private static Guid? BookFolderId(AudioBook book) => BookFolder(book)?.Id;

    /// <inheritdoc />
    public StreamInfo? GetStreamInfo(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        switch (item)
        {
            case Movie:
            case Episode:
                var bitrate = item.TotalBitrate is > 0
                    ? item.TotalBitrate
                    : SumOfStreamBitrates(_mediaSourceManager.GetMediaStreams(item.Id));
                return new StreamInfo(item is Movie ? "Movie" : "Episode", item.Name, item.Path, bitrate, item.RunTimeTicks);
            case Audio:
                // Audio is never bitrate-capped; the title id makes one album (or book) count as one stream.
                return new StreamInfo(item is AudioBook ? "AudioBook" : "Audio", item.Name, item.Path, null, item.RunTimeTicks, ArtworkHolder(item)?.Id);
            case null:
                return null;
            default:
                return new StreamInfo(item.GetType().Name, item.Name, null, null);
        }
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
        var bitRate = EffectiveBitrate(item.TotalBitrate, streams);
        var track = item as Audio;
        var holder = track is null ? null : ArtworkHolder(track);
        var folderPath = item is AudioBook book && holder is not null
            ? AudiobookFolders.Relative(book.Path, _libraryManager.GetCollectionFolders(book).SelectMany(f => f.PhysicalLocations))
            : null;

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
            _sharedImageTypes
                .Where(t => LocalImage(item, t) is not null || LocalImage(holder, t) is not null)
                .Select(t => t.ToString())
                .ToArray(),
            item.DateModified,
            episode is null || episode.SeriesId == Guid.Empty ? null : episode.SeriesId.ToString("N"),
            track?.Album,
            track is { AlbumArtists.Count: > 0 } ? track.AlbumArtists[0] : null,
            track?.Artists.ToArray(),
            track?.ParentIndexNumber,
            track?.IndexNumber,
            holder?.Id.ToString("N"),
            folderPath);
    }

    /// <summary>The container total when positive, else the sum of the stream bitrates; <c>null</c> when unknown.</summary>
    /// <param name="total">The container's total bitrate as Jellyfin recorded it (0 means unknown).</param>
    /// <param name="streams">The media streams.</param>
    /// <returns>The bitrate in bits per second, or <c>null</c>.</returns>
    internal static int? EffectiveBitrate(int? total, IEnumerable<MediaStream> streams)
        => StreamBitrate.Effective(total, VideoAndAudioRates(streams));

    /// <summary>Video plus audio stream bitrates when the container has no total; <c>null</c> when none is known.</summary>
    internal static int? SumOfStreamBitrates(IEnumerable<MediaStream> streams)
        => StreamBitrate.Sum(VideoAndAudioRates(streams));

    private static IEnumerable<int?> VideoAndAudioRates(IEnumerable<MediaStream> streams)
        => streams.Where(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio).Select(s => s.BitRate);

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
