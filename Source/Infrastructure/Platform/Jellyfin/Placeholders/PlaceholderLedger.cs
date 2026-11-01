using System.Text.Json;
using JellyShare.Jellyfin.Consumer;

namespace JellyShare.Jellyfin.Placeholders;

/// <summary>
/// What the plugin last wrote into its placeholder roots: per root (full path), relative path to the content hash
/// (<see cref="PlaceholderPlanner.Hash"/>). The planner compares the desired content with this record, not with the
/// disk, so files Jellyfin rewrites (its NFO saver) are not fought over, and only files the plugin wrote are deleted.
/// Kept in the plugin data folder.
/// </summary>
public sealed class PlaceholderLedger
{
    /// <summary>The file name in the plugin data folder.</summary>
    public const string FileName = "placeholder-ledger.json";

    private readonly Dictionary<string, Dictionary<string, string>> _roots;

    private PlaceholderLedger(Dictionary<string, Dictionary<string, string>> roots)
    {
        _roots = roots;
    }

    /// <summary>Loads the ledger; a missing or unreadable file gives an empty ledger (roots are then seeded from disk).</summary>
    /// <param name="path">The ledger file.</param>
    /// <returns>The ledger.</returns>
    public static PlaceholderLedger Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var roots = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                if (roots is not null)
                {
                    return new PlaceholderLedger(roots.ToDictionary(
                        kv => kv.Key,
                        kv => new Dictionary<string, string>(kv.Value ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                        StringComparer.Ordinal));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Re-seeded from disk below: the files there are treated as plugin-written.
        }

        return new PlaceholderLedger(new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal));
    }

    /// <summary>Gets the records of a root, or <c>null</c> when the root has none yet.</summary>
    /// <param name="rootFull">The full root path.</param>
    /// <returns>Relative path to hash.</returns>
    public Dictionary<string, string>? For(string rootFull)
        => _roots.TryGetValue(rootFull, out var records) ? records : null;

    /// <summary>Replaces the records of a root.</summary>
    /// <param name="rootFull">The full root path.</param>
    /// <param name="records">Relative path to hash.</param>
    public void Set(string rootFull, Dictionary<string, string> records) => _roots[rootFull] = records;

    /// <summary>Saves the ledger atomically.</summary>
    /// <param name="path">The ledger file.</param>
    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_roots));

    /// <summary>
    /// Seeds the records of a root from disk (first run with a ledger): every <c>.strm</c>, the <c>.nfo</c> next to a
    /// <c>.strm</c> of the same name, and <c>tvshow.nfo</c> are treated as plugin-written. Other files (a Jellyfin
    /// <c>season.nfo</c>, for example) are not.
    /// </summary>
    /// <param name="root">The placeholder root.</param>
    /// <returns>Relative path to hash.</returns>
    public static Dictionary<string, string> Seed(string root)
    {
        var existing = PlaceholderWriter.ScanExisting(root);
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rel, hash) in existing)
        {
            if (rel.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith("/tvshow.nfo", StringComparison.Ordinal)
                || (rel.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) && existing.ContainsKey(rel[..^4] + ".strm")))
            {
                records[rel] = hash;
            }
        }

        return records;
    }
}
