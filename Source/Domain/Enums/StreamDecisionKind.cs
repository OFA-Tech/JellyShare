namespace JellyShare.Domain.Enums;

/// <summary>What to do with a new playback request.</summary>
public enum StreamDecisionKind
{
    /// <summary>Some source has room; the relay serves it.</summary>
    Play,

    /// <summary>Every source is full and none may stop a playback.</summary>
    Refuse,

    /// <summary>Every source is full; stop the oldest playback of the peer to make room.</summary>
    StopOldest,
}
