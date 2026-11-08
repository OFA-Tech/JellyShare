using JellyShare.Application.Features.Sync;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Relay;
using JellyShare.Domain.Interfaces.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyShare.UnitTests.Fakes;

/// <summary>Builds the sync service and watcher over the real Data.Files stores in a data folder.</summary>
internal static class TestSync
{
    public static SyncService Create(
        ISettingsProvider config,
        IPeerClient client,
        ILocalIndex localIndex,
        IScanReporter scans,
        IRelayBase relay,
        Func<string> dataDir,
        TimeProvider clock,
        ILogger<SyncService> logger,
        IAdminLibraries? libraries = null)
        => new(
            config,
            client,
            localIndex,
            scans,
            relay,
            PeerStateStore.InDataFolder(dataDir),
            new MergedIndexStore(dataDir),
            new SyncReportStore(dataDir),
            new PlaceholderLedgerStore(dataDir),
            new PendingScanStore(dataDir, NullLogger<PendingScanStore>.Instance),
            new PlaceholderWriter(logger),
            libraries ?? new FakeLibraries(),
            clock,
            logger);
}
