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

/// <summary>A new peer needs a name, a usable URL and a token from the invitation.</summary>
public sealed class AddPeerCommandValidator : IValidator<AddPeerCommand>
{
    /// <inheritdoc />
    public Error? Validate(AddPeerCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = request.Token?.Trim() ?? string.Empty;
        if (AdminRules.CleanName(request.Name) is null)
        {
            return PeerErrors.InvalidName;
        }

        if (AdminRules.NormalizeUrl(request.BaseUrl) is null)
        {
            return PeerErrors.InvalidUrl;
        }

        if (token.Length == 0)
        {
            return PeerErrors.MissingToken;
        }

        return AdminRules.TokenError(token) is { } problem ? PeerErrors.InvalidToken(problem) : null;
    }
}

/// <summary>Adds a peer (copy-on-write) with the lowest priority.</summary>
public sealed class AddPeerCommandHandler : ICommandHandler<AddPeerCommand, PeerView>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly IConsumerIndex _consumerIndex;
    private readonly ILogger<AddPeerCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="AddPeerCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="consumerIndex">The persisted peer states.</param>
    /// <param name="logger">The logger.</param>
    public AddPeerCommandHandler(ISettingsProvider config, ISettingsStore store, IConsumerIndex consumerIndex, ILogger<AddPeerCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _consumerIndex = consumerIndex;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<PeerView>> Handle(AddPeerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The validator has accepted the fields; this only normalizes them.
        var name = AdminRules.CleanName(request.Name)!;
        var url = AdminRules.NormalizeUrl(request.BaseUrl)!;
        var token = request.Token!.Trim();
        RemotePeer peer;
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            peer = new RemotePeer
            {
                Id = Guid.NewGuid(),
                Name = name,
                BaseUrl = url,
                Token = token,
                Enabled = request.Enabled ?? true,
                Priority = cfg.Peers.Count == 0 ? 0 : cfg.Peers.Max(p => p.Priority) + 1,
            };
            cfg.Peers = new List<RemotePeer>(cfg.Peers) { peer };
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: added peer {PeerId} '{Name}' at {Url}", peer.Id, peer.Name, peer.BaseUrl);
        return Task.FromResult(Result.Success(AdminRules.ToView(peer, _consumerIndex.PeerStates())));
    }
}