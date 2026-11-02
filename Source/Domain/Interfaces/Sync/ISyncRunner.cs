using JellyShare.Domain.Models.Sync;

namespace JellyShare.Domain.Interfaces.Sync;

/// <summary>Runs syncs and exposes the last report (implemented by <c>SyncService</c>).</summary>
public interface ISyncRunner
{
    /// <summary>Gets the report of the last finished run, if any.</summary>
    SyncReport? LastReport { get; }

    /// <summary>Gets a value indicating whether a sync is running.</summary>
    bool IsRunning { get; }

    /// <summary>Gets the error of the last sync when it failed.</summary>
    string? LastError { get; }

    /// <summary>Runs one sync; concurrent calls wait for the one in progress.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The report.</returns>
    Task<SyncReport> RunAsync(CancellationToken ct);

    /// <summary>Starts a sync in the background unless one is running.</summary>
    /// <param name="queueIfRunning">Run once more after the current sync when one is running.</param>
    /// <returns><c>true</c> when started; <c>false</c> when one was already running.</returns>
    bool TryStartBackground(bool queueIfRunning = false);
}
