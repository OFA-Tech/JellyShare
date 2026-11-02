using JellyShare.Domain.Models.Configuration;

namespace JellyShare.Domain.Interfaces.Configuration;

/// <summary>Persists the plugin settings after the admin page changed them.</summary>
public interface ISettingsStore
{
    /// <summary>Makes <paramref name="settings"/> the current settings and saves them.</summary>
    /// <param name="settings">The changed settings (usually a snapshot from <see cref="ISettingsProvider.Current"/>).</param>
    void Save(JellyShareSettings settings);
}
