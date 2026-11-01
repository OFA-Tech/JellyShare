using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Storage;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Domain.Interfaces.Storage;

/// <summary>Persists the merged index of the last good peer catalogs (<c>merged-index.json</c>).</summary>
public interface IMergedIndexStore
{
    /// <summary>Loads the merged index.</summary>
    /// <returns>The index, or <c>null</c> when missing or unreadable.</returns>
    MergedIndexFile? Load();

    /// <summary>Saves the merged entries atomically.</summary>
    /// <param name="entries">The merged entries.</param>
    /// <param name="generatedUtc">The build time.</param>
    void Save(IEnumerable<MergedEntry> entries, DateTimeOffset generatedUtc);

    /// <summary>Gets when the file last changed (<c>default</c> when missing).</summary>
    /// <returns>The stamp.</returns>
    FileStamp Stamp();
}
