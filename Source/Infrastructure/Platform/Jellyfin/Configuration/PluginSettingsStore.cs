using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Models.Configuration;

namespace JellyShare.Jellyfin.Configuration;

/// <summary><see cref="ISettingsStore"/> writing into <see cref="Plugin.Instance"/> and saving its XML.</summary>
public sealed class PluginSettingsStore : ISettingsStore
{
    /// <inheritdoc />
    public void Save(JellyShareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.Apply(settings);
        plugin.SaveConfiguration();
    }
}
