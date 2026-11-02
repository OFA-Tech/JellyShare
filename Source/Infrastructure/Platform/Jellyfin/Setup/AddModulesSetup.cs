using JellyShare.Domain.Interfaces.Modules;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Jellyfin.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Jellyfin.Setup;

/// <summary>Registers the infrastructure modules (peer client, files, media).</summary>
public static class ModulesSetup
{
    /// <summary>Registers every enabled infrastructure module and logs them at startup.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="settings">The settings known at registration time.</param>
    /// <param name="dataFolder">Returns the plugin data folder (resolved when first needed).</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddModulesSetup(this IServiceCollection services, JellyShareSettings settings, Func<string> dataFolder)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dataFolder);

        new InfrastructureModuleLoader().Load(services, new InfrastructureModuleContext(settings, dataFolder));
        services.AddHostedService<InfrastructureModuleLog>();
        return services;
    }
}
