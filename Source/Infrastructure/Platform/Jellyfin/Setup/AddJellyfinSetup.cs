using JellyShare.Application.Features.Peers;
using JellyShare.Application.Features.Streaming;
using JellyShare.Application.Storage;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Streaming;
using JellyShare.Domain.Interfaces.Sync;
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
        services.AddSingleton<IFfmpegRunner, FfmpegProcessRunner>();

        services.AddSingleton<IRelayBase, JellyfinRelayBase>();
        services.AddHttpClient(PeerClient.HttpClientName, c => c.Timeout = PeerClient.HttpClientTimeout)
            .ConfigurePrimaryHttpMessageHandler(PeerClient.CreatePrimaryHandler);
        services.AddSingleton<IPeerClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new PeerClient(() => factory.CreateClient(PeerClient.HttpClientName));
        });
        services.AddSingleton<ILocalLibrary, JellyfinLocalLibrary>();
        services.AddSingleton<IScanReporter, LibraryMonitorScanReporter>();
        services.AddSingleton<ILocalItemEvents, JellyfinLocalItemEvents>();

        services.AddSingleton<IConsumerIndex>(new ConsumerIndexFiles(dataFolder));
        services.AddSingleton<IPlaceholderLookup, JellyfinPlaceholderLookup>();
        services.AddSingleton<IAdminLibraries, JellyfinAdminLibraries>();
        PlaybackInfoFilter.Register(services);
        return services;
    }
}
