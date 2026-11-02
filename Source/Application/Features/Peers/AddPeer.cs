using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Peers;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Peers;

/// <summary>A new peer needs a name, a usable URL and a token from the invitation, or a user name and password.</summary>
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

        if (request.Kind == PeerKind.Account)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return PeerErrors.MissingUsername;
            }

            if (string.IsNullOrEmpty(request.Password))
            {
                return PeerErrors.MissingPassword;
            }

            return request.MaxStreams is { } streams && streams is < AdminRules.MinAccountStreams or > AdminRules.MaxAccountStreams
                ? PeerErrors.InvalidMaxStreams
                : null;
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
    private readonly IAccountSignIn _accounts;
    private readonly ILogger<AddPeerCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="AddPeerCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="consumerIndex">The persisted peer states.</param>
    /// <param name="accounts">Signs in to a friend's server.</param>
    /// <param name="logger">The logger.</param>
    public AddPeerCommandHandler(ISettingsProvider config, ISettingsStore store, IConsumerIndex consumerIndex, IAccountSignIn accounts, ILogger<AddPeerCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _consumerIndex = consumerIndex;
        _accounts = accounts;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<PeerView>> Handle(AddPeerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The validator has accepted the fields; this only normalizes them.
        var name = AdminRules.CleanName(request.Name)!;
        var url = AdminRules.NormalizeUrl(request.BaseUrl)!;
        var id = Guid.NewGuid();
        var account = request.Kind == PeerKind.Account;
        var token = account ? string.Empty : request.Token!.Trim();
        var peer = new RemotePeer { Id = id, Name = name, BaseUrl = url, Token = token, Enabled = request.Enabled ?? true };
        if (account)
        {
            // The password is only used for this call; the peer keeps the access token.
            var (session, error) = await AccountSignInStep
                .RunAsync(_accounts, url, request.Username!.Trim(), request.Password!, id, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                return PeerErrors.SignInFailed(error!);
            }

            peer.Kind = PeerKind.Account;
            peer.Username = request.Username.Trim();
            peer.AccountUserId = session.UserId!;
            peer.Token = session.AccessToken!;
            peer.MaxStreams = request.MaxStreams ?? 1;
            peer.LimitAction = request.LimitAction ?? StreamLimitAction.Refuse;
        }

        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            peer.Priority = cfg.Peers.Count == 0 ? 0 : cfg.Peers.Max(p => p.Priority) + 1;
            cfg.Peers = new List<RemotePeer>(cfg.Peers) { peer };
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: added peer {PeerId} '{Name}' at {Url}", peer.Id, peer.Name, peer.BaseUrl);
        return Result.Success(AdminRules.ToView(peer, _consumerIndex.PeerStates()));
    }
}
