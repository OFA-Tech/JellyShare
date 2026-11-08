using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Shares;

/// <summary>Creates a share and returns its token once (<c>POST admin/shares</c>).</summary>
/// <param name="Name">The display name of the peer the invitation is for.</param>
/// <param name="LibraryIds">Ignored: shares are by media type now.</param>
/// <param name="MaxStreams">The concurrent stream limit.</param>
/// <param name="MaxBitrate">The bitrate cap in bits per second, or <c>null</c> for no cap.</param>
/// <param name="UseDefaultTypes"><c>false</c> to offer <paramref name="Types"/>; otherwise the share follows the default sharing.</param>
/// <param name="Types">The media types to offer when <paramref name="UseDefaultTypes"/> is <c>false</c>.</param>
public sealed record CreateShareCommand(
    string? Name,
    IReadOnlyList<Guid>? LibraryIds,
    int? MaxStreams,
    int? MaxBitrate,
    bool? UseDefaultTypes = null,
    IReadOnlyList<MediaType>? Types = null) : ICommand<CreatedShareResponse>;
