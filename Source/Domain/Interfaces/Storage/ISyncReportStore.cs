using JellyShare.Domain.Models.Sync;

namespace JellyShare.Domain.Interfaces.Storage;

/// <summary>Persists the last sync report (<c>last-sync-report.json</c>) so the admin page survives restarts.</summary>
public interface ISyncReportStore
{
    /// <summary>Loads the persisted report.</summary>
    /// <returns>The report, or <c>null</c> when missing or unreadable.</returns>
    SyncReport? Load();

    /// <summary>Saves the report atomically.</summary>
    /// <param name="report">The report.</param>
    void Save(SyncReport report);
}
