namespace JellyShare.Domain.Enums;

/// <summary>Why a ping failed.</summary>
public enum PingFailure
{
    /// <summary>The ping succeeded.</summary>
    None,

    /// <summary>The URL is not an absolute http(s) URL.</summary>
    InvalidUrl,

    /// <summary>The server could not be reached.</summary>
    Unreachable,

    /// <summary>The server did not answer in time.</summary>
    Timeout,

    /// <summary>The server answered with a redirect (never followed).</summary>
    Redirect,

    /// <summary>The server answered, but not like a JellyShare server.</summary>
    NotJellyShare,

    /// <summary>The server speaks another major protocol version.</summary>
    IncompatibleProtocol,
}
