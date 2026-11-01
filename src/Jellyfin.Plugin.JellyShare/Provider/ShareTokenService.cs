using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyShare.Configuration;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>Creates share tokens and verifies presented tokens against stored hashes.</summary>
public class ShareTokenService
{
    private const string Prefix = "js1_";

    /// <summary>Creates a new random token and its storage hash.</summary>
    /// <returns>The plaintext token (shown once) and its lowercase hex SHA-256 hash.</returns>
    public (string Token, string Hash) Create()
    {
        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    /// <summary>Finds the enabled share whose hash matches the presented token.</summary>
    /// <param name="presentedToken">The token from the request.</param>
    /// <param name="shares">The configured shares.</param>
    /// <returns>The matching share, or <c>null</c>.</returns>
    public PeerShare? Verify(string? presentedToken, IEnumerable<PeerShare> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            return null;
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken));
        PeerShare? match = null;
        foreach (var share in shares)
        {
            if (!share.Enabled || !TryParseHash(share.TokenHash, presented.Length, out var stored))
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(presented, stored))
            {
                match ??= share;
            }
        }

        return match;
    }

    private static bool TryParseHash(string? hex, int byteLength, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(hex) || hex.Length != byteLength * 2)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return bytes.Length == byteLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
