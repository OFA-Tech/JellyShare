using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyShare.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the shares this server offers to peers (provider side).</summary>
    public List<PeerShare> Shares { get; set; } = new();

    /// <summary>Gets or sets the remote servers this server consumes (consumer side).</summary>
    public List<RemotePeer> Peers { get; set; } = new();
}

/// <summary>A share offered to one peer. Fields are added by later tasks.</summary>
public class PeerShare
{
}

/// <summary>A remote peer this server syncs from. Fields are added by later tasks.</summary>
public class RemotePeer
{
}
