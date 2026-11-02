namespace JellyShare.Domain.Interfaces.Storage;

/// <summary>Persists the folders still to be reported for scanning (<c>pending-scans.json</c>).</summary>
public interface IPendingScanStore
{
    /// <summary>Checks whether the list exists.</summary>
    /// <returns><c>true</c> when the file exists.</returns>
    bool Exists();

    /// <summary>Reads the list; an unreadable list is logged and read as empty.</summary>
    /// <returns>The folders, in order (may contain duplicates).</returns>
    List<string> Read();

    /// <summary>Replaces the list atomically.</summary>
    /// <param name="dirs">The folders.</param>
    void Write(IReadOnlyList<string> dirs);
}
