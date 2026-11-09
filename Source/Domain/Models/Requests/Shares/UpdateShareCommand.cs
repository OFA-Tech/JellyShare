using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Shares;

/// <summary>Replaces a share's name, media types, limits and enabled flag; the token is untouched (<c>PUT admin/shares/{id}</c>).</summary>
/// <param name="Id">The share id.</param>
/// <param name="Name">The display name.</param>
/// <param name="LibraryIds">Ignored: shares are by media type now.</param>
/// <param name="MaxStreams">The concurrent stream limit.</param>
/// <param name="MaxBitrate">The bitrate cap in bits per second, or <c>null</c> for no cap.</param>
/// <param name="Enabled">Whether the share is active; <c>null</c> keeps the current value.</param>
/// <param name="UseDefaultTypes"><c>true</c> for the default sharing, <c>false</c> for <paramref name="Types"/>; with neither set the share keeps its media types.</param>
/// <param name="Types">The media types to offer; without <paramref name="UseDefaultTypes"/> they make the share custom.</param>
public sealed record UpdateShareCommand(
    Guid Id,
    string? Name,
    IReadOnlyList<Guid>? LibraryIds,
    int? MaxStreams,
    int? MaxBitrate,
    bool? Enabled,
    bool? UseDefaultTypes = null,
    IReadOnlyList<MediaType>? Types = null) : ICommand<ShareView>;
