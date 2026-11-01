using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Interfaces.Sync;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Admin;
using JellyShare.Domain.Results;

namespace JellyShare.Application.Features.Admin;

/// <summary>Builds the admin page status: shares with active streams, peers in priority order, sync state, roots, libraries.</summary>
public sealed class GetStatusQueryHandler : IQueryHandler<GetStatusQuery, StatusResponse>
{
    private readonly ISettingsProvider _config;
    private readonly IAdminLibraries _libraries;
    private readonly IConsumerIndex _consumerIndex;
    private readonly StreamLimiter _limiter;
    private readonly ISyncRunner _sync;
    private readonly IServerIdentity _identity;

    /// <summary>Initializes a new instance of the <see cref="GetStatusQueryHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="libraries">The library operations.</param>
    /// <param name="consumerIndex">The persisted peer states.</param>
    /// <param name="limiter">The stream limiter (active streams).</param>
    /// <param name="sync">The sync runner.</param>
    /// <param name="identity">The local server identity.</param>
    public GetStatusQueryHandler(
        ISettingsProvider config,
        IAdminLibraries libraries,
        IConsumerIndex consumerIndex,
        StreamLimiter limiter,
        ISyncRunner sync,
        IServerIdentity identity)
    {
        _config = config;
        _libraries = libraries;
        _consumerIndex = consumerIndex;
        _limiter = limiter;
        _sync = sync;
        _identity = identity;
    }

    /// <inheritdoc />
    public Task<Result<StatusResponse>> Handle(GetStatusQuery request, CancellationToken cancellationToken)
    {
        var cfg = _config.Current;
        var libraries = _libraries.List();
        var states = _consumerIndex.PeerStates();

        var shares = cfg.Shares.Select(s => AdminRules.ToView(s, _limiter.Active(s.Id))).ToList();
        var peers = AdminRules.ByPriority(cfg.Peers).Select(p => AdminRules.ToView(p, states)).ToList();

        return Task.FromResult(Result.Success(new StatusResponse(
            ProtocolInfo.Version,
            _identity.ServerName,
            shares,
            peers,
            _sync.LastReport,
            AdminRules.RootView(cfg.MoviesPlaceholderPath, libraries),
            AdminRules.RootView(cfg.ShowsPlaceholderPath, libraries),
            libraries,
            cfg.SyncIntervalHours)
        {
            SyncRunning = _sync.IsRunning,
            SyncError = _sync.LastError,
        }));
    }
}