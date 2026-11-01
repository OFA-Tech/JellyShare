using System.Text.Json;
using Jellyfin.Plugin.JellyShare.Keys;
using Jellyfin.Plugin.JellyShare.Protocol;

namespace Jellyfin.Plugin.JellyShare.Consumer;

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

/// <summary>Reads and writes <see cref="MergedIndexFile"/>.</summary>
public static class MergedIndexStore
{
    /// <summary>The file name inside the plugin data folder.</summary>
    public const string FileName = "merged-index.json";

    /// <summary>Saves the merged entries atomically.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="entries">The merged entries.</param>
    /// <param name="generatedUtc">The build time.</param>
    public static void Save(string path, IEnumerable<MergedEntry> entries, DateTimeOffset generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var file = new MergedIndexFile
        {
            GeneratedUtc = generatedUtc,
            Entries = entries.Select(e => new MergedIndexEntry
            {
                CanonicalKey = e.CanonicalKey,
                Keys = e.Keys.ToList(),
                Sources = e.Sources.Select(s => new MergedIndexSource { PeerId = s.PeerId, Item = s.Item }).ToList(),
            }).ToList(),
        };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file));
    }

    /// <summary>Loads the merged index.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The index, or <c>null</c> when missing or unreadable.</returns>
    public static MergedIndexFile? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<MergedIndexFile>(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
