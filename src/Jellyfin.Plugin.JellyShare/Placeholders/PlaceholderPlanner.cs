using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyShare.Keys;

namespace Jellyfin.Plugin.JellyShare.Placeholders;

/// <summary>A placeholder file to create or overwrite.</summary>
/// <param name="RelativePath">Path relative to the placeholder root, with forward slashes.</param>
/// <param name="Content">The UTF-8 text content.</param>
public sealed record FileWrite(string RelativePath, string Content);

/// <summary>The changes needed to bring a placeholder root in line with the wanted entries.</summary>
/// <param name="Writes">Files to write, ordered by path.</param>
/// <param name="Deletes">Planner-owned files (<c>.strm</c>/<c>.nfo</c>) to delete, ordered by path.</param>
/// <param name="TouchedDirs">Distinct relative directories that have a write or delete, ordered.</param>
/// <param name="Skipped">Entries skipped because their placeholder location was not a safe relative path.</param>
public sealed record PlaceholderDiff(
    IReadOnlyList<FileWrite> Writes,
    IReadOnlyList<string> Deletes,
    IReadOnlyList<string> TouchedDirs,
    int Skipped = 0);

/// <summary>Computes placeholder file changes from the merged catalog. Pure: touches no file system.</summary>
public static class PlaceholderPlanner
{
    /// <summary>Hashes content the way <c>existing</c> must be hashed: lowercase hex SHA-256 of the UTF-8 bytes.</summary>
    /// <param name="content">The file content.</param>
    /// <returns>The lowercase hex hash.</returns>
    public static string Hash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>Plans the writes and deletes.</summary>
    /// <param name="wanted">The merged entries offered by peers.</param>
    /// <param name="localKeys">Keys of titles the owner already has; those entries get no placeholder.</param>
    /// <param name="existing">Relative path (forward slashes) to content hash of files currently on disk.</param>
    /// <param name="relayBase">The loopback relay base URL.</param>
    /// <param name="localSeries">Series the owner already has, keyed <c>tvdb:{id}</c>/<c>imdb:{id}</c>/<c>tmdb:{id}</c> (lowercase ids).</param>
    /// <returns>The diff. Images, the <c>.jellyshare</c> marker and empty directories are the writer's concern.</returns>
    public static PlaceholderDiff Plan(
        IEnumerable<MergedEntry> wanted,
        ISet<string> localKeys,
        IReadOnlyDictionary<string, string> existing,
        string relayBase,
        IReadOnlyDictionary<string, LocalSeries>? localSeries = null)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(localKeys);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(relayBase);

        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        var seriesDirs = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        var ordered = wanted
            .Where(e => e.Sources.Count > 0 && !e.Keys.Any(localKeys.Contains))
            .OrderBy(e => e.CanonicalKey, StringComparer.Ordinal);

        foreach (var entry in ordered)
        {
            var item = entry.Sources[0].Item;
            var isEpisode = string.Equals(item.Type, "Episode", StringComparison.Ordinal);
            var seriesIds = isEpisode ? PathNaming.SeriesIds(entry) : null;
            var local = seriesIds is null ? null : LocalSeries.Find(localSeries, seriesIds);
            var file = PathNaming.ForEntry(entry, local);
            if (!PathNaming.IsSafeRelative(file.RelativeDir, file.BaseName))
            {
                skipped++;
                continue;
            }

            var strmPath = file.RelativeDir + "/" + file.BaseName + ".strm";
            if (desired.ContainsKey(strmPath))
            {
                continue;
            }

            desired[strmPath] = PlaceholderPaths.StrmContent(relayBase, entry.CanonicalKey);
            desired[file.RelativeDir + "/" + file.BaseName + ".nfo"] = isEpisode
                ? NfoWriter.Episode(item, local?.Name)
                : NfoWriter.Movie(item);

            if (isEpisode)
            {
                var seriesDir = file.RelativeDir[..file.RelativeDir.IndexOf('/', StringComparison.Ordinal)];
                if (seriesDirs.Add(seriesDir))
                {
                    desired[seriesDir + "/tvshow.nfo"] = NfoWriter.Series(item, local, seriesIds);
                }
            }
        }

        var writes = new List<FileWrite>();
        foreach (var (path, content) in desired)
        {
            if (!existing.TryGetValue(path, out var hash)
                || !string.Equals(hash, Hash(content), StringComparison.OrdinalIgnoreCase))
            {
                writes.Add(new FileWrite(path, content));
            }
        }

        writes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        var deletes = existing.Keys
            .Where(p => IsOwned(p) && !desired.ContainsKey(p))
            .Order(StringComparer.Ordinal)
            .ToList();

        var touched = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in writes.Select(w => w.RelativePath).Concat(deletes))
        {
            var slash = path.LastIndexOf('/');
            touched.Add(slash < 0 ? string.Empty : path[..slash]);
        }

        return new PlaceholderDiff(writes, deletes, touched.ToList(), skipped);
    }

    private static bool IsOwned(string path)
        => path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase);
}
