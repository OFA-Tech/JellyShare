using JellyShare.Application.Features.Catalog;
using JellyShare.Application.Features.Relay;
using JellyShare.Application.Features.Streaming;
using JellyShare.Application.Features.Sync;
using JellyShare.Domain.Interfaces.Catalog;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Storage;
using JellyShare.Domain.Interfaces.Streaming;
using JellyShare.Domain.Interfaces.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Setup;

/// <summary>Registers the Application layer (the use cases). All services are singletons.</summary>
public static class ApplicationSetup
{
    /// <summary>Registers the catalog, streaming, relay and sync services.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddApplicationSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDispatcherSetup();
        services.AddSingleton<IShareTokenService, ShareTokenService>();
        services.AddSingleton(new StreamLimiter(TimeProvider.System));
        services.AddSingleton(sp => new CappedStreamService(
            sp.GetRequiredService<IEncoderSettingsSource>(),
            sp.GetRequiredService<IFfmpegRunner>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<CappedStreamService>>()));
        services.AddSingleton<CatalogBuilder>(sp => new CatalogBuilder(
            sp.GetRequiredService<ILocalItemSource>(),
            sp.GetRequiredService<IServerIdentity>(),
            sp.GetRequiredService<ISettingsProvider>(),
            sp.GetRequiredService<IAdminLibraries>()));

        services.AddSingleton<ILocalIndex>(sp => new LocalIndex(
            sp.GetRequiredService<ILocalLibrary>(),
            sp.GetRequiredService<ISettingsProvider>()));
        services.AddSingleton(sp => new SyncService(
            sp.GetRequiredService<ISettingsProvider>(),
            sp.GetRequiredService<IPeerClient>(),
            sp.GetRequiredService<ILocalIndex>(),
            sp.GetRequiredService<IScanReporter>(),
            sp.GetRequiredService<IRelayBase>(),
            sp.GetRequiredService<IPeerStateStore>(),
            sp.GetRequiredService<IMergedIndexStore>(),
            sp.GetRequiredService<ISyncReportStore>(),
            sp.GetRequiredService<IPlaceholderLedgerStore>(),
            sp.GetRequiredService<IPendingScanStore>(),
            sp.GetRequiredService<IPlaceholderFiles>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<SyncService>>()));
        services.AddSingleton(sp => new LocalItemWatcher(
            sp.GetRequiredService<SyncService>(),
            sp.GetRequiredService<IMergedIndexStore>(),
            sp.GetRequiredService<IPlaceholderFiles>(),
            sp.GetRequiredService<ILocalItemEvents>(),
            sp.GetRequiredService<ILocalLibrary>(),
            sp.GetRequiredService<ISettingsProvider>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<LocalItemWatcher>>()));
        services.AddHostedService(sp => sp.GetRequiredService<LocalItemWatcher>());

        services.AddSingleton(new RelaySessions(TimeProvider.System));
        services.AddSingleton<ISyncRunner>(sp => sp.GetRequiredService<SyncService>());
        return services;
    }
}
