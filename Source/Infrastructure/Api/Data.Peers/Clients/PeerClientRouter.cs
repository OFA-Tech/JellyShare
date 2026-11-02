using JellyShare.Domain.Enums;
using JellyShare.Domain.Interfaces.Peers;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Data.Peers.Clients;

/// <summary>Sends each peer request to the client of the peer's <see cref="RemotePeer.Kind"/>; pings go to the token client.</summary>
public sealed class PeerClientRouter : IPeerClient
{
    private readonly IPeerClient _tokens;
    private readonly IPeerClient _accounts;

    /// <summary>Initializes a new instance of the <see cref="PeerClientRouter"/> class.</summary>
    /// <param name="tokens">The client for token peers (JellyShare servers).</param>
    /// <param name="accounts">The client for account peers (plain Jellyfin servers).</param>
    public PeerClientRouter(IPeerClient tokens, IPeerClient accounts)
    {
        _tokens = tokens;
        _accounts = accounts;
    }

    /// <inheritdoc />
    public Task<PeerPingResult> PingAsync(string baseUrl, CancellationToken ct) => _tokens.PingAsync(baseUrl, ct);

    /// <inheritdoc />
    public Task<PeerFetchResult> FetchCatalogAsync(RemotePeer peer, string? etag, CancellationToken ct)
        => For(peer).FetchCatalogAsync(peer, etag, ct);

    /// <inheritdoc />
    public Task<Stream?> GetImageAsync(RemotePeer peer, string itemId, string type, CancellationToken ct)
        => For(peer).GetImageAsync(peer, itemId, type, ct);

    /// <inheritdoc />
    public Task<HttpResponseMessage> OpenStreamAsync(RemotePeer peer, CatalogItem item, string? range, CancellationToken ct)
        => For(peer).OpenStreamAsync(peer, item, range, ct);

    private IPeerClient For(RemotePeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return peer.Kind == PeerKind.Account ? _accounts : _tokens;
    }
}
