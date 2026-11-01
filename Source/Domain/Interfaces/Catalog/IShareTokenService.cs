using JellyShare.Domain.Models.Configuration;

namespace JellyShare.Domain.Interfaces.Catalog;

/// <summary>Creates share tokens and verifies presented tokens against stored hashes.</summary>
public interface IShareTokenService
{
    /// <summary>Creates a new random token and its storage hash.</summary>
    /// <returns>The plaintext token (shown once) and its lowercase hex SHA-256 hash.</returns>
    (string Token, string Hash) Create();

    /// <summary>Finds the enabled share whose hash matches the presented token.</summary>
    /// <param name="presentedToken">The token from the request.</param>
    /// <param name="shares">The configured shares.</param>
    /// <returns>The matching share, or <c>null</c>.</returns>
    PeerShare? Verify(string? presentedToken, IEnumerable<PeerShare> shares);
}
