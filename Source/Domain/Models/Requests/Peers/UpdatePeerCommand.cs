using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>Updates a peer; an empty token keeps the stored one (<c>PUT admin/peers/{id}</c>).</summary>
/// <param name="Id">The peer id.</param>
/// <param name="Name">The display name.</param>
/// <param name="BaseUrl">The base URL of the remote Jellyfin server.</param>
/// <param name="Token">A new token, or empty to keep the stored one.</param>
/// <param name="Enabled">Whether the peer is used; <c>null</c> keeps the current value.</param>
public sealed record UpdatePeerCommand(Guid Id, string? Name, string? BaseUrl, string? Token, bool? Enabled) : ICommand<PeerView>
{
    /// <inheritdoc />
    public override string ToString() => $"UpdatePeerCommand {{ Id = {Id}, Name = {Name}, BaseUrl = {BaseUrl}, Token = ***, Enabled = {Enabled} }}";
}
