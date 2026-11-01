using MediaBrowser.Controller;

namespace JellyShare.Jellyfin.Provider;

/// <summary>
/// <see cref="IServerIdentity"/> backed by the Jellyfin application host.
/// </summary>
public sealed class ServerIdentity : IServerIdentity
{
    private readonly IServerApplicationHost _applicationHost;

    /// <summary>Initializes a new instance of the <see cref="ServerIdentity"/> class.</summary>
    /// <param name="applicationHost">The Jellyfin application host.</param>
    public ServerIdentity(IServerApplicationHost applicationHost)
    {
        _applicationHost = applicationHost;
    }

    /// <inheritdoc />
    public string ServerName => _applicationHost.FriendlyName;
}
