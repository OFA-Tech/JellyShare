using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Modules;

/// <summary>
/// Logs the enabled infrastructure modules once at startup. Jellyfin offers no logger while services are registered,
/// so the loader only records them and this logs them.
/// </summary>
public sealed class InfrastructureModuleLog : IHostedService
{
    private readonly InfrastructureModuleLoader _loader;
    private readonly ILogger<InfrastructureModuleLoader> _logger;

    /// <summary>Initializes a new instance of the <see cref="InfrastructureModuleLog"/> class.</summary>
    /// <param name="loader">The loader that registered the modules.</param>
    /// <param name="logger">The logger.</param>
    public InfrastructureModuleLog(InfrastructureModuleLoader loader, ILogger<InfrastructureModuleLoader> logger)
    {
        _loader = loader;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loader.LogLoaded(_logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
