using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Keys;

/// <summary>One title merged across peers.</summary>
/// <param name="CanonicalKey">The deterministic primary key of the entry.</param>
/// <param name="Keys">All keys of the entry, in ordinal order.</param>
/// <param name="Sources">The peer items that make up the entry, in input order.</param>
public sealed record MergedEntry(
    string CanonicalKey,
    IReadOnlyList<string> Keys,
    IReadOnlyList<(Guid PeerId, CatalogItem Item)> Sources);

/// <summary>
/// Merges items from several peers into one entry per title using union-find over their keys.
/// </summary>
public sealed class MergeIndex
{
    private static readonly string[] _kindPreference = ["tmdb", "imdb", "tvdb"];

    private readonly Dictionary<string, MergedEntry> _byKey;

    private MergeIndex(
        IReadOnlyList<MergedEntry> entries,
        Dictionary<string, MergedEntry> byKey,
        int unmatchedCount,
        int conflictCount)
    {
        Entries = entries;
        _byKey = byKey;
        UnmatchedCount = unmatchedCount;
        ConflictCount = conflictCount;
    }

    /// <summary>Gets the merged entries, ordered by canonical key (ordinal).</summary>
    public IReadOnlyList<MergedEntry> Entries { get; }

    /// <summary>
    /// Gets the number of items that belong to no entry: items without usable provider ids,
    /// plus items dropped because their ids conflict (see <see cref="ConflictCount"/>).
    /// </summary>
    public int UnmatchedCount { get; }

    /// <summary>
    /// Gets the number of items dropped because their connected keys contain more than one id of the
    /// same kind (for example two different tmdb ids bridged by a shared imdb id). Included in <see cref="UnmatchedCount"/>.
    /// </summary>
    public int ConflictCount { get; }

    /// <summary>Builds the index.</summary>
    /// <param name="items">The peer items.</param>
    /// <returns>The merged index.</returns>
    public static MergeIndex Build(IEnumerable<(Guid PeerId, CatalogItem Item)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var matched = new List<((Guid PeerId, CatalogItem Item) Source, IReadOnlyList<string> Keys)>();
        var unmatched = 0;
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in items)
        {
            var keys = MediaKey.For(source.Item);
            if (keys.Count == 0)
            {
                unmatched++;
                continue;
            }

            matched.Add((source, keys));
            foreach (var key in keys)
            {
                parent.TryAdd(key, key);
            }

            for (var i = 1; i < keys.Count; i++)
            {
                Union(parent, keys[0], keys[i]);
            }
        }

        var groups = new Dictionary<string, (List<string> Keys, List<(Guid PeerId, CatalogItem Item)> Sources)>(
            StringComparer.Ordinal);
        foreach (var key in parent.Keys)
        {
            var root = FindRoot(parent, key);
            if (!groups.TryGetValue(root, out var group))
            {
                group = ([], []);
                groups[root] = group;
            }

            group.Keys.Add(key);
        }

        foreach (var (source, keys) in matched)
        {
            groups[FindRoot(parent, keys[0])].Sources.Add(source);
        }

        var entries = new List<MergedEntry>(groups.Count);
        var conflicts = 0;
        foreach (var (groupKeys, sources) in groups.Values)
        {
            groupKeys.Sort(StringComparer.Ordinal);
            if (HasConflictingIds(groupKeys))
            {
                conflicts += sources.Count;
                continue;
            }

            entries.Add(new MergedEntry(PickCanonical(groupKeys), groupKeys, sources));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.CanonicalKey, b.CanonicalKey));

        var byKey = new Dictionary<string, MergedEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var key in entry.Keys)
            {
                byKey[key] = entry;
            }
        }

        return new MergeIndex(entries, byKey, unmatched + conflicts, conflicts);
    }

    private static bool HasConflictingIds(List<string> keys)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        return keys.Exists(key => !kinds.Add(MediaKey.KindOf(key)));
    }

    /// <summary>Finds the entry containing a key.</summary>
    /// <param name="key">Any key of the entry.</param>
    /// <returns>The entry, or <c>null</c> if no entry has that key.</returns>
    public MergedEntry? Find(string key)
        => key is not null && _byKey.TryGetValue(key, out var entry) ? entry : null;

    private static string PickCanonical(List<string> sortedKeys)
    {
        foreach (var kind in _kindPreference)
        {
            if (sortedKeys.Find(key => string.Equals(MediaKey.KindOf(key), kind, StringComparison.Ordinal)) is { } match)
            {
                return match;
            }
        }

        return sortedKeys[0];
    }

    private static string FindRoot(Dictionary<string, string> parent, string key)
    {
        var root = key;
        while (!string.Equals(parent[root], root, StringComparison.Ordinal))
        {
            root = parent[root];
        }

        while (!string.Equals(parent[key], root, StringComparison.Ordinal))
        {
            var next = parent[key];
            parent[key] = root;
            key = next;
        }

        return root;
    }

    private static void Union(Dictionary<string, string> parent, string a, string b)
    {
        var rootA = FindRoot(parent, a);
        var rootB = FindRoot(parent, b);
        if (!string.Equals(rootA, rootB, StringComparison.Ordinal))
        {
            parent[rootB] = rootA;
        }
    }
}
