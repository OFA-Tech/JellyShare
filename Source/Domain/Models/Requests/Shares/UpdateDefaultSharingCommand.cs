using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Shares;

/// <summary>Sets the media types a share offers by default (<c>PUT admin/sharing/default</c>).</summary>
/// <param name="All"><c>true</c> to share every media type by default.</param>
/// <param name="Types">The media types shared by default when <paramref name="All"/> is not <c>true</c>.</param>
public sealed record UpdateDefaultSharingCommand(bool? All, IReadOnlyList<MediaType>? Types) : ICommand<DefaultSharingView>;
