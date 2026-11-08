using Jellyfin.Data.Enums;
using JellyShare.Application.Features.Placeholders;
using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Models.Placeholders;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Adapters;

/// <summary>
/// <see cref="IPlaceholderMetadata"/> on the Jellyfin library: setters on the item, <c>Name</c>/<c>Cast</c> locked so the
/// <c>.strm</c> probe (PlaybackInfo) keeps them, saved as a metadata edit; then the artists are created and the albums
/// fully refreshed so they pick up their album artist (docs/spike-results.md, audio playback spike).
/// </summary>
public sealed class JellyfinAudioMetadata : IPlaceholderMetadata
{
    private static readonly MetadataField[] _locks = { MetadataField.Name, MetadataField.Cast };

    private readonly ILibraryManager _library;
    private readonly IFileSystem _fileSystem;
    private readonly ISettingsProvider _config;
    private readonly IRelayBase _relayBase;
    private readonly ILogger<JellyfinAudioMetadata> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinAudioMetadata"/> class.</summary>
    /// <param name="library">The library manager.</param>
    /// <param name="fileSystem">The file system (album refresh).</param>
    /// <param name="config">The settings (placeholder roots).</param>
    /// <param name="relayBase">The relay base URL.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinAudioMetadata(
        ILibraryManager library,
        IFileSystem fileSystem,
        ISettingsProvider config,
        IRelayBase relayBase,
        ILogger<JellyfinAudioMetadata> logger)
    {
        _library = library;
        _fileSystem = fileSystem;
        _config = config;
        _relayBase = relayBase;
        _logger = logger;
    }

    /// <inheritdoc />
    public IDisposable SubscribeAdded(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EventHandler<ItemChangeEventArgs> added = (_, e) =>
        {
            // Runs inside Jellyfin's scan: never throw, never block.
            try
            {
                if (e.Item is Audio && PlaceholderPaths.IsPlaceholder(e.Item.Path, _config.Current))
                {
                    handler();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JellyShare: added item not checked");
            }
        };
        _library.ItemAdded += added;
        return new Subscription(() => _library.ItemAdded -= added);
    }

    /// <inheritdoc />
    public PlaceholderAudio ReadTracks(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var prefixes = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar).ToList();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relay = _relayBase.Current;

        // shortcut: Jellyfin has no path-prefix query, so all audio items are read and filtered here; fine for a pass
        // per scan, upgrade if very large music libraries make it slow.
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.AudioBook },
            IsVirtualItem = false,
            Recursive = true,
        });

        var cfg = _config.Current;
        var tracks = new List<PlaceholderTrack>();
        var ownerArtists = new List<string>();
        foreach (var audio in items.OfType<Audio>())
        {
            var path = audio.Path;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (!path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && !PlaceholderPaths.IsPlaceholder(path, cfg))
            {
                ownerArtists.AddRange(audio.AlbumArtists ?? []);
                ownerArtists.AddRange(audio.Artists ?? []);
                continue;
            }

            if (!path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !prefixes.Any(p => path.StartsWith(p, comparison)))
            {
                continue;
            }

            // Multi-file audiobook folders are resolved without IsShortcut, so ShortcutPath may be empty: read the file.
            var url = string.IsNullOrWhiteSpace(audio.ShortcutPath) ? FirstLine(path) : audio.ShortcutPath;
            if (RelayUrl.TryGetKey(url, relay, out var key))
            {
                tracks.Add(new PlaceholderTrack(audio.Id, key, Current(audio)));
            }
        }

        return new PlaceholderAudio(tracks, ownerArtists);
    }

    /// <inheritdoc />
    public async Task WriteAsync(IReadOnlyList<PlaceholderTrack> tracks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var artists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var albums = new Dictionary<Guid, MusicAlbum>();
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (_library.GetItemById(track.ItemId) is not Audio audio)
            {
                continue;
            }

            var meta = track.Metadata;
            audio.Name = meta.Name;
            audio.Artists = meta.Artists.ToArray();
            audio.AlbumArtists = meta.AlbumArtists.ToArray();
            audio.Album = meta.Album;
            audio.IndexNumber = meta.IndexNumber;
            audio.ParentIndexNumber = meta.ParentIndexNumber;
            audio.LockedFields = (audio.LockedFields ?? []).Union(_locks).ToArray();
            try
            {
                await audio.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "JellyShare: could not save the metadata of a placeholder item");
                continue;
            }

            artists.UnionWith(meta.Artists.Concat(meta.AlbumArtists));
            if (audio is not AudioBook && audio.AlbumEntity is { } album)
            {
                albums[album.Id] = album;
            }
        }

        foreach (var name in artists)
        {
            // Creates the artist item now; otherwise a new artist only shows after the next full library scan.
            TryRun(() => _library.GetArtist(name), "create an artist");
        }

        foreach (var album in albums.Values)
        {
            // A Default refresh leaves the album without its album artist (spike); a full one takes it from the tracks.
            try
            {
                await album.RefreshMetadata(
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem)) { MetadataRefreshMode = MetadataRefreshMode.FullRefresh },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "JellyShare: could not refresh a placeholder album");
            }
        }
    }

    private static TrackMetadata Current(Audio audio)
        => new(
            audio.Name ?? string.Empty,
            audio.Artists?.ToArray() ?? [],
            audio.AlbumArtists?.ToArray() ?? [],
            audio.Album,
            audio.IndexNumber,
            audio.ParentIndexNumber,
            _locks.All(f => audio.LockedFields?.Contains(f) == true));

    private string? FirstLine(string path)
    {
        try
        {
            return File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "JellyShare: placeholder file unreadable");
            return null;
        }
    }

    private void TryRun(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "JellyShare: could not {What}", what);
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
