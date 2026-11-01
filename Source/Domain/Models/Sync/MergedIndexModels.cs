using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Models.Sync;

/// <summary>One peer item of a merged entry.</summary>
public sealed class MergedIndexSource
{
    /// <summary>Gets or sets the peer id.</summary>
    public Guid PeerId { get; set; }

    /// <summary>Gets or sets the peer's catalog item (id, bitrate, naming input).</summary>
    public CatalogItem Item { get; set; } = null!;
}

/// <summary>One merged title.</summary>
public sealed class MergedIndexEntry
{
    /// <summary>Gets or sets the canonical key (the key in the <c>.strm</c> URL).</summary>
    public string CanonicalKey { get; set; } = string.Empty;

    /// <summary>Gets or sets all keys of the entry.</summary>
    public List<string> Keys { get; set; } = new();

    /// <summary>Gets or sets the peer items, preferred peer first.</summary>
    public List<MergedIndexSource> Sources { get; set; } = new();
}

/// <summary>The persisted merged index (<c>merged-index.json</c>), read by the relay and the watcher.</summary>
public sealed class MergedIndexFile
{
    /// <summary>Gets or sets when the index was built.</summary>
    public DateTimeOffset GeneratedUtc { get; set; }

    /// <summary>Gets or sets the entries, ordered by canonical key.</summary>
    public List<MergedIndexEntry> Entries { get; set; } = new();

    /// <summary>Converts back to <see cref="MergedEntry"/> values.</summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<MergedEntry> ToEntries()
        => Entries
            .Where(e => e.Sources.Count > 0)
            .Select(e => new MergedEntry(
                e.CanonicalKey,
                e.Keys.ToList(),
                e.Sources.Where(s => s.Item is not null).Select(s => (s.PeerId, s.Item)).ToList()))
            .ToList();
}
