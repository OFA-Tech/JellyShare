using Jellyfin.Plugin.JellyShare.Keys;
using Jellyfin.Plugin.JellyShare.Placeholders;
using Jellyfin.Plugin.JellyShare.Protocol;
using Jellyfin.Plugin.JellyShare.Provider;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>A local movie or episode, with just what is needed for its keys.</summary>
/// <param name="Type"><c>Movie</c> or <c>Episode</c>.</param>
/// <param name="Path">The media file path.</param>
/// <param name="ProviderIds">The item's provider ids.</param>
/// <param name="SeriesProviderIds">For episodes, the series' provider ids.</param>
/// <param name="SeasonNumber">For episodes, the season number.</param>
/// <param name="EpisodeNumber">For episodes, the episode number.</param>
public sealed record LocalMedia(
    string Type,
    string? Path,
    IReadOnlyDictionary<string, string>? ProviderIds,
    IReadOnlyDictionary<string, string>? SeriesProviderIds,
    int? SeasonNumber,
    int? EpisodeNumber);

/// <summary>A local series.</summary>
/// <param name="Name">The series title.</param>
/// <param name="Path">The series folder.</param>
/// <param name="ProviderIds">The series provider ids.</param>
public sealed record LocalSeriesItem(string Name, string? Path, IReadOnlyDictionary<string, string>? ProviderIds);

/// <summary>Everything the local index reads from the library, in one pass.</summary>
/// <param name="Media">All movies and episodes in all libraries.</param>
/// <param name="Series">All series in all libraries.</param>
public sealed record LocalLibrarySnapshot(IReadOnlyList<LocalMedia> Media, IReadOnlyList<LocalSeriesItem> Series);

/// <summary>Reads the local library.</summary>
public interface ILocalLibrary
{
    /// <summary>Reads all movies, episodes and series.</summary>
    /// <returns>The snapshot.</returns>
    LocalLibrarySnapshot Read();
}

/// <summary>The titles the owner has locally.</summary>
/// <param name="MovieKeys">Keys of local movies whose file exists.</param>
/// <param name="EpisodeKeys">Keys of local episodes whose file exists.</param>
/// <param name="Series">Local series keyed <c>tvdb:{id}</c>/<c>imdb:{id}</c>/<c>tmdb:{id}</c>.</param>
/// <param name="UncheckedFiles">Files not checked within the time budget (counted as present).</param>
/// <param name="MissingFiles">Files that were checked and do not exist.</param>
public sealed record LocalIndexResult(
    ISet<string> MovieKeys,
    ISet<string> EpisodeKeys,
    IReadOnlyDictionary<string, LocalSeries> Series,
    int UncheckedFiles,
    int MissingFiles)
{
    /// <summary>Gets the keys for a media type.</summary>
    /// <param name="mediaType"><c>Movie</c> or <c>Episode</c>.</param>
    /// <returns>The keys.</returns>
    public ISet<string> KeysFor(string mediaType)
        => string.Equals(mediaType, "Movie", StringComparison.OrdinalIgnoreCase) ? MovieKeys : EpisodeKeys;
}

/// <summary>Builds the set of titles the owner has locally.</summary>
public interface ILocalIndex
{
    /// <summary>Builds the index.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The local keys and series.</returns>
    Task<LocalIndexResult> BuildAsync(CancellationToken ct);
}

/// <summary>
/// Local, non-placeholder items in all libraries whose file exists. Existence checks run off the caller's thread
/// under a time budget: a hung mount can block <c>stat</c>, so files not checked in time count as present
/// (never create placeholders over owned titles because a mount is slow).
/// </summary>
public sealed class LocalIndex : ILocalIndex
{
    /// <summary>Default time budget for the file existence checks.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    private static readonly MediaInfo EmptyMedia = new(null, null, null, null, null, [], []);

    private readonly ILocalLibrary _library;
    private readonly IConfigProvider _config;
    private readonly Func<string, bool> _fileExists;
    private readonly TimeSpan _budget;

    /// <summary>Initializes a new instance of the <see cref="LocalIndex"/> class.</summary>
    /// <param name="library">The library reader.</param>
    /// <param name="config">The plugin configuration (placeholder roots).</param>
    /// <param name="fileExists">Existence check; defaults to <see cref="File.Exists(string)"/>.</param>
    /// <param name="budget">Time budget for all existence checks; defaults to <see cref="DefaultBudget"/>.</param>
    public LocalIndex(ILocalLibrary library, IConfigProvider config, Func<string, bool>? fileExists = null, TimeSpan? budget = null)
    {
        _library = library;
        _config = config;
        _fileExists = fileExists ?? File.Exists;
        _budget = budget ?? DefaultBudget;
    }

    /// <summary>Gets the keys of local titles of a type.</summary>
    /// <param name="mediaType"><c>Movie</c> or <c>Episode</c>.</param>
    /// <returns>The keys.</returns>
    public ISet<string> GetLocalKeys(string mediaType)
        => BuildAsync(CancellationToken.None).GetAwaiter().GetResult().KeysFor(mediaType);

    /// <inheritdoc />
    public async Task<LocalIndexResult> BuildAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var snapshot = _library.Read();

        var candidates = new List<(LocalMedia Media, IReadOnlyList<string> Keys)>();
        foreach (var media in snapshot.Media)
        {
            if (string.IsNullOrWhiteSpace(media.Path)
                || media.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || PlaceholderPaths.IsPlaceholder(media.Path, cfg))
            {
                continue;
            }

            var keys = MediaKey.For(ToItem(media));
            if (keys.Count > 0)
            {
                candidates.Add((media, keys));
            }
        }

        // 0 = not checked yet, 1 = exists, 2 = missing.
        var states = new int[candidates.Count];
        var stop = 0;
        var check = Task.Run(
            () =>
            {
                for (var i = 0; i < candidates.Count && Volatile.Read(ref stop) == 0; i++)
                {
                    bool exists;
                    try
                    {
                        exists = _fileExists(candidates[i].Media.Path!);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        exists = true;
                    }

                    Volatile.Write(ref states[i], exists ? 1 : 2);
                }
            },
            CancellationToken.None);

        try
        {
            await check.WaitAsync(_budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref stop, 1);
        }

        var movies = new HashSet<string>(StringComparer.Ordinal);
        var episodes = new HashSet<string>(StringComparer.Ordinal);
        int unchecked_ = 0, missing = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var state = Volatile.Read(ref states[i]);
            if (state == 2)
            {
                missing++;
                continue;
            }

            if (state == 0)
            {
                unchecked_++;
            }

            var target = string.Equals(candidates[i].Media.Type, "Movie", StringComparison.Ordinal) ? movies : episodes;
            target.UnionWith(candidates[i].Keys);
        }

        return new LocalIndexResult(movies, episodes, BuildSeries(snapshot.Series, cfg), unchecked_, missing);
    }

    private static Dictionary<string, LocalSeries> BuildSeries(
        IReadOnlyList<LocalSeriesItem> series,
        Configuration.PluginConfiguration cfg)
    {
        var result = new Dictionary<string, LocalSeries>(StringComparer.Ordinal);
        foreach (var s in series)
        {
            if (s.ProviderIds is null || string.IsNullOrWhiteSpace(s.Name) || PlaceholderPaths.IsPlaceholder(s.Path, cfg))
            {
                continue;
            }

            var local = new LocalSeries(s.Name, new Dictionary<string, string>(s.ProviderIds));
            foreach (var kind in LocalSeries.SeriesKinds)
            {
                if (local.IdOf(kind) is { } id)
                {
                    result.TryAdd(kind + ":" + id, local);
                }
            }
        }

        return result;
    }

    private static CatalogItem ToItem(LocalMedia m)
        => new(
            string.Empty,
            m.Type,
            string.Empty,
            null,
            Copy(m.ProviderIds) ?? new Dictionary<string, string>(),
            null,
            Copy(m.SeriesProviderIds),
            m.SeasonNumber,
            m.EpisodeNumber,
            null,
            null,
            EmptyMedia,
            [],
            default);

    private static Dictionary<string, string>? Copy(IReadOnlyDictionary<string, string>? ids)
        => ids is null ? null : new Dictionary<string, string>(ids);
}
