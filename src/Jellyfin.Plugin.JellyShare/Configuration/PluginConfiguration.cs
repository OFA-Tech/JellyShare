using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyShare.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin as XML.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the shares this server offers to peers (provider side).</summary>
    public List<PeerShare> Shares { get; set; } = new();

    /// <summary>Gets or sets the remote servers this server consumes (consumer side).</summary>
    public List<RemotePeer> Peers { get; set; } = new();

    /// <summary>Gets or sets the plugin-owned folder holding movie placeholders. Empty resolves to the default.</summary>
    public string MoviesPlaceholderPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin-owned folder holding show placeholders. Empty resolves to the default.</summary>
    public string ShowsPlaceholderPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the hours between catalog syncs.</summary>
    public int SyncIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets the seconds between presence sweeps of local files (clamped to 15..3600).</summary>
    public int PresenceSweepSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets how many capped (transcoded) streams this server runs at once across all shares.
    /// Further capped requests get 429 so the consumer can try another peer.
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; } = 4;

    /// <summary>Gets or sets an override for the loopback relay base URL used in placeholder files.</summary>
    public string? RelayBaseOverride { get; set; }

    /// <summary>
    /// Fills empty placeholder roots with <c>&lt;dataFolder&gt;/placeholders/{Movies,Shows}</c>.
    /// Configured values are kept. Does not touch the file system.
    /// </summary>
    /// <param name="cfg">The configuration to update in place.</param>
    /// <param name="dataFolder">The plugin data folder.</param>
    public static void ResolvePlaceholderRoots(PluginConfiguration cfg, string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);

        if (string.IsNullOrWhiteSpace(cfg.MoviesPlaceholderPath))
        {
            cfg.MoviesPlaceholderPath = Path.Combine(dataFolder, "placeholders", "Movies");
        }

        if (string.IsNullOrWhiteSpace(cfg.ShowsPlaceholderPath))
        {
            cfg.ShowsPlaceholderPath = Path.Combine(dataFolder, "placeholders", "Shows");
        }
    }

    /// <inheritdoc />
    public override string ToString()
        => $"PluginConfiguration(Shares={Shares.Count}, Peers={Peers.Count})";
}

/// <summary>A share offered to one peer. Only the hash of the share token is stored.</summary>
public class PeerShare
{
    /// <summary>Gets or sets the share id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name of the peer this share is for.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the lowercase hex SHA-256 of the share token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the share is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the ids of the libraries the peer may see.</summary>
    public List<Guid> LibraryIds { get; set; } = new();

    /// <summary>Gets or sets the maximum concurrent streams for the peer.</summary>
    public int MaxStreams { get; set; } = 2;

    /// <summary>Gets or sets the bitrate cap in bits per second; <c>null</c> means no cap.</summary>
    public int? MaxBitrate { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PeerShare({Id}, {Name})";
}

/// <summary>A remote peer this server syncs from. Holds that peer's secret token.</summary>
public class RemotePeer
{
    /// <summary>Gets or sets the peer id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the base URL of the remote Jellyfin server.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret token the remote server gave us.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the peer is used.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the priority; lower is preferred.</summary>
    public int Priority { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"RemotePeer({Id}, {Name})";
}
