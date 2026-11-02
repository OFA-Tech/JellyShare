using JellyShare.Data.Files.Setup;
using JellyShare.Domain.Interfaces.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace JellyShare.Data.Files.Modules;

/// <summary>The plugin's files (data folder stores and placeholder roots). Always enabled.</summary>
public sealed class FilesModule : IInfrastructureModule
{
    /// <inheritdoc />
    public string Name => "Files";

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
        services.AddDataFilesSetup(context.DataFolder);
    }
}
