using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Shares;

/// <summary>Replaces a share's name, libraries, limits and enabled flag; the token is untouched (<c>PUT admin/shares/{id}</c>).</summary>
/// <param name="Id">The share id.</param>
/// <param name="Name">The display name.</param>
/// <param name="LibraryIds">The libraries the peer may see.</param>
/// <param name="MaxStreams">The concurrent stream limit.</param>
/// <param name="MaxBitrate">The bitrate cap in bits per second, or <c>null</c> for no cap.</param>
/// <param name="Enabled">Whether the share is active; <c>null</c> keeps the current value.</param>
public sealed record UpdateShareCommand(Guid Id, string? Name, IReadOnlyList<Guid>? LibraryIds, int? MaxStreams, int? MaxBitrate, bool? Enabled) : ICommand<ShareView>;
