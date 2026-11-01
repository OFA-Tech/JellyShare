using JellyShare.Application.Features.Sync;
using JellyShare.Domain.Interfaces.Configuration;
using MediaBrowser.Model.Tasks;

namespace JellyShare.Jellyfin.Tasks;

/// <summary>Scheduled task "JellyShare: sync peers". Also runnable on demand from the dashboard.</summary>
public sealed class SyncTask : IScheduledTask
{
    private readonly SyncService _sync;
    private readonly ISettingsProvider _config;

    /// <summary>Initializes a new instance of the <see cref="SyncTask"/> class.</summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="config">The plugin configuration.</param>
    public SyncTask(SyncService sync, ISettingsProvider config)
    {
        _sync = sync;
        _config = config;
    }

    /// <inheritdoc />
    public string Name => "JellyShare: sync peers";

    /// <inheritdoc />
    public string Key => "JellyShareSyncPeers";

    /// <inheritdoc />
    public string Description => "Fetches the catalogs of JellyShare peers and updates the placeholder folders.";

    /// <inheritdoc />
    public string Category => "JellyShare";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress?.Report(0);
        await _sync.RunAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Math.Clamp(_config.Current.SyncIntervalHours, 1, 24 * 7);
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(hours).Ticks,
        };
    }
}
