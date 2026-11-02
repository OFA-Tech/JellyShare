using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Models.Configuration;

namespace JellyShare.Jellyfin.Configuration;

/// <summary><see cref="ISettingsProvider"/> reading <see cref="Plugin.Instance"/> on every access.</summary>
public sealed class PluginSettingsProvider : ISettingsProvider
{
    /// <inheritdoc />
    public JellyShareSettings Current => (Plugin.Instance?.Configuration ?? new PluginConfiguration()).ToSettings();
}
