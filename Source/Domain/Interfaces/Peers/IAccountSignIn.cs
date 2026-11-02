using JellyShare.Domain.Models.Peers;

namespace JellyShare.Domain.Interfaces.Peers;

/// <summary>Signs in to and out of a friend's Jellyfin server with a user account.</summary>
public interface IAccountSignIn
{
    /// <summary>Signs in with a user name and password. The password is only sent in the request body and never kept.</summary>
    /// <param name="baseUrl">The friend's server base URL.</param>
    /// <param name="username">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="peerId">The peer id, used for the device id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<AccountSignInResult> SignInAsync(string baseUrl, string username, string password, Guid peerId, CancellationToken ct);

    /// <summary>Ends the session of an access token. Best effort: failures are ignored.</summary>
    /// <param name="baseUrl">The friend's server base URL.</param>
    /// <param name="accessToken">The access token to end.</param>
    /// <param name="peerId">The peer id, used for the device id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the request finished.</returns>
    Task SignOutAsync(string baseUrl, string accessToken, Guid peerId, CancellationToken ct);
}
