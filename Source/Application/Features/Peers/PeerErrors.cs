using JellyShare.Domain.Results;

namespace JellyShare.Application.Features.Peers;

/// <summary>Errors of the peer use cases.</summary>
public static class PeerErrors
{
    /// <summary>The peer does not exist (404).</summary>
    public static readonly Error NotFound = Error.NotFound("Peer.NotFound", "Peer not found.");

    /// <summary>The name is missing, too long or has control characters (400).</summary>
    public static readonly Error InvalidName = Error.Validation("Peer.InvalidName", "Enter a name for the peer.");

    /// <summary>The URL cannot be used (400).</summary>
    public static readonly Error InvalidUrl = Error.Validation("Peer.InvalidUrl", Admin.AdminRules.InvalidUrlMessage);

    /// <summary>No token was given (400).</summary>
    public static readonly Error MissingToken = Error.Validation("Peer.MissingToken", Admin.AdminRules.MissingTokenMessage);

    /// <summary>Creates the error for an unusable token (400).</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The error.</returns>
    public static Error InvalidToken(string problem) => Error.Validation("Peer.InvalidToken", problem);
}
