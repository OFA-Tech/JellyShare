using JellyShare.Domain.Interfaces.Modules;
using JellyShare.Media.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Media.Modules;

/// <summary>The ffmpeg process runner for capped (transcoded) streams. Always enabled: a cap can be set on any share.</summary>
public sealed class MediaModule : IInfrastructureModule
{
    /// <inheritdoc />
    public string Name => "Media";

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
        services.AddMediaSetup();
    }
}