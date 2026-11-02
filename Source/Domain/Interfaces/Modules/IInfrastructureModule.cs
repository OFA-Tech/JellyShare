using JellyShare.Domain.Models.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Domain.Interfaces.Modules;

/// <summary>What an infrastructure module may look at when it registers itself.</summary>
/// <param name="Settings">
/// The plugin settings as known at registration time (the defaults when the plugin has not been created yet). Services
/// read live settings through <c>ISettingsProvider</c>; this snapshot is only for deciding what to register.
/// </param>
/// <param name="DataFolder">Returns the plugin data folder (resolved when first needed, never at registration).</param>
public sealed record InfrastructureModuleContext(JellyShareSettings Settings, Func<string> DataFolder);

/// <summary>An infrastructure project that registers its services as one unit (template module pattern).</summary>
public interface IInfrastructureModule
{
    /// <summary>Gets the unique module name.</summary>
    string Name { get; }

    /// <summary>Checks whether the module should be registered.</summary>
    /// <param name="context">The module context.</param>
    /// <returns><c>true</c> when enabled.</returns>
    bool IsEnabled(InfrastructureModuleContext context);

    /// <summary>Describes the module for the startup log.</summary>
    /// <param name="context">The module context.</param>
    /// <returns>A short description, without secrets.</returns>
    string Describe(InfrastructureModuleContext context);

    /// <summary>Registers the module's services.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="context">The module context.</param>
    void Register(IServiceCollection services, InfrastructureModuleContext context);
}
