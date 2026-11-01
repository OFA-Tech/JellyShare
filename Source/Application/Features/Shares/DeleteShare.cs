using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Requests.Shares;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Shares;

/// <summary>Revokes a share immediately: its token stops working with the next request.</summary>
public sealed class DeleteShareCommandHandler : ICommandHandler<DeleteShareCommand>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly ILogger<DeleteShareCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="DeleteShareCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="logger">The logger.</param>
    public DeleteShareCommandHandler(ISettingsProvider config, ISettingsStore store, ILogger<DeleteShareCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> Handle(DeleteShareCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            if (cfg.Shares.All(s => s.Id != request.Id))
            {
                return Task.FromResult(Result.Failure(ShareErrors.NotFound));
            }

            cfg.Shares = cfg.Shares.Where(s => s.Id != request.Id).ToList();
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: revoked share {ShareId}", request.Id);
        return Task.FromResult(Result.Success());
    }
}