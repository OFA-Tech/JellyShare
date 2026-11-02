using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Configuration;
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
    private readonly IAccountSignIn _accounts;
    private readonly ILogger<DeletePeerCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="DeletePeerCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="sync">The sync runner.</param>
    /// <param name="accounts">Signs out of an account peer's server.</param>
    /// <param name="logger">The logger.</param>
    public DeletePeerCommandHandler(ISettingsProvider config, ISettingsStore store, ISyncRunner sync, IAccountSignIn accounts, ILogger<DeletePeerCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _sync = sync;
        _accounts = accounts;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> Handle(DeletePeerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RemotePeer removed;
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            var peer = cfg.Peers.FirstOrDefault(p => p.Id == request.Id);
            if (peer is null)
            {
                return Result.Failure(PeerErrors.NotFound);
            }

            removed = peer;
            cfg.Peers = cfg.Peers.Where(p => p.Id != request.Id).ToList();
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: removed peer {PeerId}", request.Id);

        // Its placeholders go with the next sync: start one now (or right after the one running).
        _sync.TryStartBackground(queueIfRunning: true);

        if (removed.Kind == PeerKind.Account && !string.IsNullOrEmpty(removed.Token))
        {
            try
            {
                await _accounts.SignOutAsync(removed.BaseUrl, removed.Token, removed.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // Best effort: the peer is already gone; the friend's session expires or is revoked on their side.
                _logger.LogDebug(ex, "JellyShare admin: sign-out of removed peer {PeerId} failed", request.Id);
            }
        }

        return Result.Success();
    }
}
