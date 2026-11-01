using JellyShare.Application.Setup;
using JellyShare.Domain.Setup;
using JellyShare.Jellyfin.Setup;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Jellyfin;

/// <summary>
/// Registers JellyShare services with the Jellyfin dependency injection container (the composition root).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddDomainSetup()
            .AddApplicationSetup(DataFolder)
            .AddJellyfinSetup(DataFolder);
    }

    private static string DataFolder()
        => Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("JellyShare plugin not loaded");
}
