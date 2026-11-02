namespace JellyShare.Domain.Models.Peers;

/// <summary>Result of signing in to a friend's Jellyfin server with a user account.</summary>
/// <param name="Ok">The sign-in succeeded.</param>
/// <param name="AccessToken">The Jellyfin access token when <paramref name="Ok"/>.</param>
/// <param name="UserId">The user id on the friend's server when <paramref name="Ok"/>.</param>
/// <param name="ServerName">The friend's server name, if it answered.</param>
/// <param name="Error">A short error that never contains the password or token.</param>
public sealed record AccountSignInResult(bool Ok, string? AccessToken, string? UserId, string? ServerName, string? Error);
