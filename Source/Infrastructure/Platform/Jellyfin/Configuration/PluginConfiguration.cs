using JellyShare.Domain.Models.Configuration;
using MediaBrowser.Model.Plugins;

namespace JellyShare.Jellyfin.Configuration;

/// <summary>
/// Plugin configuration persisted by Jellyfin as XML. Same properties (names, order, defaults) as
/// <see cref="JellyShareSettings"/>, so the stored XML keeps its shape; the rest of the plugin works with
/// <see cref="JellyShareSettings"/> through <see cref="ToSettings"/> and <see cref="Apply"/>.
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

        var settings = new JellyShareSettings
        {
            MoviesPlaceholderPath = cfg.MoviesPlaceholderPath,
            ShowsPlaceholderPath = cfg.ShowsPlaceholderPath,
        };
        JellyShareSettings.ResolvePlaceholderRoots(settings, dataFolder);
        cfg.MoviesPlaceholderPath = settings.MoviesPlaceholderPath;
        cfg.ShowsPlaceholderPath = settings.ShowsPlaceholderPath;
    }

    /// <summary>
    /// Creates a settings snapshot. The share and peer lists are shared, not copied: they are never changed in place
    /// (copy-on-write), so a reader keeps a self-consistent view while the admin page saves new lists.
    /// </summary>
    /// <returns>The settings.</returns>
    public JellyShareSettings ToSettings() => new()
    {
        Shares = Shares,
        Peers = Peers,
        MoviesPlaceholderPath = MoviesPlaceholderPath,
        ShowsPlaceholderPath = ShowsPlaceholderPath,
        SyncIntervalHours = SyncIntervalHours,
        PresenceSweepSeconds = PresenceSweepSeconds,
        MaxConcurrentTranscodes = MaxConcurrentTranscodes,
        RelayBaseOverride = RelayBaseOverride,
    };

    /// <summary>Copies every setting from <paramref name="settings"/> into this configuration.</summary>
    /// <param name="settings">The settings.</param>
    public void Apply(JellyShareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Shares = settings.Shares;
        Peers = settings.Peers;
        MoviesPlaceholderPath = settings.MoviesPlaceholderPath;
        ShowsPlaceholderPath = settings.ShowsPlaceholderPath;
        SyncIntervalHours = settings.SyncIntervalHours;
        PresenceSweepSeconds = settings.PresenceSweepSeconds;
        MaxConcurrentTranscodes = settings.MaxConcurrentTranscodes;
        RelayBaseOverride = settings.RelayBaseOverride;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"PluginConfiguration(Shares={Shares.Count}, Peers={Peers.Count})";
}
