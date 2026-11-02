using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Streaming;
using JellyShare.Jellyfin.Adapters;
using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Jellyfin.Setup;

/// <summary>Registers the Jellyfin adapters behind the Domain ports, the peer client, the files and the MVC filter.</summary>
public static class JellyfinSetup
{
    /// <summary>Registers everything that talks to Jellyfin, the network or the disk. All services are singletons.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataFolder">Returns the plugin data folder (resolved when first needed).</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddJellyfinSetup(this IServiceCollection services, Func<string> dataFolder)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataFolder);

        services.AddSingleton<IServerIdentity, ServerIdentity>();
        services.AddSingleton<ISettingsProvider, PluginSettingsProvider>();
        services.AddSingleton<ISettingsStore, PluginSettingsStore>();
        services.AddSingleton<ILocalItemSource, JellyfinLocalItemSource>();
        services.AddSingleton<IEncoderSettingsSource, JellyfinEncoderSettings>();

        services.AddSingleton<IRelayBase, JellyfinRelayBase>();
        services.AddSingleton<ILocalLibrary, JellyfinLocalLibrary>();
        services.AddSingleton<IScanReporter, LibraryMonitorScanReporter>();
        services.AddSingleton<ILocalItemEvents, JellyfinLocalItemEvents>();

        services.AddSingleton<IPlaceholderLookup, JellyfinPlaceholderLookup>();
        services.AddSingleton<IAdminLibraries, JellyfinAdminLibraries>();
        services.AddSingleton<IPlaybackStopper, JellyfinPlaybackStopper>();
        PlaybackInfoFilter.Register(services);
        return services;
    }
}
