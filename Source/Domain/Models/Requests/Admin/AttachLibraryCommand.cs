using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Admin;

/// <summary>Adds one of the plugin's own placeholder roots to a library and refreshes it (<c>POST admin/libraries/attach</c>).</summary>
/// <param name="LibraryId">The library that receives the placeholder root.</param>
/// <param name="Type"><c>Movies</c> or <c>Shows</c>: which of the plugin's own roots (the path never comes from the request).</param>
public sealed record AttachLibraryCommand(Guid LibraryId, string? Type) : ICommand<PlaceholderRootView>;
