using JellyShare.Data.Files.Modules;
using JellyShare.Data.Peers.Modules;
using JellyShare.Domain.Interfaces.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Modules;

/// <summary>Registers the infrastructure modules from an explicit list (template module loader).</summary>
public sealed class InfrastructureModuleLoader
{
    /// <summary>Initializes a new instance of the <see cref="InfrastructureModuleLoader"/> class with every module.</summary>
    public InfrastructureModuleLoader()
        : this(
            [
                new PeersModule(),
                new FilesModule(),
            ])
    {
    }

    /// <summary>Initializes a new instance of the <see cref="InfrastructureModuleLoader"/> class.</summary>
    /// <param name="modules">The modules, in registration order.</param>
    /// <exception cref="InvalidOperationException">Two modules share a name.</exception>
    public InfrastructureModuleLoader(IReadOnlyList<IInfrastructureModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var duplicates = modules
            .GroupBy(module => module.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate infrastructure module names: {string.Join(", ", duplicates)}.");
        }

        Modules = modules;
    }

    /// <summary>Gets the modules, in registration order.</summary>
    public IReadOnlyList<IInfrastructureModule> Modules { get; }

    /// <summary>Gets the descriptions of the modules registered by <see cref="Load"/>.</summary>
    public IReadOnlyList<string> Loaded { get; private set; } = [];

    /// <summary>Registers every enabled module, then this loader (so the enabled modules can be logged at startup).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="context">The module context.</param>
    /// <returns>The descriptions of the registered modules.</returns>
    public IReadOnlyList<string> Load(IServiceCollection services, InfrastructureModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        var loaded = new List<string>();
        foreach (var module in Modules.Where(module => module.IsEnabled(context)))
        {
            module.Register(services, context);
            loaded.Add(module.Describe(context));
        }

        Loaded = loaded;
        services.AddSingleton(this);
        return loaded;
    }

    /// <summary>Logs the enabled modules.</summary>
    /// <param name="logger">The logger.</param>
    public void LogLoaded(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation(
            "JellyShare infrastructure modules enabled: {EnabledModules}.",
            Loaded.Count > 0 ? string.Join(", ", Loaded) : "none");
    }
}
