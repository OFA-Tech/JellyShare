using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JellyShare.Domain.Keys;

/// <summary>Normalizes names and hashes them into the name-based keys of music and audiobooks.</summary>
public static class KeyText
{
    /// <summary>
    /// Lowercases, decomposes (NFKD), drops diacritics, turns everything but letters and digits into a space and
    /// collapses spaces, so "AC/DC" and "ac dc" (or "Beyoncé" and "beyonce") compare equal.
    /// </summary>
    /// <param name="text">The raw text.</param>
    /// <returns>The normalized text, possibly empty.</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                pendingSpace = false;
                sb.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>Hashes normalized fields: the first 16 lowercase hex digits of SHA-256 over the <c>|</c>-joined fields.</summary>
    /// <param name="fields">The raw fields; each is normalized here.</param>
    /// <returns>The 16-character hash.</returns>
    public static string Hash(params string?[] fields)
    {
        var joined = string.Join('|', fields.Select(Normalize));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }
}
