using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary><see cref="ILocalLibrary"/> backed by the Jellyfin library (all libraries, two queries).</summary>
public sealed class JellyfinLocalLibrary : ILocalLibrary
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLocalLibrary"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    public JellyfinLocalLibrary(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public LocalLibrarySnapshot Read()
    {
        var seriesItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true,
        });
        var seriesById = new Dictionary<Guid, BaseItem>();
        var series = new List<LocalSeriesItem>(seriesItems.Count);
        foreach (var s in seriesItems)
        {
            seriesById[s.Id] = s;
            series.Add(new LocalSeriesItem(s.Name, s.Path, Copy(s.ProviderIds)));
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        });
        var media = new List<LocalMedia>(items.Count);
        foreach (var item in items)
        {
            switch (item)
            {
                case Movie movie:
                    media.Add(new LocalMedia("Movie", movie.Path, Copy(movie.ProviderIds), null, null, null));
                    break;
                case Episode episode:
                    seriesById.TryGetValue(episode.SeriesId, out var parent);
                    media.Add(new LocalMedia(
                        "Episode",
                        episode.Path,
                        Copy(episode.ProviderIds),
                        parent is null ? null : Copy(parent.ProviderIds),
                        episode.ParentIndexNumber,
                        episode.IndexNumber));
                    break;
            }
        }

        return new LocalLibrarySnapshot(media, series);
    }

    private static Dictionary<string, string> Copy(Dictionary<string, string>? ids)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
