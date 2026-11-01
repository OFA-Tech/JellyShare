using System.Globalization;

namespace JellyShare.Application.Features.Relay;

/// <summary>Resolves the relay base URL.</summary>
public static class RelayBase
{
    /// <summary>
    /// Returns <paramref name="overrideValue"/> when it is a valid loopback URL (see <see cref="OverrideError"/>), else
    /// <c>http://127.0.0.1:{port}{baseUrl}</c> (base URL normalised to a leading slash and no trailing slash; empty when
    /// unset). Never ends with a slash. An invalid override is ignored here; the sync refuses to run with it.
    /// </summary>
    /// <param name="overrideValue">The configured <c>RelayBaseOverride</c>.</param>
    /// <param name="httpPort">The server's internal HTTP port.</param>
    /// <param name="baseUrl">The server's network <c>BaseUrl</c>.</param>
    /// <returns>The relay base URL.</returns>
    public static string Resolve(string? overrideValue, int httpPort, string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue) && OverrideError(overrideValue) is null)
        {
            return overrideValue.Trim().TrimEnd('/');
        }

        return Default(httpPort, baseUrl);
    }

    /// <summary>
    /// Validates <c>RelayBaseOverride</c>: it must be an absolute http(s) URL to a loopback host (127.x, [::1] or
    /// localhost) without query or fragment, because the relay only answers loopback callers.
    /// </summary>
    /// <param name="overrideValue">The configured override.</param>
    /// <returns>A clear error, or <c>null</c> when the override is unset or valid.</returns>
    public static string? OverrideError(string? overrideValue)
    {
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return null;
        }

        if (Uri.TryCreate(overrideValue.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsLoopback)
        {
            return null;
        }

        return "JellyShare: RelayBaseOverride must be a loopback URL such as http://127.0.0.1:8096, http://[::1]:8096 or http://localhost:8096 "
            + "(the relay only answers requests from this machine). Fix or clear it in the plugin configuration.";
    }

    private static string Default(int httpPort, string? baseUrl)
    {
        var path = (baseUrl ?? string.Empty).Trim().Trim('/');
        var port = httpPort.ToString(CultureInfo.InvariantCulture);
        return path.Length == 0 ? "http://127.0.0.1:" + port : "http://127.0.0.1:" + port + "/" + path;
    }
}
