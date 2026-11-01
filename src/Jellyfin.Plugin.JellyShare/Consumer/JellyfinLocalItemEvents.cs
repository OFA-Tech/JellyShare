using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary><see cref="ILocalItemEvents"/> backed by <see cref="ILibraryManager.ItemAdded"/> / <see cref="ILibraryManager.ItemRemoved"/>.</summary>
public sealed class JellyfinLocalItemEvents : ILocalItemEvents
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<JellyfinLocalItemEvents> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLocalItemEvents"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinLocalItemEvents(ILibraryManager libraryManager, ILogger<JellyfinLocalItemEvents> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<LocalItemEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EventHandler<ItemChangeEventArgs> added = (_, e) => Forward(false, e, handler);
        EventHandler<ItemChangeEventArgs> removed = (_, e) => Forward(true, e, handler);
        _libraryManager.ItemAdded += added;
        _libraryManager.ItemRemoved += removed;
        return new Subscription(() =>
        {
            _libraryManager.ItemAdded -= added;
            _libraryManager.ItemRemoved -= removed;
        });
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

    private void Forward(bool removed, ItemChangeEventArgs args, Action<LocalItemEvent> handler)
    {
        // Runs synchronously inside Jellyfin's scan/delete: never throw, never block.
        try
        {
            var media = ToMedia(args.Item);
            if (media is not null)
            {
                var id = args.Item.Id;
                handler(new LocalItemEvent(removed, media, removed ? null : () => ToMedia(_libraryManager.GetItemById(id))));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyShare: library event could not be handled");
        }
    }

    private LocalMedia? ToMedia(BaseItem? item)
    {
        switch (item)
        {
            case Movie movie:
                return new LocalMedia("Movie", movie.Path, Copy(movie.ProviderIds), null, null, null);
            case Episode episode:
                // SeriesId is not always set yet when a new episode is added; the parent chain (season, series) is.
                BaseItem? series = episode.Series
                    ?? (episode.SeriesId == Guid.Empty ? null : _libraryManager.GetItemById(episode.SeriesId));
                return new LocalMedia(
                    "Episode",
                    episode.Path,
                    Copy(episode.ProviderIds),
                    series is null ? null : Copy(series.ProviderIds),
                    episode.ParentIndexNumber,
                    episode.IndexNumber);
            default:
                // Folder, Series, Season and everything else: only the movie/episode file matters.
                return null;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _onDispose;

        public Subscription(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
