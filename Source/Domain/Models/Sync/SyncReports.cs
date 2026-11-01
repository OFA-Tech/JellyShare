namespace JellyShare.Domain.Models.Sync;

/// <summary>Outcome of a sync run.</summary>
/// <param name="Offered">Merged titles offered by peers.</param>
/// <param name="Unmatched">Peer items without usable provider ids (including <see cref="Conflicts"/>).</param>
/// <param name="SkippedLocal">Offered titles skipped because the owner has them locally.</param>
/// <param name="Written">Placeholder files written.</param>
/// <param name="Deleted">Placeholder files deleted.</param>
/// <param name="PeerErrors">One <c>"{peer}: {error}"</c> line per failing peer.</param>
public sealed record SyncReport(
    int Offered,
    int Unmatched,
    int SkippedLocal,
    int Written,
    int Deleted,
    IReadOnlyList<string> PeerErrors)
{
    /// <summary>Gets entries skipped because their placeholder path was unsafe.</summary>
    public int Skipped { get; init; }

    /// <summary>Gets peer items dropped because their provider ids conflict.</summary>
    public int Conflicts { get; init; }

    /// <summary>Gets the directories reported for a targeted scan in this run.</summary>
    public int Scanned { get; init; }

    /// <summary>Gets the directories left for the next run (batch limit).</summary>
    public int PendingScans { get; init; }

    /// <summary>Gets the images downloaded.</summary>
    public int ImagesWritten { get; init; }

    /// <summary>Gets local files not checked within the time budget (counted as present).</summary>
    public int UncheckedLocalFiles { get; init; }

    /// <summary>Gets local files that are missing (their titles may get placeholders).</summary>
    public int MissingLocalFiles { get; init; }

    /// <summary>Gets when the run finished.</summary>
    public DateTimeOffset FinishedUtc { get; init; }
}

/// <summary>Outcome of a targeted <c>SyncService.ReconcileAsync</c>.</summary>
/// <param name="Entries">Merged-index entries that matched the requested keys.</param>
/// <param name="Written">Placeholder files written.</param>
/// <param name="Deleted">Placeholder files deleted.</param>
/// <param name="Scanned">Directories reported for a targeted scan.</param>
/// <param name="MissingLocalFiles">Matching local files that are missing.</param>
public sealed record ReconcileReport(int Entries, int Written, int Deleted, int Scanned, int MissingLocalFiles);
