using JellyShare.Domain.Models.Configuration;

namespace JellyShare.Domain.Interfaces.Configuration;

/// <summary>Provides the current plugin settings at request time.</summary>
public interface ISettingsProvider
{
    /// <summary>
    /// Gets a snapshot of the current settings. Collections are shared with the stored settings and are never
    /// changed in place (copy-on-write): to change settings, assign new collections and pass the snapshot to
    /// <see cref="ISettingsStore.Save"/>.
    /// </summary>
    JellyShareSettings Current { get; }
}
