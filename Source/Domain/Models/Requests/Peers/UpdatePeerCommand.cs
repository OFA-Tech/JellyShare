using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>Updates a peer; an empty token or password keeps the stored credentials (<c>PUT admin/peers/{id}</c>).</summary>
/// <param name="Id">The peer id.</param>
/// <param name="Name">The display name.</param>
/// <param name="BaseUrl">The base URL of the remote Jellyfin server.</param>
/// <param name="Token">A new token, or empty to keep the stored one.</param>
/// <param name="Enabled">Whether the peer is used; <c>null</c> keeps the current value.</param>
/// <param name="Kind">The peer kind; it cannot change.</param>
/// <param name="Username">The user name (account peers); <c>null</c> keeps the stored one.</param>
/// <param name="Password">A password that signs in again (account peers), or empty to keep the session.</param>
/// <param name="MaxStreams">The concurrent playback limit (account peers); <c>null</c> keeps it.</param>
/// <param name="LimitAction">What happens at the limit (account peers); <c>null</c> keeps it.</param>
/// <param name="AcceptAllTypes">Accept every media type from the peer; <c>null</c> keeps the current setting.</param>
/// <param name="AcceptTypes">The media types to accept when <paramref name="AcceptAllTypes"/> is <c>false</c> (at least one); <c>null</c> keeps the current list.</param>
public sealed record UpdatePeerCommand(
    Guid Id,
    string? Name,
    string? BaseUrl,
    string? Token,
    bool? Enabled,
    PeerKind? Kind = null,
    string? Username = null,
    string? Password = null,
    int? MaxStreams = null,
    StreamLimitAction? LimitAction = null,
    bool? AcceptAllTypes = null,
    List<MediaType>? AcceptTypes = null) : ICommand<PeerView>
{
    /// <inheritdoc />
    public override string ToString()
        => $"UpdatePeerCommand {{ Id = {Id}, Name = {Name}, BaseUrl = {BaseUrl}, Token = ***, Enabled = {Enabled}, Kind = {Kind}, Username = {Username}, Secret = ***, MaxStreams = {MaxStreams}, LimitAction = {LimitAction}, AcceptAllTypes = {AcceptAllTypes} }}";
}
