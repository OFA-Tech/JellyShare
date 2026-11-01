using Jellyfin.Plugin.JellyShare.Consumer;
using Jellyfin.Plugin.JellyShare.Provider;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyShare;

/// <summary>
/// Registers JellyShare services with the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IServerIdentity, ServerIdentity>();
        serviceCollection.AddSingleton<IConfigProvider, PluginConfigProvider>();
        serviceCollection.AddSingleton<ShareTokenService>();
        serviceCollection.AddSingleton(new StreamLimiter(TimeProvider.System));
        serviceCollection.AddSingleton<ILocalItemSource, JellyfinLocalItemSource>();
        serviceCollection.AddSingleton<CatalogBuilder>(sp => new CatalogBuilder(
            sp.GetRequiredService<ILocalItemSource>(),
            sp.GetRequiredService<IServerIdentity>(),
            sp.GetRequiredService<IConfigProvider>()));

        serviceCollection.AddSingleton<IRelayBase, JellyfinRelayBase>();
        serviceCollection.AddHttpClient(PeerClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(PeerClient.CreatePrimaryHandler);
        serviceCollection.AddSingleton<IPeerClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new PeerClient(() => factory.CreateClient(PeerClient.HttpClientName));
        });
        serviceCollection.AddSingleton<ILocalLibrary, JellyfinLocalLibrary>();
        serviceCollection.AddSingleton<ILocalIndex>(sp => new LocalIndex(
            sp.GetRequiredService<ILocalLibrary>(),
            sp.GetRequiredService<IConfigProvider>()));
        serviceCollection.AddSingleton<IScanReporter, LibraryMonitorScanReporter>();
        serviceCollection.AddSingleton(sp => new SyncService(
            sp.GetRequiredService<IConfigProvider>(),
            sp.GetRequiredService<IPeerClient>(),
            sp.GetRequiredService<ILocalIndex>(),
            sp.GetRequiredService<IScanReporter>(),
            sp.GetRequiredService<IRelayBase>(),
            () => Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("JellyShare plugin not loaded"),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<SyncService>>()));

        serviceCollection.AddSingleton<ILocalItemEvents, JellyfinLocalItemEvents>();
        serviceCollection.AddSingleton(sp => new LocalItemWatcher(
            sp.GetRequiredService<SyncService>(),
            sp.GetRequiredService<ILocalItemEvents>(),
            sp.GetRequiredService<ILocalLibrary>(),
            sp.GetRequiredService<IConfigProvider>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<LocalItemWatcher>>()));
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<LocalItemWatcher>());

        serviceCollection.AddSingleton<IConsumerIndex>(new ConsumerIndexFiles(
            () => Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("JellyShare plugin not loaded")));
        serviceCollection.AddSingleton(new RelaySessions(TimeProvider.System));
        serviceCollection.AddSingleton<IPlaceholderLookup, JellyfinPlaceholderLookup>();
        PlaybackInfoFilter.Register(serviceCollection);
    }
}
