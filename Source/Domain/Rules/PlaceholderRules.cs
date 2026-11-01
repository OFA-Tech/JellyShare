using System.Security.Cryptography;
using System.Text;

namespace JellyShare.Domain.Rules;

/// <summary>Placeholder rules shared by the planner (Application) and the writer (Data.Files). Pure.</summary>
public static class PlaceholderRules
{
    /// <summary>Hashes placeholder content: lowercase hex SHA-256 of the UTF-8 bytes.</summary>
    /// <param name="content">The file content.</param>
    /// <returns>The lowercase hex hash.</returns>
    public static string Hash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>
    /// Checks that a relative placeholder location cannot leave the placeholder root: every segment is non-empty, not
    /// <c>.</c>/<c>..</c>, has no illegal or control characters and does not start or end with a space or end with a dot.
    /// </summary>
    /// <param name="relativeDir">The relative directory (forward slashes).</param>
    /// <param name="baseName">The file name without extension.</param>
    /// <returns><c>true</c> if the location cannot escape the placeholder root.</returns>
    public static bool IsSafeRelative(string relativeDir, string baseName)
    {
        if (string.IsNullOrEmpty(relativeDir) || string.IsNullOrEmpty(baseName))
        {
            return false;
        }

        return relativeDir.Split('/').All(IsSafeSegment) && IsSafeSegment(baseName);
    }

    /// <summary>Checks whether a character may not appear in a placeholder file or folder name.</summary>
    /// <param name="c">The character.</param>
    /// <returns><c>true</c> when it is illegal.</returns>
    public static bool IsIllegal(char c)
        => c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static bool IsSafeSegment(string segment)
        => segment.Length > 0
            && segment is not ("." or "..")
            && !segment.Any(c => IsIllegal(c) || (char.IsWhiteSpace(c) && c != ' '))
            && !segment.EndsWith('.')
            && !segment.EndsWith(' ')
            && !segment.StartsWith(' ');
}
