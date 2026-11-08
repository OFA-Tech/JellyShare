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

    /// <summary>No user name was given for an account peer (400).</summary>
    public static readonly Error MissingUsername = Error.Validation("Peer.MissingUsername", "Enter the user name of the account on the other server.");

    /// <summary>No password was given for an account peer (400).</summary>
    public static readonly Error MissingPassword = Error.Validation("Peer.MissingPassword", "Enter the password of the account on the other server.");

    /// <summary>The playback limit is out of range (400).</summary>
    public static readonly Error InvalidMaxStreams = Error.Validation(
        "Peer.InvalidMaxStreams",
        $"The stream limit must be between {Admin.AdminRules.MinAccountStreams} and {Admin.AdminRules.MaxAccountStreams}.");

    /// <summary>Creates the error for an invalid accept list (400).</summary>
    /// <param name="problem">What is wrong with the media types.</param>
    /// <returns>The error.</returns>
    public static Error InvalidTypes(string problem) => Error.Validation("Peer.InvalidTypes", problem);

    /// <summary>The peer kind cannot be changed (400).</summary>
    public static readonly Error KindChange = Error.Validation("Peer.KindChange", "The kind of a peer cannot be changed. Remove it and add it again.");

    /// <summary>The address of an account peer was changed without the password (400).</summary>
    public static readonly Error AddressChanged = Error.Validation("Peer.AddressChanged", "The address was changed. Enter the password again.");

    /// <summary>Creates the error for a failed sign-in (400).</summary>
    /// <param name="message">The sign-in error; it never contains the password.</param>
    /// <returns>The error.</returns>
    public static Error SignInFailed(string message) => Error.Validation("Peer.SignInFailed", message);
}
