using JellyShare.Application.Features.Admin;
using JellyShare.Application.Features.Catalog;
using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Shares;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Shares;

/// <summary>Validates a new share before any token is created.</summary>
public sealed class CreateShareCommandValidator : IValidator<CreateShareCommand>
{
    private readonly IAdminLibraries _libraries;

    /// <summary>Initializes a new instance of the <see cref="CreateShareCommandValidator"/> class.</summary>
    /// <param name="libraries">The libraries on this server.</param>
    public CreateShareCommandValidator(IAdminLibraries libraries)
    {
        _libraries = libraries;
    }

    /// <inheritdoc />
    public Error? Validate(CreateShareCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problem = AdminRules.ValidateShare(
            request.Name,
            request.LibraryIds,
            request.MaxStreams,
            request.MaxBitrate,
            () => _libraries.List().Select(l => l.Id),
            out _,
            out _);
        return problem is null ? null : Error.Validation("Share.Invalid", problem);
    }
}

/// <summary>Creates a share (copy-on-write) and returns its plaintext token once; only the hash is stored.</summary>
public sealed class CreateShareCommandHandler : ICommandHandler<CreateShareCommand, CreatedShareResponse>
{
    private readonly ISettingsProvider _config;
    private readonly ISettingsStore _store;
    private readonly ShareTokenService _tokens;
    private readonly ILogger<CreateShareCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="CreateShareCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="store">Persists settings changes.</param>
    /// <param name="tokens">Creates the token and its hash.</param>
    /// <param name="logger">The logger.</param>
    public CreateShareCommandHandler(ISettingsProvider config, ISettingsStore store, ShareTokenService tokens, ILogger<CreateShareCommandHandler> logger)
    {
        _config = config;
        _store = store;
        _tokens = tokens;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<CreatedShareResponse>> Handle(CreateShareCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The validator has accepted the fields; this only normalizes them (trimmed name, distinct libraries).
        var name = AdminRules.CleanName(request.Name)!;
        var libs = (request.LibraryIds ?? []).Distinct().ToList();
        var (token, hash) = _tokens.Create();
        var share = new PeerShare
        {
            Id = Guid.NewGuid(),
            Name = name,
            TokenHash = hash,
            Enabled = true,
            LibraryIds = libs,
            MaxStreams = request.MaxStreams!.Value,
            MaxBitrate = request.MaxBitrate,
        };

        lock (AdminRules.SettingsLock)
        {
            var cfg = _config.Current;
            cfg.Shares = new List<PeerShare>(cfg.Shares) { share };
            _store.Save(cfg);
        }

        _logger.LogInformation("JellyShare admin: created share {ShareId} '{Name}'", share.Id, share.Name);
        return Task.FromResult(Result.Success(new CreatedShareResponse(share.Id, share.Name, token)));
    }
}