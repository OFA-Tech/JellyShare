using Jellyfin.Plugin.JellyShare.Provider;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShare.Consumer;

/// <summary><see cref="IPlaceholderLookup"/> backed by the Jellyfin library, users and network settings.</summary>
public sealed class JellyfinPlaceholderLookup : IPlaceholderLookup
{
    private readonly ILibraryManager _library;
    private readonly IAuthorizationContext _auth;
    private readonly IDeviceManager _devices;
    private readonly INetworkManager _network;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IConsumerIndex _index;
    private readonly IConfigProvider _config;
    private readonly IRelayBase _relayBase;
    private readonly ILogger<JellyfinPlaceholderLookup> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinPlaceholderLookup"/> class.</summary>
    /// <param name="library">The library manager.</param>
    /// <param name="auth">The authorization context.</param>
    /// <param name="devices">The device manager (stored client capabilities).</param>
    /// <param name="network">The network manager.</param>
    /// <param name="serverConfig">The server configuration.</param>
    /// <param name="index">The merged index (catalog bitrate before the first probe).</param>
    /// <param name="config">The plugin configuration (peers).</param>
    /// <param name="relayBase">The relay base URL.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinPlaceholderLookup(
        ILibraryManager library,
        IAuthorizationContext auth,
        IDeviceManager devices,
        INetworkManager network,
        IServerConfigurationManager serverConfig,
        IConsumerIndex index,
        IConfigProvider config,
        IRelayBase relayBase,
        ILogger<JellyfinPlaceholderLookup> logger)
    {
        _library = library;
        _auth = auth;
        _devices = devices;
        _network = network;
        _serverConfig = serverConfig;
        _index = index;
        _config = config;
        _relayBase = relayBase;
        _logger = logger;
    }

    /// <inheritdoc />
    public PlaceholderMedia? Find(Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is null || !item.IsShortcut || !RelayUrl.TryGetKey(item.ShortcutPath, _relayBase.Current, out var key))
        {
            return null;
        }

        return new PlaceholderMedia(key, item.TotalBitrate is > 0 ? item.TotalBitrate : CatalogBitrate(key));
    }

    /// <inheritdoc />
    public async Task<CallerLimits> GetCallerLimitsAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthorizationInfo info;
        try
        {
            info = await _auth.GetAuthorizationInfo(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "JellyShare: could not read the caller's authorization");
            return new CallerLimits(null, null);
        }

        return new CallerLimits(RemoteLimit(context, info), StoredProfileBitrate(info));
    }

    private int? StoredProfileBitrate(AuthorizationInfo info)
    {
        if (string.IsNullOrEmpty(info.DeviceId))
        {
            return null;
        }

        try
        {
            return _devices.GetCapabilities(info.DeviceId)?.DeviceProfile?.MaxStreamingBitrate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "JellyShare: could not read the caller's stored device profile");
            return null;
        }
    }

    private int? RemoteLimit(HttpContext context, AuthorizationInfo info)
    {
        try
        {
            var limit = info.User?.RemoteClientBitrateLimit ?? 0;
            if (limit <= 0)
            {
                limit = _serverConfig.Configuration.RemoteClientBitrateLimit;
            }

            var ip = context.Connection.RemoteIpAddress;
            if (limit <= 0 || ip is null)
            {
                return null;
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            return _network.IsInLocalNetwork(ip) ? null : limit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "JellyShare: could not read the caller's bitrate limit");
            return null;
        }
    }

    private int? CatalogBitrate(string key)
    {
        var entry = _index.Find(key);
        if (entry is null)
        {
            return null;
        }

        var preferred = SourceSelector.Order(entry, _config.Current.Peers, _index.PeerStates()).FirstOrDefault();
        return preferred?.Item.Media?.BitRate
            ?? entry.Sources.Select(s => s.Item.Media?.BitRate).Where(b => b is > 0).DefaultIfEmpty().Max();
    }
}
