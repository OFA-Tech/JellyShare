namespace JellyShare.Application.Features.Relay;

/// <summary>Which client <c>Range</c> headers the relay passes on to a peer.</summary>
public static class RelayRange
{
    /// <summary>The longest <c>Range</c> header that is forwarded.</summary>
    public const int MaxLength = 256;

    /// <summary>Returns the header to forward, or <c>null</c> when it is empty, too long, not a bytes range or has control characters.</summary>
    /// <param name="range">The client's <c>Range</c> header value.</param>
    /// <returns>The trimmed header, or <c>null</c>.</returns>
    public static string? Forwardable(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)
            || range.Length > MaxLength
            || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || range.Any(char.IsControl))
        {
            return null;
        }

        return range.Trim();
    }
}
