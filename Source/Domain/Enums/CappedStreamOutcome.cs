namespace JellyShare.Domain.Enums;

/// <summary>How a capped (transcoded) stream ended.</summary>
public enum CappedStreamOutcome
{
    /// <summary>ffmpeg ran to the end, or failed after it had sent output (the response is already under way).</summary>
    Completed,

    /// <summary>ffmpeg could not start or failed before any output: nothing was served (503, the consumer fails over).</summary>
    FailedWithoutOutput,

    /// <summary>A newer request for the same stream session took over: the response must be cut.</summary>
    Superseded,

    /// <summary>The client disconnected (or a write to it failed).</summary>
    ClientDisconnected,
}
