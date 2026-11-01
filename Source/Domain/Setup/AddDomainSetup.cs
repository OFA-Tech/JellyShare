using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Domain.Setup;

/// <summary>Registers the Domain layer.</summary>
public static class DomainSetup
{
    /// <summary>
    /// Registers Domain services. The Domain holds models, ports and pure rules only, so nothing is registered today;
    /// the composition root still calls this so a Domain service needs no change outside this project.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddDomainSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
