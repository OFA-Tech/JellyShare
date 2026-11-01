using System.Globalization;
using Jellyfin.Plugin.JellyShare.Provider;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary>Provides the loopback relay base URL written into placeholder <c>.strm</c> files.</summary>
public interface IRelayBase
{
    /// <summary>Gets the relay base URL, without a trailing slash.</summary>
    string Current { get; }
}

/// <summary>Resolves the relay base URL.</summary>
public static class RelayBase
{
    /// <summary>
    /// Returns <paramref name="overrideValue"/> when set, else <c>http://127.0.0.1:{port}{baseUrl}</c>
    /// (base URL normalised to a leading slash and no trailing slash; empty when unset). Never ends with a slash.
    /// </summary>
    /// <param name="overrideValue">The configured <c>RelayBaseOverride</c>.</param>
    /// <param name="httpPort">The server's internal HTTP port.</param>
    /// <param name="baseUrl">The server's network <c>BaseUrl</c>.</param>
    /// <returns>The relay base URL.</returns>
    public static string Resolve(string? overrideValue, int httpPort, string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue.Trim().TrimEnd('/');
        }

        var path = (baseUrl ?? string.Empty).Trim().Trim('/');
        var port = httpPort.ToString(CultureInfo.InvariantCulture);
        return path.Length == 0 ? "http://127.0.0.1:" + port : "http://127.0.0.1:" + port + "/" + path;
    }
}

/// <summary><see cref="IRelayBase"/> from the plugin override or the server's network configuration.</summary>
public sealed class JellyfinRelayBase : IRelayBase
{
    private readonly IServerApplicationHost _host;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IConfigProvider _config;

    /// <summary>Initializes a new instance of the <see cref="JellyfinRelayBase"/> class.</summary>
    /// <param name="host">The application host (internal HTTP port).</param>
    /// <param name="serverConfig">The server configuration (network BaseUrl).</param>
    /// <param name="config">The plugin configuration.</param>
    public JellyfinRelayBase(IServerApplicationHost host, IServerConfigurationManager serverConfig, IConfigProvider config)
    {
        _host = host;
        _serverConfig = serverConfig;
        _config = config;
    }

    /// <inheritdoc />
    public string Current
        => RelayBase.Resolve(_config.Current.RelayBaseOverride, _host.HttpPort, _serverConfig.GetNetworkConfiguration().BaseUrl);
}
