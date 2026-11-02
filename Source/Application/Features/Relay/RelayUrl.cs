namespace JellyShare.Application.Features.Relay;

/// <summary>Recognises relay URLs (the content of placeholder <c>.strm</c> files).</summary>
public static class RelayUrl
{
    /// <summary>The path segment of the relay endpoint, before the escaped key.</summary>
    public const string PlaySegment = "/JellyShare/v1/play/";

    /// <summary>
    /// Checks whether a media source path is a relay URL: it starts with the current relay base, or it is an
    /// http(s) URL whose path contains <see cref="PlaySegment"/> (a placeholder written under an older relay base).
    /// </summary>
    /// <param name="path">The media source path.</param>
    /// <param name="relayBase">The current relay base URL.</param>
    /// <returns><c>true</c> for relay URLs.</returns>
    public static bool IsRelayUrl(string? path, string? relayBase)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (!string.IsNullOrWhiteSpace(relayBase)
            && trimmed.StartsWith(relayBase.Trim().TrimEnd('/') + PlaySegment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsolutePath.Contains(PlaySegment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the canonical key from a relay URL.</summary>
    /// <param name="path">The media source path or <c>.strm</c> content.</param>
    /// <param name="relayBase">The current relay base URL.</param>
    /// <param name="key">The unescaped canonical key.</param>
    /// <returns><c>true</c> when <paramref name="path"/> is a relay URL.</returns>
    public static bool TryGetKey(string? path, string? relayBase, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        string? escaped = null;
        if (!string.IsNullOrWhiteSpace(relayBase))
        {
            var prefix = relayBase.Trim().TrimEnd('/') + PlaySegment;
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                escaped = trimmed[prefix.Length..];
            }
        }

        if (escaped is null)
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            var raw = uri.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
            var at = raw.IndexOf(PlaySegment, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            escaped = raw[(at + PlaySegment.Length)..];
        }

        if (escaped.Length == 0 || escaped.Contains('/', StringComparison.Ordinal) || escaped.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        key = Uri.UnescapeDataString(escaped);
        return key.Length > 0;
    }
}
