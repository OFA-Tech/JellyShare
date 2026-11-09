using JellyShare.Application.Features.Admin;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Requests.Shares;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Shares;

/// <summary>Rejects a default sharing that is neither "all" nor at least one valid media type.</summary>
public sealed class UpdateDefaultSharingCommandValidator : IValidator<UpdateDefaultSharingCommand>
{
    /// <inheritdoc />
    public Error? Validate(UpdateDefaultSharingCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.All == true)
        {
            return null;
        }

        var problem = AdminRules.ValidateTypes(request.Types, out _);
        return problem is null ? null : Error.Validation("Share.Invalid", problem);
    }
}

/// <summary>Sets the media types new and "default" shares offer; <c>All</c> is stored as <c>null</c>.</summary>
public sealed class UpdateDefaultSharingCommandHandler : ICommandHandler<UpdateDefaultSharingCommand, DefaultSharingView>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly ILogger<UpdateDefaultSharingCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateDefaultSharingCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="logger">The logger.</param>
    public UpdateDefaultSharingCommandHandler(ISettingsProvider config, ISettingsStore store, ILogger<UpdateDefaultSharingCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<DefaultSharingView>> Handle(UpdateDefaultSharingCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            cfg.DefaultShareTypes = request.All == true ? null : request.Types!.Distinct().ToArray();
            _store.Save(cfg);
            _logger.LogInformation("JellyShare admin: default sharing is now {Types}", string.Join(',', AdminRules.DefaultSharing(cfg).Types));
            return Task.FromResult(Result.Success(AdminRules.DefaultSharing(cfg)));
        }
    }
}
