using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace JellyShare.Jellyfin.Consumer;

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
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        });
        return ToSnapshot(seriesItems, items) with { Locations = GetLocations() };
    }

    /// <inheritdoc />
    public LocalLibrarySnapshot Read(LibraryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IReadOnlyList<BaseItem> movies = Array.Empty<BaseItem>();
        if (scope.MovieIds.Count > 0)
        {
            movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                HasAnyProviderIds = ProviderQuery(scope.MovieIds),
                IsVirtualItem = false,
                Recursive = true,
            });
        }

        IReadOnlyList<BaseItem> seriesItems = Array.Empty<BaseItem>();
        IReadOnlyList<BaseItem> episodes = Array.Empty<BaseItem>();
        if (scope.SeriesIds.Count > 0)
        {
            seriesItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                HasAnyProviderIds = ProviderQuery(scope.SeriesIds),
                Recursive = true,
            });
            if (seriesItems.Count > 0)
            {
                episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    AncestorIds = seriesItems.Select(s => s.Id).ToArray(),
                    IsVirtualItem = false,
                    Recursive = true,
                });
            }
        }

        return ToSnapshot(seriesItems, movies.Concat(episodes).ToList());
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetLocations()
        => _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static Dictionary<string, string[]> ProviderQuery(IReadOnlyDictionary<string, IReadOnlySet<string>> ids)
    {
        // Jellyfin stores provider names as Tmdb/Imdb/Tvdb; ids are matched as stored (numeric, or "tt…" for IMDb).
        var query = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, values) in ids)
        {
            var name = kind switch
            {
                "tmdb" => "Tmdb",
                "imdb" => "Imdb",
                "tvdb" => "Tvdb",
                _ => null,
            };
            if (name is not null && values.Count > 0)
            {
                query[name] = values.ToArray();
            }
        }

        return query;
    }

    private static LocalLibrarySnapshot ToSnapshot(IReadOnlyList<BaseItem> seriesItems, IReadOnlyList<BaseItem> items)
    {
        var seriesById = new Dictionary<Guid, BaseItem>();
        var series = new List<LocalSeriesItem>(seriesItems.Count);
        foreach (var s in seriesItems)
        {
            seriesById[s.Id] = s;
            series.Add(new LocalSeriesItem(s.Name, s.Path, Copy(s.ProviderIds)));
        }

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
