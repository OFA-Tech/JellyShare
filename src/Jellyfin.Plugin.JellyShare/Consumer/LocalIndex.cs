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
public sealed record LocalLibrarySnapshot(IReadOnlyList<LocalMedia> Media, IReadOnlyList<LocalSeriesItem> Series)
{
    /// <summary>Gets the configured library folder paths (all libraries).</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];
}

/// <summary>Result of a file existence probe.</summary>
public enum FilePresence
{
    /// <summary>The file exists.</summary>
    Present,

    /// <summary>The file or its folder does not exist.</summary>
    Missing,

    /// <summary>The probe failed (permissions, I/O error); treated as present.</summary>
    Unknown,
}

/// <summary>
/// Which local titles a targeted reconcile needs: movies with any of <see cref="MovieIds"/>, series with any of
/// <see cref="SeriesIds"/> and their episodes. Provider kinds and ids are lowercase.
/// </summary>
/// <param name="MovieIds">Movie provider ids by kind (<c>tmdb</c>, <c>imdb</c>).</param>
/// <param name="SeriesIds">Series provider ids by kind (<c>tvdb</c>, <c>imdb</c>, <c>tmdb</c>).</param>
public sealed record LibraryScope(
    IReadOnlyDictionary<string, IReadOnlySet<string>> MovieIds,
    IReadOnlyDictionary<string, IReadOnlySet<string>> SeriesIds)
{
    /// <summary>Gets a value indicating whether nothing is asked for.</summary>
    public bool IsEmpty => MovieIds.Count == 0 && SeriesIds.Count == 0;

    /// <summary>Builds the scope covering merged entries (their keys and, for episodes, all their series ids).</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The scope.</returns>
    public static LibraryScope ForEntries(IEnumerable<MergedEntry> entries)
    {
        var movies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var series = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, HashSet<string>> map, string kind, string id)
        {
            if (!map.TryGetValue(kind, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                map[kind] = set;
            }

            set.Add(id);
        }

        foreach (var entry in entries)
        {
            if (entry.Sources.Count == 0)
            {
                continue;
            }

            if (entry.Sources[0].Item.Type == "Episode")
            {
                foreach (var (kind, id) in PathNaming.SeriesIds(entry))
                {
                    Add(series, kind, id);
                }
            }
            else
            {
                foreach (var key in entry.Keys)
                {
                    // movie:{kind}:{id}
                    var parts = key.Split(':');
                    if (parts.Length == 3 && parts[0] == "movie")
                    {
                        Add(movies, parts[1], parts[2]);
                    }
                }
            }
        }

        return new LibraryScope(
            movies.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            series.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal));
    }
}

/// <summary>Reads the local library.</summary>
public interface ILocalLibrary
{
    /// <summary>Reads all movies, episodes and series.</summary>
    /// <returns>The snapshot.</returns>
    LocalLibrarySnapshot Read();

    /// <summary>Reads only the titles in <paramref name="scope"/> (provider-id queries, no full library read).</summary>
    /// <param name="scope">The provider ids of interest.</param>
    /// <returns>The matching movies, series and those series' episodes (no locations).</returns>
    LocalLibrarySnapshot Read(LibraryScope scope);

    /// <summary>Gets the folders of all libraries.</summary>
    /// <returns>The library locations.</returns>
    IReadOnlyList<string> GetLocations();
}

/// <summary>The titles the owner has locally.</summary>
/// <param name="MovieKeys">Keys of local movies whose file exists.</param>
/// <param name="EpisodeKeys">Keys of local episodes whose file exists.</param>
/// <param name="Series">Local series keyed <c>tvdb:{id}</c>/<c>imdb:{id}</c>/<c>tmdb:{id}</c>.</param>
/// <param name="UncheckedFiles">
/// Files counted as present without proof: not checked within the time budget, or probe errors other than not-found.
/// </param>
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

    /// <summary>
    /// Builds the index for the titles that have any of the given keys, reading only the titles in
    /// <paramref name="scope"/> from the library; only those files are checked.
    /// </summary>
    /// <param name="keys">The keys of interest.</param>
    /// <param name="scope">The provider ids to read from the library (covering <paramref name="keys"/>).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The local keys (restricted to titles matching <paramref name="keys"/>) and the series in scope.</returns>
    Task<LocalIndexResult> BuildForKeysAsync(IReadOnlySet<string> keys, LibraryScope scope, CancellationToken ct);

    /// <summary>Gets the folders of all libraries.</summary>
    /// <returns>The library locations.</returns>
    IReadOnlyList<string> LibraryLocations();
}

/// <summary>
/// Local, non-placeholder items in all libraries whose file exists. A title counts as missing (so a peer copy is used,
/// spec goal 3) only when its file is reported as not found — this includes a deleted file, an offline drive, a
/// missing library folder or an empty mount point. Anything uncertain counts as present:
/// <list type="bullet">
/// <item>probe errors other than not-found (permissions, I/O, stale handles) → present;</item>
/// <item>checks run off the caller's thread under a time budget (hung mounts) → items not checked in time present.</item>
/// </list>
/// </summary>
public sealed class LocalIndex : ILocalIndex
{
    /// <summary>Default time budget for the file existence checks.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    private const int NotChecked = 0;
    private const int Exists = 1;
    private const int Missing = 2;
    private const int AssumedPresent = 3;

    private static readonly MediaInfo EmptyMedia = new(null, null, null, null, null, [], []);

    private readonly ILocalLibrary _library;
    private readonly IConfigProvider _config;
    private readonly Func<string, FilePresence> _probe;
    private readonly TimeSpan _budget;

    /// <summary>Initializes a new instance of the <see cref="LocalIndex"/> class.</summary>
    /// <param name="library">The library reader.</param>
    /// <param name="config">The plugin configuration (placeholder roots).</param>
    /// <param name="probe">File probe; defaults to <see cref="ProbeFile"/>.</param>
    /// <param name="budget">Time budget for all checks; defaults to <see cref="DefaultBudget"/>.</param>
    public LocalIndex(
        ILocalLibrary library,
        IConfigProvider config,
        Func<string, FilePresence>? probe = null,
        TimeSpan? budget = null)
    {
        _library = library;
        _config = config;
        _probe = probe ?? ProbeFile;
        _budget = budget ?? DefaultBudget;
    }

    /// <summary>
    /// Probes a file: <see cref="FilePresence.Missing"/> only for file/directory not found; any other error
    /// (unlike <see cref="File.Exists(string)"/>, which returns <c>false</c> for those) is <see cref="FilePresence.Unknown"/>.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The presence.</returns>
    public static FilePresence ProbeFile(string path)
    {
        try
        {
            File.GetAttributes(path);
            return FilePresence.Present;
        }
        catch (FileNotFoundException)
        {
            return FilePresence.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FilePresence.Missing;
        }
        catch (Exception)
        {
            return FilePresence.Unknown;
        }
    }

    /// <summary>Gets the keys of local titles of a type.</summary>
    /// <param name="mediaType"><c>Movie</c> or <c>Episode</c>.</param>
    /// <returns>The keys.</returns>
    public ISet<string> GetLocalKeys(string mediaType)
        => BuildAsync(CancellationToken.None).GetAwaiter().GetResult().KeysFor(mediaType);

    /// <summary>Computes the keys of a local item.</summary>
    /// <param name="media">The item.</param>
    /// <returns>The keys; empty when the item has no usable provider ids.</returns>
    internal static IReadOnlyList<string> KeysFor(LocalMedia media) => MediaKey.For(ToItem(media));

    /// <inheritdoc />
    public Task<LocalIndexResult> BuildAsync(CancellationToken ct) => BuildCoreAsync(null, null, ct);

    /// <inheritdoc />
    public Task<LocalIndexResult> BuildForKeysAsync(IReadOnlySet<string> keys, LibraryScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(scope);
        return BuildCoreAsync(keys, scope, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> LibraryLocations() => _library.GetLocations();

    private async Task<LocalIndexResult> BuildCoreAsync(IReadOnlySet<string>? onlyKeys, LibraryScope? scope, CancellationToken ct)
    {
        var cfg = _config.Current;
        var snapshot = scope is null
            ? _library.Read()
            : scope.IsEmpty ? new LocalLibrarySnapshot([], []) : _library.Read(scope);

        var candidates = new List<(LocalMedia Media, IReadOnlyList<string> Keys)>();
        foreach (var media in snapshot.Media)
        {
            if (string.IsNullOrWhiteSpace(media.Path)
                || media.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || PlaceholderPaths.IsPlaceholder(media.Path, cfg))
            {
                continue;
            }

            var keys = KeysFor(media);
            if (keys.Count > 0 && (onlyKeys is null || keys.Any(onlyKeys.Contains)))
            {
                candidates.Add((media, keys));
            }
        }

        var states = new int[candidates.Count];
        var stop = 0;
        var check = Task.Run(
            () =>
            {
                for (var i = 0; i < candidates.Count && Volatile.Read(ref stop) == 0; i++)
                {
                    var presence = SafeProbe(candidates[i].Media.Path!);
                    Volatile.Write(ref states[i], presence switch
                    {
                        FilePresence.Present => Exists,
                        FilePresence.Missing => Missing,
                        _ => AssumedPresent,
                    });
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
        int unverified = 0, missing = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var state = Volatile.Read(ref states[i]);
            if (state == Missing)
            {
                missing++;
                continue;
            }

            if (state is NotChecked or AssumedPresent)
            {
                unverified++;
            }

            var target = string.Equals(candidates[i].Media.Type, "Movie", StringComparison.Ordinal) ? movies : episodes;
            target.UnionWith(candidates[i].Keys);
        }

        return new LocalIndexResult(movies, episodes, BuildSeries(snapshot.Series, cfg), unverified, missing);
    }


    private FilePresence SafeProbe(string path)
    {
        try
        {
            return _probe(path);
        }
        catch (Exception)
        {
            return FilePresence.Unknown;
        }
    }

    internal static Dictionary<string, LocalSeries> BuildSeries(
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
