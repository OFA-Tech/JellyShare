using Jellyfin.Plugin.JellyShare.Provider;
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
        serviceCollection.AddSingleton<ILocalItemSource, JellyfinLocalItemSource>();
        serviceCollection.AddSingleton<CatalogBuilder>(sp => new CatalogBuilder(
            sp.GetRequiredService<ILocalItemSource>(),
            sp.GetRequiredService<IServerIdentity>(),
            sp.GetRequiredService<IConfigProvider>()));
    }
}
