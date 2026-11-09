using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Interfaces.Storage;
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
    private readonly IPeerStateStore _peerStates;
    private readonly IAccountSignIn _accounts;
    private readonly ILogger<UpdatePeerCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdatePeerCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="consumerIndex">The persisted peer states.</param>
    /// <param name="peerStates">The peer state store, to forget the ETag when the accept list changes.</param>
    /// <param name="accounts">Signs in to a friend's server.</param>
    /// <param name="logger">The logger.</param>
    public UpdatePeerCommandHandler(ISettingsProvider config, ISettingsStore store, IConsumerIndex consumerIndex, IPeerStateStore peerStates, IAccountSignIn accounts, ILogger<UpdatePeerCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _consumerIndex = consumerIndex;
        _peerStates = peerStates;
        _accounts = accounts;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<PeerView>> Handle(UpdatePeerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = _config.Current.Peers.FirstOrDefault(p => p.Id == request.Id);
        if (current is not null && request.Kind is { } kind && kind != current.Kind)
        {
            return PeerErrors.KindChange;
        }

        return current?.Kind == PeerKind.Account
            ? await UpdateAccountAsync(request, current, cancellationToken).ConfigureAwait(false)
            : Update(request);
    }

    // The network call happens outside the settings lock; the lock only covers the read-modify-save.
    private async Task<Result<PeerView>> UpdateAccountAsync(UpdatePeerCommand request, RemotePeer current, CancellationToken ct)
    {
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

        if (request.MaxStreams is < AdminRules.MinAccountStreams or > AdminRules.MaxAccountStreams)
        {
            return PeerErrors.InvalidMaxStreams;
        }

        if (AdminRules.ApplyAccept(current.Copy(), request.AcceptAllTypes, request.AcceptTypes) is { } typesProblem)
        {
            return PeerErrors.InvalidTypes(typesProblem);
        }

        var username = string.IsNullOrWhiteSpace(request.Username) ? current.Username : request.Username.Trim();
        var password = request.Password ?? string.Empty;
        string? token = null;
        string? userId = null;
        if (password.Length == 0)
        {
            // The access token only ever goes to the address it was issued for.
            if (!string.Equals(url, AdminRules.NormalizeUrl(current.BaseUrl), StringComparison.Ordinal))
            {
                return PeerErrors.AddressChanged;
            }

            if (!string.Equals(username, current.Username, StringComparison.Ordinal))
            {
                return PeerErrors.MissingPassword;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(username))
            {
                return PeerErrors.MissingUsername;
            }

            var (session, error) = await AccountSignInStep.RunAsync(_accounts, url, username, password, current.Id, ct).ConfigureAwait(false);
            if (session is null)
            {
                return PeerErrors.SignInFailed(error!);
            }

            token = session.AccessToken;
            userId = session.UserId;
        }

        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            var latest = cfg.Peers.FirstOrDefault(p => p.Id == request.Id);
            if (latest is null)
            {
                return PeerErrors.NotFound;
            }

            var peer = latest.Copy();
            peer.Name = name;
            peer.BaseUrl = url;
            peer.Username = username;
            peer.Enabled = request.Enabled ?? latest.Enabled;
            peer.MaxStreams = request.MaxStreams ?? latest.MaxStreams;
            peer.LimitAction = request.LimitAction ?? latest.LimitAction;
            if (AdminRules.ApplyAccept(peer, request.AcceptAllTypes, request.AcceptTypes) is { } problem)
            {
                return PeerErrors.InvalidTypes(problem);
            }

            if (token is not null)
            {
                peer.Token = token;
                peer.AccountUserId = userId!;
            }

            cfg.Peers = cfg.Peers.Select(p => p.Id == request.Id ? peer : p).ToList();
            _store.Save(cfg);
            ForgetEtagIfAcceptChanged(latest, peer);
            _logger.LogInformation("JellyShare admin: updated account peer {PeerId} (enabled={Enabled})", peer.Id, peer.Enabled);
            return Result.Success(AdminRules.ToView(peer, _consumerIndex.PeerStates()));
        }
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
            var peer = current.Copy();
            peer.Name = name;
            peer.BaseUrl = url;
            peer.Token = string.IsNullOrEmpty(newToken) ? current.Token : newToken;
            peer.Enabled = request.Enabled ?? current.Enabled;
            if (AdminRules.ApplyAccept(peer, request.AcceptAllTypes, request.AcceptTypes) is { } typesProblem)
            {
                return PeerErrors.InvalidTypes(typesProblem);
            }

            cfg.Peers = cfg.Peers.Select(p => p.Id == request.Id ? peer : p).ToList();
            _store.Save(cfg);
            ForgetEtagIfAcceptChanged(current, peer);
            _logger.LogInformation("JellyShare admin: updated peer {PeerId} (enabled={Enabled})", peer.Id, peer.Enabled);
            return Result.Success(AdminRules.ToView(peer, _consumerIndex.PeerStates()));
        }
    }

    // The next sync must fetch the catalog in full, so newly accepted types appear. If the state file cannot be read or
    // written this is skipped: the receiver still filters by the accept list at sync time, so nothing unwanted is
    // placed, but a newly accepted type may only appear once the catalog changes (a 0.2.0.0 provider's ETag does not
    // cover the requested types).
    private void ForgetEtagIfAcceptChanged(RemotePeer before, RemotePeer after)
    {
        if (MediaTypes.Accepted(before).SetEquals(MediaTypes.Accepted(after)))
        {
            return;
        }

        try
        {
            var states = _peerStates.Load();
            if (states.TryGetValue(after.Id, out var state) && state.Etag is not null)
            {
                state.Etag = null;
                _peerStates.Save(states);
            }
        }
        catch (Exception ex) when (ex is PeerStateUnreadableException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "JellyShare admin: could not reset the catalog ETag of peer {PeerId}", after.Id);
        }
    }
}
