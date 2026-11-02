namespace JellyShare.Domain.Enums;

/// <summary>What happens when a new stream would exceed an account peer's stream limit.</summary>
public enum StreamLimitAction
{
    /// <summary>The new stream is refused.</summary>
    Refuse = 0,

    /// <summary>The oldest running stream is stopped to make room.</summary>
    StopOldest = 1,
}
