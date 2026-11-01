using JellyShare.Data.Peers.Setup;
using JellyShare.Domain.Interfaces.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Data.Peers.Modules;

/// <summary>The outbound peer HTTP client. Always enabled: every server can receive from peers.</summary>
public sealed class PeersModule : IInfrastructureModule
{
    /// <inheritdoc />
    public string Name => "Peers";

    /// <inheritdoc />
    public bool IsEnabled(InfrastructureModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return true;
    }

    /// <inheritdoc />
    public string Describe(InfrastructureModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Name;
    }

    /// <inheritdoc />
    public void Register(IServiceCollection services, InfrastructureModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        services.AddDataPeersSetup();
    }
}
