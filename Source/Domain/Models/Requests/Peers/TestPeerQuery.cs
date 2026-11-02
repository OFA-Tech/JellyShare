using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>
/// Tests a peer URL and token or account (<c>POST admin/peers/test</c>). Always succeeds: problems are in the response
/// and never contain the token or the password.
/// </summary>
/// <param name="Url">The base URL to test.</param>
/// <param name="Token">The token to test with.</param>
/// <param name="PeerId">A saved peer whose stored credentials are used when the token and the password are empty.</param>
/// <param name="Kind">How to connect; <c>null</c> means the saved peer's kind, or <see cref="PeerKind.Token"/>.</param>
/// <param name="Username">The user name (account peers).</param>
/// <param name="Password">The password (account peers); used once and never stored.</param>
public sealed record TestPeerQuery(
    string? Url,
    string? Token,
    Guid? PeerId,
    PeerKind? Kind = null,
    string? Username = null,
    string? Password = null) : IQuery<TestPeerResponse>
{
    /// <inheritdoc />
    public override string ToString()
        => $"TestPeerQuery {{ Url = {Url}, Token = ***, PeerId = {PeerId}, Kind = {Kind}, Username = {Username}, Secret = *** }}";
}
