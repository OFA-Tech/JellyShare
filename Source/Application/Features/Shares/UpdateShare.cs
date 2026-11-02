using JellyShare.Application.Features.Admin;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Shares;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Shares;

/// <summary>
/// Replaces a share's name, libraries, limits and enabled flag, keeping its token. A missing share is reported before
/// the fields are validated, so this has no validator.
/// </summary>
public sealed class UpdateShareCommandHandler : ICommandHandler<UpdateShareCommand, ShareView>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly IAdminLibraries _libraries;
    private readonly StreamLimiter _limiter;
    private readonly ILogger<UpdateShareCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateShareCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="libraries">The libraries on this server.</param>
    /// <param name="limiter">The stream limiter (active streams).</param>
    /// <param name="logger">The logger.</param>
    public UpdateShareCommandHandler(
        ISettingsProvider config,
        ISettingsStore store,
        IAdminLibraries libraries,
        StreamLimiter limiter,
        ILogger<UpdateShareCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _libraries = libraries;
        _limiter = limiter;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<ShareView>> Handle(UpdateShareCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Update(request));
    }

    private Result<ShareView> Update(UpdateShareCommand request)
    {
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            var current = cfg.Shares.FirstOrDefault(s => s.Id == request.Id);
            if (current is null)
            {
                return ShareErrors.NotFound;
            }

            var problem = AdminRules.ValidateShare(
                request.Name,
                request.LibraryIds,
                request.MaxStreams,
                request.MaxBitrate,
                () => _libraries.List().Select(l => l.Id),
                out var name,
                out var libs);
            if (problem is not null)
            {
                return Error.Validation("Share.Invalid", problem);
            }

            // Copy-on-write: a request that is reading the old share (catalog, stream limits) never sees a half-updated one.
            var share = new PeerShare
            {
                Id = current.Id,
                Name = name,
                TokenHash = current.TokenHash,
                Enabled = request.Enabled ?? current.Enabled,
                LibraryIds = libs,
                MaxStreams = request.MaxStreams!.Value,
                MaxBitrate = request.MaxBitrate,
            };
            cfg.Shares = cfg.Shares.Select(s => s.Id == request.Id ? share : s).ToList();
            _store.Save(cfg);
            _logger.LogInformation("JellyShare admin: updated share {ShareId} (enabled={Enabled})", share.Id, share.Enabled);
            return Result.Success(AdminRules.ToView(share, _limiter.Active(share.Id)));
        }
    }
}
