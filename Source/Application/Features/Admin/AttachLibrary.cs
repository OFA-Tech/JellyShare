using JellyShare.Domain.Interfaces.Configuration;
using JellyShare.Domain.Interfaces.Library;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Placeholders;
using JellyShare.Domain.Models.Admin;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Requests.Admin;
using JellyShare.Domain.Results;
using Microsoft.Extensions.Logging;

namespace JellyShare.Application.Features.Admin;

/// <summary>Only <c>Movies</c>, <c>Shows</c>, <c>Music</c> and <c>Audiobooks</c> can be attached (checked before anything is read).</summary>
public sealed class AttachLibraryCommandValidator : IValidator<AttachLibraryCommand>
{
    /// <inheritdoc />
    public Error? Validate(AttachLibraryCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AttachLibraryCommandHandler.TypeOf(request.Type) is not null
            ? null
            : Error.Validation("Library.InvalidType", "Type must be Movies, Shows, Music or Audiobooks.");
    }
}

/// <summary>
/// Adds one of the plugin's own placeholder roots to a library of the right type and refreshes it. The path never comes
/// from the request; the marker is only created in a new or empty folder.
/// </summary>
public sealed class AttachLibraryCommandHandler : ICommandHandler<AttachLibraryCommand, PlaceholderRootView>
{
    private readonly ISettingsProvider _config;
    private readonly IAdminLibraries _libraries;
    private readonly IPlaceholderFiles _placeholders;
    private readonly ILogger<AttachLibraryCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="AttachLibraryCommandHandler"/> class.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="libraries">The library operations.</param>
    /// <param name="placeholders">The placeholder roots on disk.</param>
    /// <param name="logger">The logger.</param>
    public AttachLibraryCommandHandler(
        ISettingsProvider config,
        IAdminLibraries libraries,
        IPlaceholderFiles placeholders,
        ILogger<AttachLibraryCommandHandler> logger)
    {
        _config = config;
        _libraries = libraries;
        _placeholders = placeholders;
        _logger = logger;
    }

    /// <summary>Gets the attachable type named by the request, its library collection type and its placeholder root.</summary>
    internal static (string Name, string CollectionType, Func<JellyShareSettings, string> Root)? TypeOf(string? type) => type?.ToUpperInvariant() switch
    {
        "MOVIES" => ("Movies", "movies", c => c.MoviesPlaceholderPath),
        "SHOWS" => ("Shows", "tvshows", c => c.ShowsPlaceholderPath),
        "MUSIC" => ("Music", "music", c => c.MusicPlaceholderPath),
        "AUDIOBOOKS" => ("Audiobooks", "books", c => c.AudiobooksPlaceholderPath),
        _ => null,
    };

    /// <inheritdoc />
    public async Task<Result<PlaceholderRootView>> Handle(AttachLibraryCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = TypeOf(request.Type) ?? throw new ArgumentException("Unknown type.", nameof(request));
        var root = type.Root(_config.Current);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Error.Validation("Library.RootNotConfigured", "The placeholder folder is not configured.");
        }

        var libraries = _libraries.List();
        var library = libraries.FirstOrDefault(l => l.Id == request.LibraryId);
        if (library is null)
        {
            return Error.NotFound("Library.NotFound", "Library not found.");
        }

        if (!string.Equals(library.CollectionType, type.CollectionType, StringComparison.OrdinalIgnoreCase))
        {
            var article = type.Name.StartsWith('A') ? "an" : "a";
            return Error.Validation("Library.WrongType", $"'{library.Name}' is not {article} {type.Name} library.");
        }

        var holder = libraries.FirstOrDefault(l => l.Locations.Any(loc => AdminRules.SamePath(loc, root)));
        if (holder is not null)
        {
            return holder.Id == library.Id
                ? Result.Success(AdminRules.RootView(root, libraries))
                : Error.Conflict("Library.RootInUse", $"This folder is already part of the library '{holder.Name}'. Remove it there first.");
        }

        if (_placeholders.RootProblem(root) is { } problem)
        {
            return Error.Conflict("Library.RootUnusable", problem);
        }

        try
        {
            // The marker is only created in a new or empty folder (RootProblem above refuses anything else).
            _placeholders.EnsureRoot(root);
            await _libraries.AddPathAsync(library.Name, root, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "JellyShare admin: could not attach {Root} to {Library}", root, library.Name);
            return Error.Unexpected("Library.AttachFailed", $"Could not add the placeholder folder to '{library.Name}': {ex.Message}");
        }

        _logger.LogInformation("JellyShare admin: added {Root} to library '{Library}' and started a refresh", root, library.Name);
        return Result.Success(AdminRules.RootView(root, _libraries.List()));
    }
}
