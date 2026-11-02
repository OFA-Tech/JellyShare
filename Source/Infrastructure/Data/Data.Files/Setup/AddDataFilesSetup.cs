using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Interfaces.Storage;
using JellyShare.Domain.Interfaces.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyShare.Data.Files.Setup;

/// <summary>Registers the files the plugin keeps: the JSON stores in its data folder and its placeholder roots.</summary>
public static class DataFilesSetup
{
    /// <summary>Registers every store and the placeholder writer as singletons.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataFolder">Returns the plugin data folder (resolved when first needed).</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddDataFilesSetup(this IServiceCollection services, Func<string> dataFolder)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataFolder);

        services.AddSingleton<IPeerStateStore>(PeerStateStore.InDataFolder(dataFolder));
        services.AddSingleton<IMergedIndexStore>(new MergedIndexStore(dataFolder));
        services.AddSingleton<ISyncReportStore>(new SyncReportStore(dataFolder));
        services.AddSingleton<IPlaceholderLedgerStore>(new PlaceholderLedgerStore(dataFolder));
        services.AddSingleton<IPendingScanStore>(sp => new PendingScanStore(dataFolder, sp.GetRequiredService<ILogger<PendingScanStore>>()));
        services.AddSingleton<IPlaceholderFiles>(sp => new PlaceholderWriter(sp.GetRequiredService<ILogger<PlaceholderWriter>>()));
        services.AddSingleton<IConsumerIndex>(new ConsumerIndexFiles(dataFolder));
        return services;
    }
}
