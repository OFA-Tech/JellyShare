using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Relay;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;

namespace JellyShare.Jellyfin.Adapters;

/// <summary><see cref="IRelayBase"/> from the plugin override or the server's network configuration.</summary>
public sealed class JellyfinRelayBase : IRelayBase
{
    private readonly IServerApplicationHost _host;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ISettingsProvider _config;

    /// <summary>Initializes a new instance of the <see cref="JellyfinRelayBase"/> class.</summary>
    /// <param name="host">The application host (internal HTTP port).</param>
    /// <param name="serverConfig">The server configuration (network BaseUrl).</param>
    /// <param name="config">The plugin configuration.</param>
    public JellyfinRelayBase(IServerApplicationHost host, IServerConfigurationManager serverConfig, ISettingsProvider config)
    {
        _host = host;
        _serverConfig = serverConfig;
        _config = config;
    }

    /// <inheritdoc />
    public string Current
        => RelayBase.Resolve(_config.Current.RelayBaseOverride, _host.HttpPort, _serverConfig.GetNetworkConfiguration().BaseUrl);
}
