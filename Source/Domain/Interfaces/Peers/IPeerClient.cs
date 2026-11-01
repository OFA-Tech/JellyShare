using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Peers;

namespace JellyShare.Domain.Interfaces.Peers;

/// <summary>Talks to a peer's provider endpoints.</summary>
public interface IPeerClient
{
    /// <summary>Probes <c>GET /JellyShare/v1/ping</c> (anonymous, no token is sent).</summary>
    /// <param name="baseUrl">The peer base URL.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PeerPingResult> PingAsync(string baseUrl, CancellationToken ct);

    /// <summary>Fetches the peer's catalog.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="etag">The ETag of the last good catalog, if any.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<PeerFetchResult> FetchCatalogAsync(RemotePeer peer, string? etag, CancellationToken ct);

    /// <summary>Downloads an image of a peer item.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="itemId">The peer item (or series) id.</param>
    /// <param name="type">The image type, e.g. <c>Primary</c>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The image bytes, or <c>null</c> on any failure.</returns>
    Task<Stream?> GetImageAsync(RemotePeer peer, string itemId, string type, CancellationToken ct);

    /// <summary>Opens the peer's stream of an item. The caller disposes the response.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="itemId">The peer item id.</param>
    /// <param name="range">The <c>Range</c> header to forward, if any.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The response, read up to the headers only.</returns>
    Task<HttpResponseMessage> OpenStreamAsync(RemotePeer peer, string itemId, string? range, CancellationToken ct);
}
