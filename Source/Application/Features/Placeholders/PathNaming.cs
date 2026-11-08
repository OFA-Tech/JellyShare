using System.Globalization;
using System.Text;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Library;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Rules;

namespace JellyShare.Application.Features.Placeholders;

/// <summary>Where a placeholder lives relative to its placeholder root.</summary>
/// <param name="RelativeDir">The directory holding the <c>.strm</c>/<c>.nfo</c> pair (forward slashes).</param>
/// <param name="BaseName">The file name without extension.</param>
/// <param name="Kind"><c>Movie</c> or <c>Episode</c>.</param>
public sealed record PlaceholderFile(string RelativeDir, string BaseName, string Kind);

/// <summary>File system safe names for placeholders.</summary>
public static class PathNaming
{
    private const int MaxLength = 120;

    private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Makes a title safe to use as a single path segment on every platform.</summary>
    /// <param name="name">The raw title.</param>
    /// <returns>A deterministic, non-empty, path-safe name of at most 120 chars.</returns>
    public static string Safe(string name)
    {
        var sb = new StringBuilder(name?.Length ?? 0);
        var lastSpace = false;
        foreach (var c in name ?? string.Empty)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
                continue;
            }

            lastSpace = false;
            sb.Append(IsIllegal(c) ? '-' : c);
        }

        var result = Trim(sb.ToString());
        if (result.Length > MaxLength)
        {
            var cut = MaxLength;
            if (char.IsHighSurrogate(result[cut - 1]))
            {
                cut--;
            }

            result = Trim(result[..cut]);
        }

        if (result.Length == 0)
        {
            return "Untitled";
        }

        var dot = result.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? result : result[..dot];
        return _reserved.Contains(stem.TrimEnd()) ? result + "_" : result;
    }

    /// <summary>Computes the placeholder location of a merged entry.</summary>
    /// <param name="e">The merged entry; the first source provides the names.</param>
    /// <param name="localSeries">The owner's local series, if the owner already has this series.</param>
    /// <param name="ownerArtists">The owner's artist names (<see cref="OwnerArtists.Build"/>), used instead of the friend's spelling.</param>
    /// <returns>The placeholder location.</returns>
    public static PlaceholderFile ForEntry(MergedEntry e, LocalSeries? localSeries = null, IReadOnlyDictionary<string, string>? ownerArtists = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        var item = e.Sources[0].Item;
        if (string.Equals(item.Type, "Episode", StringComparison.Ordinal))
        {
            return ForEpisode(e, item, localSeries);
        }

        if (MediaTypes.IsAudio(item.Type))
        {
            return ForAudio(item, ownerArtists);
        }

        var name = Safe(item.Name);
        var baseName = item.Year is { } year ? string.Create(CultureInfo.InvariantCulture, $"{name} ({year})") : name;
        var ids = ItemIds(e, item);
        var tag = FirstTag(("tmdb", ids.GetValueOrDefault("tmdb")), ("imdb", ids.GetValueOrDefault("imdb")));
        return new PlaceholderFile(tag.Length == 0 ? baseName : baseName + " " + tag, baseName, "Movie");
    }

    /// <summary>
    /// Gets the validated series provider ids (tvdb, imdb, tmdb; lowercased) of an episode entry, merged across all
    /// its sources (earlier sources win) and its keys. Folder tag, local-series lookup and tvshow.nfo all use this set.
    /// </summary>
    /// <param name="e">The merged entry.</param>
    /// <returns>Kind to id.</returns>
    internal static Dictionary<string, string> SeriesIds(MergedEntry e)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, item) in e.Sources)
        {
            if (item.SeriesProviderIds is null)
            {
                continue;
            }

            foreach (var kind in LocalSeries.SeriesKinds)
            {
                if (LocalSeries.Normalize(item.SeriesProviderIds, kind) is { } id)
                {
                    ids.TryAdd(kind, id);
                }
            }
        }

        AddFromKeys(e, ids, "ep");
        return ids;
    }

    /// <summary>
    /// Checks that a placeholder location is a plain relative path: no rooted path, no empty, <c>.</c> or <c>..</c>
    /// segment and no characters that are illegal in file names.
    /// </summary>
    /// <param name="relativeDir">The relative directory (forward slashes).</param>
    /// <param name="baseName">The file name without extension.</param>
    /// <returns><c>true</c> if the location cannot escape the placeholder root.</returns>
    public static bool IsSafeRelative(string relativeDir, string baseName) => PlaceholderRules.IsSafeRelative(relativeDir, baseName);

    private static PlaceholderFile ForEpisode(MergedEntry e, CatalogItem item, LocalSeries? local)
    {
        var ids = SeriesIds(e);
        var seriesName = Safe(local?.Name ?? item.SeriesName ?? "Unknown Series");
        var tag = SeriesTag(ids, local);
        var season = (item.SeasonNumber ?? 0).ToString("00", CultureInfo.InvariantCulture);
        var episode = (item.EpisodeNumber ?? 0).ToString("00", CultureInfo.InvariantCulture);
        var seriesDir = tag.Length == 0 ? seriesName : seriesName + " " + tag;
        return new PlaceholderFile($"{seriesDir}/Season {season}", $"{seriesName} S{season}E{episode}", "Episode");
    }

    // No .nfo for audio (Jellyfin has no track nfo reader): the plugin writes the item's metadata after the scan.
    // Jellyfin makes one album per folder, so every track of an album shares its folder.
    private static PlaceholderFile ForAudio(CatalogItem item, IReadOnlyDictionary<string, string>? ownerArtists)
    {
        var disc = item.DiscNumber is > 1 ? string.Create(CultureInfo.InvariantCulture, $"{item.DiscNumber}-") : string.Empty;
        var baseName = item.TrackNumber is { } track
            ? string.Create(CultureInfo.InvariantCulture, $"{disc}{track:00} {Safe(item.Name)}")
            : Safe(item.Name);
        if (string.Equals(item.Type, "Audio", StringComparison.Ordinal))
        {
            var albumArtist = TitleKey.AlbumArtistOf(item);
            var artist = Safe(albumArtist is null ? "Unknown Artist" : OwnerArtists.Resolve(ownerArtists, albumArtist));
            var album = Safe(string.IsNullOrWhiteSpace(item.Album) ? "Unknown Album" : item.Album);
            return new PlaceholderFile(artist + "/" + album, baseName, "Audio");
        }

        var folders = (item.FolderPath ?? string.Empty)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Safe)
            .ToList();
        return new PlaceholderFile(folders.Count == 0 ? Safe(item.Name) : string.Join('/', folders), baseName, "AudioBook");
    }

    private static string SeriesTag(Dictionary<string, string> ids, LocalSeries? local)
    {
        if (local is not null)
        {
            return FirstTag(("tvdb", local.IdOf("tvdb")), ("imdb", local.IdOf("imdb")));
        }

        return FirstTag(("tvdb", ids.GetValueOrDefault("tvdb")), ("tmdb", ids.GetValueOrDefault("tmdb")));
    }

    private static string FirstTag(params (string Kind, string? Id)[] candidates)
        => candidates.FirstOrDefault(c => c.Id is not null) is { Id: { } id } found ? "[" + found.Kind + "id-" + id + "]" : string.Empty;

    private static Dictionary<string, string> ItemIds(MergedEntry e, CatalogItem item)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "tmdb", "imdb" })
        {
            if (LocalSeries.Normalize(item.ProviderIds, kind) is { } id)
            {
                ids[kind] = id;
            }
        }

        AddFromKeys(e, ids, "movie");
        return ids;
    }

    private static void AddFromKeys(MergedEntry e, Dictionary<string, string> ids, string prefix)
    {
        var expectedParts = string.Equals(prefix, "ep", StringComparison.Ordinal) ? 4 : 3;
        foreach (var key in e.Keys)
        {
            var parts = key.Split(':');
            if (parts.Length == expectedParts
                && string.Equals(parts[0], prefix, StringComparison.Ordinal)
                && Array.IndexOf(LocalSeries.SeriesKinds, parts[1]) >= 0
                && MediaKey.NormalizeId(parts[2]) is { } id
                && string.Equals(id, parts[2], StringComparison.Ordinal))
            {
                ids.TryAdd(parts[1], id);
            }
        }
    }

    private static bool IsIllegal(char c) => PlaceholderRules.IsIllegal(c);

    private static string Trim(string s) => s.TrimStart(' ').TrimEnd('.', ' ', '-');
}
