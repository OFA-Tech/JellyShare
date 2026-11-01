namespace JellyShare.Domain.Models.Placeholders;

/// <summary>
/// What the plugin last wrote into its placeholder roots: per root (full path), relative path to the content hash
/// (<c>PlaceholderRules.Hash</c>). The planner compares the desired content with this record, not with the disk, so
/// files Jellyfin rewrites (its NFO saver) are not fought over, and only files the plugin wrote are deleted.
/// Persisted in the plugin data folder by <c>IPlaceholderLedgerStore</c>.
/// </summary>
public sealed class PlaceholderLedger
{
    private readonly Dictionary<string, Dictionary<string, string>> _roots;

    /// <summary>Initializes a new instance of the <see cref="PlaceholderLedger"/> class.</summary>
    /// <param name="roots">Full root path to (relative path to hash); copied with ordinal comparers.</param>
    public PlaceholderLedger(IReadOnlyDictionary<string, Dictionary<string, string>>? roots = null)
    {
        _roots = (roots ?? new Dictionary<string, Dictionary<string, string>>()).ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, string>(kv.Value ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>Gets the records of every root (what is persisted).</summary>
    public IReadOnlyDictionary<string, Dictionary<string, string>> Roots => _roots;

    /// <summary>Gets the records of a root, or <c>null</c> when the root has none yet.</summary>
    /// <param name="rootFull">The full root path.</param>
    /// <returns>Relative path to hash.</returns>
    public Dictionary<string, string>? For(string rootFull)
        => _roots.TryGetValue(rootFull, out var records) ? records : null;

    /// <summary>Replaces the records of a root.</summary>
    /// <param name="rootFull">The full root path.</param>
    /// <param name="records">Relative path to hash.</param>
    public void Set(string rootFull, Dictionary<string, string> records) => _roots[rootFull] = records;

    /// <summary>
    /// Seeds the records of a root from what is on disk (first run with a ledger): every <c>.strm</c>, the <c>.nfo</c>
    /// next to a <c>.strm</c> of the same name, and <c>tvshow.nfo</c> are treated as plugin-written. Other files (a
    /// Jellyfin <c>season.nfo</c>, for example) are not.
    /// </summary>
    /// <param name="existing">The placeholder files under the root (relative path to hash).</param>
    /// <returns>Relative path to hash.</returns>
    public static Dictionary<string, string> Seed(IReadOnlyDictionary<string, string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
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
