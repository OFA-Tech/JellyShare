using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Requests.Sync;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Sync;

/// <summary>
/// Starts a sync in the background (not tied to the request: a first sync with many images can take minutes) unless one
/// is running. The admin page polls the status for the outcome.
/// </summary>
public sealed class StartSyncCommandHandler : ICommandHandler<StartSyncCommand, SyncStartResponse>
{
    private readonly ISyncRunner _sync;
    private readonly ILogger<StartSyncCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="StartSyncCommandHandler"/> class.</summary>
    /// <param name="sync">The sync runner.</param>
    /// <param name="logger">The logger.</param>
    public StartSyncCommandHandler(ISyncRunner sync, ILogger<StartSyncCommandHandler> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<SyncStartResponse>> Handle(StartSyncCommand request, CancellationToken cancellationToken)
    {
        var started = _sync.TryStartBackground();
        _logger.LogInformation("JellyShare admin: sync requested ({State})", started ? "started" : "already running");
        return Task.FromResult(Result.Success(new SyncStartResponse(started, !started)));
    }
}
