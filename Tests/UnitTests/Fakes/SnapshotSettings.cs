using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Models.Configuration;

namespace JellyShare.UnitTests.Fakes;

/// <summary>
/// Settings as the real provider behaves: every read of <see cref="Current"/> returns a NEW snapshot (the share and
/// peer lists are shared with the stored settings, like <c>PluginConfiguration.ToSettings()</c>), so assigning a
/// property of a snapshot changes nothing until it is passed to <see cref="Store"/> (<c>ISettingsStore.Save</c>).
/// Tests set up state through <see cref="Settings"/> and read results through <see cref="Current"/>.
/// </summary>
internal abstract class SnapshotSettings : ISettingsProvider
{
    /// <summary>Gets the stored settings (what a test arranges, and what only a save changes).</summary>
    public JellyShareSettings Settings { get; } = new();

    /// <inheritdoc />
    public JellyShareSettings Current => Snapshot(Settings);

    /// <summary>Copies <paramref name="saved"/> into the stored settings, like <c>PluginConfiguration.Apply</c>.</summary>
    /// <param name="saved">The settings passed to the store.</param>
    public void Store(JellyShareSettings saved)
    {
        Settings.Shares = saved.Shares;
        Settings.Peers = saved.Peers;
        Settings.MoviesPlaceholderPath = saved.MoviesPlaceholderPath;
        Settings.ShowsPlaceholderPath = saved.ShowsPlaceholderPath;
        Settings.SyncIntervalHours = saved.SyncIntervalHours;
        Settings.PresenceSweepSeconds = saved.PresenceSweepSeconds;
        Settings.MaxConcurrentTranscodes = saved.MaxConcurrentTranscodes;
        Settings.RelayBaseOverride = saved.RelayBaseOverride;
    }

    private static JellyShareSettings Snapshot(JellyShareSettings source) => new()
    {
        Shares = source.Shares,
        Peers = source.Peers,
        MoviesPlaceholderPath = source.MoviesPlaceholderPath,
        ShowsPlaceholderPath = source.ShowsPlaceholderPath,
        SyncIntervalHours = source.SyncIntervalHours,
        PresenceSweepSeconds = source.PresenceSweepSeconds,
        MaxConcurrentTranscodes = source.MaxConcurrentTranscodes,
        RelayBaseOverride = source.RelayBaseOverride,
    };
}
