namespace JellyShare.Domain.Interfaces.Library;

/// <summary>Asks Jellyfin to rescan a changed path (targeted, never a full library refresh).</summary>
public interface IScanReporter
{
    /// <summary>Reports a changed directory.</summary>
    /// <param name="fullPath">The full directory path (may no longer exist).</param>
    void ReportChanged(string fullPath);
}
