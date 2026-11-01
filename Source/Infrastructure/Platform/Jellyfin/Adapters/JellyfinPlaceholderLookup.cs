using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Relay;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Adapters;

/// <summary><see cref="IPlaceholderLookup"/> backed by the Jellyfin library, devices and network settings.</summary>
public sealed class JellyfinPlaceholderLookup : IPlaceholderLookup
{
    private readonly ILibraryManager _library;
    private readonly IDeviceManager _devices;
    private readonly INetworkManager _network;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IConsumerIndex _index;
    private readonly ISettingsProvider _config;
    private readonly IRelayBase _relayBase;
    private readonly ILogger<JellyfinPlaceholderLookup> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinPlaceholderLookup"/> class.</summary>
    /// <param name="library">The library manager.</param>
    /// <param name="devices">The device manager (stored client capabilities).</param>
    /// <param name="network">The network manager.</param>
    /// <param name="serverConfig">The server configuration.</param>
    /// <param name="index">The merged index (catalog bitrate before the first probe).</param>
    /// <param name="config">The plugin configuration (peers).</param>
    /// <param name="relayBase">The relay base URL.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinPlaceholderLookup(
        ILibraryManager library,
        IDeviceManager devices,
        INetworkManager network,
        IServerConfigurationManager serverConfig,
        IConsumerIndex index,
        ISettingsProvider config,
        IRelayBase relayBase,
        ILogger<JellyfinPlaceholderLookup> logger)
    {
        _library = library;
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
    public Task<CallerLimits> GetCallerLimitsAsync(CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return Task.FromResult(new CallerLimits(RemoteLimit(caller), StoredProfileBitrate(caller.DeviceId)));
    }

    private int? StoredProfileBitrate(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        try
        {
            return _devices.GetCapabilities(deviceId)?.DeviceProfile?.MaxStreamingBitrate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "JellyShare: could not read the caller's stored device profile");
            return null;
        }
    }

    private int? RemoteLimit(CallerContext caller)
    {
        try
        {
            var limit = caller.UserRemoteBitrateLimit ?? 0;
            if (limit <= 0)
            {
                limit = _serverConfig.Configuration.RemoteClientBitrateLimit;
            }

            var ip = caller.RemoteAddress;
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
