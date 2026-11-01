using System.Reflection;
using JellyShare.Application.Behaviors;
using JellyShare.Application.Dispatching;
using JellyShare.Domain.Interfaces.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Application.Setup;

/// <summary>Registers the sender, the pipeline behaviors and every handler and validator of the Application assembly.</summary>
public static class DispatcherSetup
{
    /// <summary>
    /// Registers <see cref="ISender"/>, the Logging (outer) and Validation (inner) behaviors, and by reflection every
    /// closed <see cref="IRequestHandler{TRequest, TResponse}"/> and <see cref="IValidator{TRequest}"/> implementation.
    /// All are singletons: they are stateless and depend only on singletons.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddDispatcherSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISender, Sender>();
        services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        var assembly = typeof(DispatcherSetup).Assembly;
        RegisterClosedImplementations(services, assembly, typeof(IRequestHandler<,>));
        RegisterClosedImplementations(services, assembly, typeof(IValidator<>));
        return services;
    }

    private static void RegisterClosedImplementations(IServiceCollection services, Assembly assembly, Type openContract)
    {
        var candidates = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false });
        foreach (var implementation in candidates)
        {
            var contracts = implementation.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == openContract);
            foreach (var contract in contracts)
            {
                services.AddSingleton(contract, implementation);
            }
        }
    }
}
