using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Peers;

/// <summary>
/// Updates a peer (copy-on-write, so a running sync keeps a consistent URL and token). An empty token keeps the stored
/// one. A missing peer is reported before the fields are validated, so this has no validator.
/// </summary>
public sealed class UpdatePeerCommandHandler : ICommandHandler<UpdatePeerCommand, PeerView>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly IConsumerIndex _consumerIndex;
    private readonly ILogger<UpdatePeerCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdatePeerCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="consumerIndex">The persisted peer states.</param>
    /// <param name="logger">The logger.</param>
    public UpdatePeerCommandHandler(ISettingsProvider config, ISettingsStore store, IConsumerIndex consumerIndex, ILogger<UpdatePeerCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _consumerIndex = consumerIndex;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<PeerView>> Handle(UpdatePeerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Update(request));
    }

    private Result<PeerView> Update(UpdatePeerCommand request)
    {
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            var current = cfg.Peers.FirstOrDefault(p => p.Id == request.Id);
            if (current is null)
            {
                return PeerErrors.NotFound;
            }

            var name = AdminRules.CleanName(request.Name);
            var url = AdminRules.NormalizeUrl(request.BaseUrl);
            if (name is null)
            {
                return PeerErrors.InvalidName;
            }

            if (url is null)
            {
                return PeerErrors.InvalidUrl;
            }

            var newToken = request.Token?.Trim();
            if (!string.IsNullOrEmpty(newToken) && AdminRules.TokenError(newToken) is { } problem)
            {
                return PeerErrors.InvalidToken(problem);
            }

            // Copy-on-write: a running sync keeps a self-consistent (url, token) pair.
            var peer = new RemotePeer
            {
                Id = current.Id,
                Name = name,
                BaseUrl = url,
                Token = string.IsNullOrEmpty(newToken) ? current.Token : newToken,
                Enabled = request.Enabled ?? current.Enabled,
                Priority = current.Priority,
            };
            cfg.Peers = cfg.Peers.Select(p => p.Id == request.Id ? peer : p).ToList();
            _store.Save(cfg);
            _logger.LogInformation("JellyShare admin: updated peer {PeerId} (enabled={Enabled})", peer.Id, peer.Enabled);
            return Result.Success(AdminRules.ToView(peer, _consumerIndex.PeerStates()));
        }
    }
}
