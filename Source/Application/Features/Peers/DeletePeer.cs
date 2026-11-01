using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Peers;

/// <summary>Removes a peer, then starts a sync (or queues one after the running sync) so its placeholders go.</summary>
public sealed class DeletePeerCommandHandler : ICommandHandler<DeletePeerCommand>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly ISyncRunner _sync;
    private readonly ILogger<DeletePeerCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="DeletePeerCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="sync">The sync runner.</param>
    /// <param name="logger">The logger.</param>
    public DeletePeerCommandHandler(ISettingsProvider config, ISettingsStore store, ISyncRunner sync, ILogger<DeletePeerCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _sync = sync;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Handle(DeletePeerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            if (cfg.Peers.All(p => p.Id != request.Id))
            {
                return Task.FromResult(Result.Failure(PeerErrors.NotFound));
            }

            cfg.Peers = cfg.Peers.Where(p => p.Id != request.Id).ToList();
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: removed peer {PeerId}", request.Id);

        // Its placeholders go with the next sync: start one now (or right after the one running).
        _sync.TryStartBackground(queueIfRunning: true);
        return Task.FromResult(Result.Success());
    }
}