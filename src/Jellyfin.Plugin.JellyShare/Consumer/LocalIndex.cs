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
/// <param name="Roots">The physical library folders.</param>
public sealed record LocalLibrarySnapshot(IReadOnlyList<LocalMedia> Media, IReadOnlyList<LocalSeriesItem> Series, IReadOnlyList<string>? Roots = null);

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
/// <param name="UncheckedFiles">
/// Files counted as present without proof: not checked within the time budget, probe errors, or under a missing,
/// unreadable or empty library folder.
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
}


/// <summary>
/// Local, non-placeholder items in all libraries whose file exists. A title only counts as missing when its file is
/// positively reported as not found inside a reachable library folder; everything uncertain counts as present
/// (never create placeholders over owned titles):
/// <list type="bullet">
/// <item>probe errors other than not-found (permissions, I/O, stale handles) → present;</item>
/// <item>a library folder that is missing, unreadable or empty (unmounted share, offline drive) → all its items present;</item>
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
    private readonly Func<string, bool> _rootReachable;
    private readonly TimeSpan _budget;

    /// <summary>Initializes a new instance of the <see cref="LocalIndex"/> class.</summary>
    /// <param name="library">The library reader.</param>
    /// <param name="config">The plugin configuration (placeholder roots).</param>
    /// <param name="probe">File probe; defaults to <see cref="ProbeFile"/>.</param>
    /// <param name="budget">Time budget for all checks; defaults to <see cref="DefaultBudget"/>.</param>
    /// <param name="rootReachable">Library folder check; defaults to <see cref="IsRootReachable"/>.</param>
    public LocalIndex(
        ILocalLibrary library,
        IConfigProvider config,
        Func<string, FilePresence>? probe = null,
        TimeSpan? budget = null,
        Func<string, bool>? rootReachable = null)
    {
        _library = library;
        _config = config;
        _probe = probe ?? ProbeFile;
        _rootReachable = rootReachable ?? IsRootReachable;
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

    /// <summary>Checks that a library folder exists, can be listed and is not empty.</summary>
    /// <param name="root">The library folder.</param>
    /// <returns><c>true</c> when its items can be judged by their own files.</returns>
    public static bool IsRootReachable(string root)
    {
        try
        {
            return Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
        }
        catch (Exception)
        {
            return false;
        }
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
        var roots = (snapshot.Roots ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => (Raw: r, Norm: Normalize(r)))
            .Where(r => r.Norm.Length > 0)
            .OrderByDescending(r => r.Norm.Length)
            .ToList();

        var candidates = new List<(LocalMedia Media, IReadOnlyList<string> Keys, string? Root)>();
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
                var norm = Normalize(media.Path);
                var root = roots.FirstOrDefault(r => norm.StartsWith(r.Norm + "/", PathComparison)).Raw;
                candidates.Add((media, keys, root));
            }
        }

        var states = new int[candidates.Count];
        var stop = 0;
        var check = Task.Run(
            () =>
            {
                var reachable = new Dictionary<string, bool>(StringComparer.Ordinal);
                for (var i = 0; i < candidates.Count && Volatile.Read(ref stop) == 0; i++)
                {
                    var root = candidates[i].Root;
                    if (root is not null)
                    {
                        if (!reachable.TryGetValue(root, out var ok))
                        {
                            ok = SafeRootCheck(root);
                            reachable[root] = ok;
                        }

                        if (!ok)
                        {
                            Volatile.Write(ref states[i], AssumedPresent);
                            continue;
                        }
                    }

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

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    private bool SafeRootCheck(string root)
    {
        try
        {
            return _rootReachable(root);
        }
        catch (Exception)
        {
            return false;
        }
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
