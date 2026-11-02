using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>Adds a peer this server receives from (<c>POST admin/peers</c>).</summary>
/// <param name="Name">The display name.</param>
/// <param name="BaseUrl">The base URL of the remote Jellyfin server.</param>
/// <param name="Token">The token the remote server gave us (token peers).</param>
/// <param name="Enabled">Whether the peer is used; <c>null</c> means enabled.</param>
/// <param name="Kind">How to connect; <c>null</c> means <see cref="PeerKind.Token"/>.</param>
/// <param name="Username">The user name (account peers).</param>
/// <param name="Password">The password (account peers); used once to sign in and never stored.</param>
/// <param name="MaxStreams">The concurrent playback limit (account peers); <c>null</c> means 1.</param>
/// <param name="LimitAction">What happens at the limit (account peers); <c>null</c> means refuse.</param>
public sealed record AddPeerCommand(
    string? Name,
    string? BaseUrl,
    string? Token,
    bool? Enabled,
    PeerKind? Kind = null,
    string? Username = null,
    string? Password = null,
    int? MaxStreams = null,
    StreamLimitAction? LimitAction = null) : ICommand<PeerView>
{
    /// <inheritdoc />
    public override string ToString()
        => $"AddPeerCommand {{ Name = {Name}, BaseUrl = {BaseUrl}, Token = ***, Enabled = {Enabled}, Kind = {Kind}, Username = {Username}, Secret = ***, MaxStreams = {MaxStreams}, LimitAction = {LimitAction} }}";
}
