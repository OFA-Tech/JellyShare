namespace JellyShare.Domain.Enums;

/// <summary>How this server authenticates to a peer.</summary>
public enum PeerKind
{
    /// <summary>With a JellyShare invitation token.</summary>
    Token = 0,

    /// <summary>With a user account on the peer's Jellyfin server.</summary>
    Account = 1,
}
