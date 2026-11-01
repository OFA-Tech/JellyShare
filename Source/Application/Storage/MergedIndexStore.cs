using System.Text.Json;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Application.Storage;

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
        TryLoad(path, out var file);
        return file;
    }

    /// <summary>Loads the merged index, telling a missing file apart from a read failure.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="file">The index, or <c>null</c> when missing or unreadable.</param>
    /// <returns><c>false</c> when the file exists but could not be read or parsed (possibly a transient error).</returns>
    public static bool TryLoad(string path, out MergedIndexFile? file)
    {
        file = null;
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            using var stream = File.OpenRead(path);
            file = JsonSerializer.Deserialize<MergedIndexFile>(stream);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
