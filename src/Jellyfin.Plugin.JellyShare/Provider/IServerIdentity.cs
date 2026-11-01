namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>
/// Identity of the local Jellyfin server, abstracted so controllers stay unit-testable.
/// </summary>
public interface IServerIdentity
{
    /// <summary>Gets the friendly server name.</summary>
    string ServerName { get; }
}
