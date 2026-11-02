using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>Adds a peer this server receives from (<c>POST admin/peers</c>).</summary>
/// <param name="Name">The display name.</param>
/// <param name="BaseUrl">The base URL of the remote Jellyfin server.</param>
/// <param name="Token">The token the remote server gave us.</param>
/// <param name="Enabled">Whether the peer is used; <c>null</c> means enabled.</param>
public sealed record AddPeerCommand(string? Name, string? BaseUrl, string? Token, bool? Enabled) : ICommand<PeerView>
{
    /// <inheritdoc />
    public override string ToString() => $"AddPeerCommand {{ Name = {Name}, BaseUrl = {BaseUrl}, Token = ***, Enabled = {Enabled} }}";
}
