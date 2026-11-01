namespace JellyShare.Domain.Models.Configuration;

/// <summary>The plugin settings, independent of how the host stores them.</summary>
public class JellyShareSettings
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
    /// <param name="settings">The settings to update in place.</param>
    /// <param name="dataFolder">The plugin data folder.</param>
    public static void ResolvePlaceholderRoots(JellyShareSettings settings, string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);

        settings.MoviesPlaceholderPath = ResolvePlaceholderRoot(settings.MoviesPlaceholderPath, dataFolder, "Movies");
        settings.ShowsPlaceholderPath = ResolvePlaceholderRoot(settings.ShowsPlaceholderPath, dataFolder, "Shows");
    }

    /// <summary>Returns <paramref name="configured"/>, or <c>&lt;dataFolder&gt;/placeholders/&lt;kind&gt;</c> when it is empty.</summary>
    /// <param name="configured">The configured root.</param>
    /// <param name="dataFolder">The plugin data folder.</param>
    /// <param name="kind"><c>Movies</c> or <c>Shows</c>.</param>
    /// <returns>The root to use.</returns>
    public static string ResolvePlaceholderRoot(string? configured, string dataFolder, string kind)
        => string.IsNullOrWhiteSpace(configured) ? Path.Combine(dataFolder, "placeholders", kind) : configured;

    /// <inheritdoc />
    public override string ToString()
        => $"JellyShareSettings(Shares={Shares.Count}, Peers={Peers.Count})";
}
