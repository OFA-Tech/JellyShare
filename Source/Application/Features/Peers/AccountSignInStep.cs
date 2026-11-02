using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Application.Features.Peers;

/// <summary>The sign-in shared by add, update and test of an account peer.</summary>
internal static class AccountSignInStep
{
    /// <summary>Signs in. A failure is a readable message that never contains the password.</summary>
    /// <param name="accounts">The sign-in service.</param>
    /// <param name="baseUrl">The normalized server URL.</param>
    /// <param name="username">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="peerId">The peer id (the device id derives from it).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The session, or the error message.</returns>
    public static async Task<(AccountSignInResult? Session, string? Error)> RunAsync(
        IAccountSignIn accounts,
        string baseUrl,
        string username,
        string password,
        Guid peerId,
        CancellationToken ct)
    {
        var result = await accounts.SignInAsync(baseUrl, username, password, peerId, ct).ConfigureAwait(false);
        if (result.Ok && !string.IsNullOrEmpty(result.AccessToken) && !string.IsNullOrEmpty(result.UserId))
        {
            return (result, null);
        }

        var message = !result.Ok && !string.IsNullOrWhiteSpace(result.Error)
            ? result.Error
            : AccountPeerInfo.SignInFailedPrefix + ": invalid response";
        return (null, message.Replace(password, "***", StringComparison.Ordinal));
    }
}
