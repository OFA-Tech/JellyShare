using JellyShare.Application.Features.Placeholders;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Application.Features.Sync;

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

    private static readonly MediaInfo _emptyMedia = new(null, null, null, null, null, [], []);

    private readonly ILocalLibrary _library;
    private readonly ISettingsProvider _config;
    private readonly Func<string, FilePresence> _probe;
    private readonly TimeSpan _budget;

    /// <summary>Initializes a new instance of the <see cref="LocalIndex"/> class.</summary>
    /// <param name="library">The library reader.</param>
    /// <param name="config">The plugin configuration (placeholder roots).</param>
    /// <param name="probe">File probe; defaults to <see cref="ProbeFile"/>.</param>
    /// <param name="budget">Time budget for all checks; defaults to <see cref="DefaultBudget"/>.</param>
    public LocalIndex(
        ILocalLibrary library,
        ISettingsProvider config,
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

    // Tracks and audiobook files also carry their album/book key: the owner having any of it keeps the friend's copy out.
    private static IReadOnlyList<string> IndexKeysFor(LocalMedia media)
    {
        var item = ToItem(media);
        var keys = MediaKey.For(item);
        return keys.Count > 0 && TitleKey.ForAudio(item) is { } title ? [.. keys, title] : keys;
    }

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

    private LocalLibrarySnapshot ReadLibrary(LibraryScope? scope)
    {
        if (scope is null)
        {
            return _library.Read();
        }

        return scope.IsEmpty ? new LocalLibrarySnapshot([], []) : _library.Read(scope);
    }

    private async Task<LocalIndexResult> BuildCoreAsync(IReadOnlySet<string>? onlyKeys, LibraryScope? scope, CancellationToken ct)
    {
        var cfg = _config.Current;
        var snapshot = ReadLibrary(scope);

        var candidates = new List<(LocalMedia Media, IReadOnlyList<string> Keys)>();
        var artistNames = new List<string?>();
        foreach (var media in snapshot.Media)
        {
            if (string.IsNullOrWhiteSpace(media.Path)
                || media.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || PlaceholderPaths.IsPlaceholder(media.Path, cfg))
            {
                continue;
            }

            artistNames.Add(media.AlbumArtist);
            artistNames.AddRange(media.Artists ?? []);

            var keys = IndexKeysFor(media);
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

        var byType = new Dictionary<MediaType, ISet<string>>();
        foreach (var type in MediaTypes.All)
        {
            byType[type] = new HashSet<string>(StringComparer.Ordinal);
        }

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

            if (MediaTypes.Of(candidates[i].Media.Type) is { } type)
            {
                byType[type].UnionWith(candidates[i].Keys);
            }
        }

        return new LocalIndexResult(byType, BuildSeries(snapshot.Series, cfg), unverified, missing)
        {
            Artists = OwnerArtists.Build(artistNames),
        };
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
        JellyShareSettings cfg)
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
            m.Name ?? string.Empty,
            null,
            Copy(m.ProviderIds) ?? new Dictionary<string, string>(),
            null,
            Copy(m.SeriesProviderIds),
            m.SeasonNumber,
            m.EpisodeNumber,
            null,
            null,
            _emptyMedia,
            [],
            default,
            Album: m.Album,
            AlbumArtist: m.AlbumArtist,
            Artists: m.Artists?.ToArray(),
            DiscNumber: m.DiscNumber,
            TrackNumber: m.TrackNumber,
            FolderPath: m.FolderPath);

    private static Dictionary<string, string>? Copy(IReadOnlyDictionary<string, string>? ids)
        => ids is null ? null : new Dictionary<string, string>(ids);
}
