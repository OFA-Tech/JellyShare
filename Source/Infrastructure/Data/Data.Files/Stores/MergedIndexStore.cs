using System.Text.Json;
using JellyShare.Domain.Interfaces.Storage;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Storage;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Data.Files.Stores;

/// <summary>Reads and writes <see cref="MergedIndexFile"/>.</summary>
public sealed class MergedIndexStore : IMergedIndexStore
{
    /// <summary>The file name inside the plugin data folder.</summary>
    public const string FileName = "merged-index.json";

    private readonly Func<string> _dataDir;

    /// <summary>Initializes a new instance of the <see cref="MergedIndexStore"/> class.</summary>
    /// <param name="dataDir">Returns the plugin data folder.</param>
    public MergedIndexStore(Func<string> dataDir)
    {
        _dataDir = dataDir;
    }

    private string FilePath => Path.Combine(_dataDir(), FileName);

    /// <inheritdoc />
    public MergedIndexFile? Load() => Load(FilePath);

    /// <inheritdoc />
    public void Save(IEnumerable<MergedEntry> entries, DateTimeOffset generatedUtc) => Save(FilePath, entries, generatedUtc);

    /// <inheritdoc />
    public FileStamp Stamp() => FileStamps.Of(FilePath);

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
