using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Shares;

/// <summary>Creates a share and returns its token once (<c>POST admin/shares</c>).</summary>
/// <param name="Name">The display name of the peer the invitation is for.</param>
/// <param name="LibraryIds">The libraries the peer may see.</param>
/// <param name="MaxStreams">The concurrent stream limit.</param>
/// <param name="MaxBitrate">The bitrate cap in bits per second, or <c>null</c> for no cap.</param>
public sealed record CreateShareCommand(string? Name, IReadOnlyList<Guid>? LibraryIds, int? MaxStreams, int? MaxBitrate) : ICommand<CreatedShareResponse>;
