namespace JellyShare.Domain.Rules;

/// <summary>Peer address rules shared by the peer client and the admin validation.</summary>
public static class PeerUri
{
    /// <summary>Joins a peer base URL (which may have a sub-path) and an API path.</summary>
    /// <param name="baseUrl">The peer base URL, e.g. <c>https://h/jellyfin</c>.</param>
    /// <param name="relative">The API path, e.g. <c>JellyShare/v1/catalog</c>.</param>
    /// <returns>The absolute URI, or <c>null</c> when the base URL is not an absolute http(s) URL.</returns>
    public static Uri? Build(string? baseUrl, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var b)
            || (b.Scheme != Uri.UriSchemeHttp && b.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(b.Query)
            || !string.IsNullOrEmpty(b.Fragment))
        {
            return null;
        }

        return new Uri(b.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + relative.TrimStart('/'));
    }

    /// <summary>Normalizes a peer base URL the way it is stored: absolute http(s), no user info, no trailing slash.</summary>
    /// <param name="url">The URL as typed.</param>
    /// <returns>The normalized URL, or <c>null</c> when it is not valid.</returns>
    public static string? Normalize(string? url)
    {
        var uri = Build(url, string.Empty);
        return uri is null || !string.IsNullOrEmpty(uri.UserInfo) ? null : uri.AbsoluteUri.TrimEnd('/');
    }
}
