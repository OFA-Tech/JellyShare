using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Admin;

namespace JellyShare.Domain.Models.Requests.Peers;

/// <summary>
/// Tests a peer URL and token: ping, then catalog fetch (<c>POST admin/peers/test</c>). Always succeeds: problems are in
/// the response and never contain the token.
/// </summary>
/// <param name="Url">The base URL to test.</param>
/// <param name="Token">The token to test with.</param>
/// <param name="PeerId">A saved peer whose stored token is used when <paramref name="Token"/> is empty.</param>
public sealed record TestPeerQuery(string? Url, string? Token, Guid? PeerId) : IQuery<TestPeerResponse>
{
    /// <inheritdoc />
    public override string ToString() => $"TestPeerQuery {{ Url = {Url}, Token = ***, PeerId = {PeerId} }}";
}
