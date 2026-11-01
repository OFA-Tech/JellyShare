using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Keys;
using JellyShare.Jellyfin.Protocol;

namespace JellyShare.Jellyfin.Consumer;

/// <summary>A peer item the relay can stream from.</summary>
/// <param name="Peer">The configured peer.</param>
/// <param name="Item">The peer's catalog item.</param>
public sealed record RelaySource(RemotePeer Peer, CatalogItem Item);

/// <summary>Orders the peers of a merged title for playback.</summary>
public static class SourceSelector
{
    /// <summary>Orders the sources using the persisted peer states.</summary>
    /// <param name="entry">The merged title.</param>
    /// <param name="peers">The configured peers.</param>
    /// <param name="state">The peer state store.</param>
    /// <returns>The usable sources, preferred first.</returns>
    /// <exception cref="PeerStateUnreadableException">The state file exists but cannot be read.</exception>
    public static IReadOnlyList<RelaySource> Order(MergedEntry entry, IReadOnlyList<RemotePeer> peers, PeerStateStore state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Order(entry, peers, state.Load());
    }

    /// <summary>
    /// Orders the sources: only enabled, configured peers. Peers whose last sync failed
    /// (<see cref="PeerState.FailingSinceUtc"/> set) are kept but tried after the healthy ones, since a short outage
    /// may already be over. Within each group by <see cref="RemotePeer.Priority"/> ascending, then peer name
    /// (ordinal, ignoring case), then peer id.
    /// </summary>
    /// <param name="entry">The merged title.</param>
    /// <param name="peers">The configured peers.</param>
    /// <param name="states">The peer states by peer id.</param>
    /// <returns>The usable sources, preferred first.</returns>
    public static IReadOnlyList<RelaySource> Order(MergedEntry entry, IReadOnlyList<RemotePeer> peers, IReadOnlyDictionary<Guid, PeerState> states)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(states);

        var byId = new Dictionary<Guid, RemotePeer>();
        foreach (var peer in peers)
        {
            if (peer is not null)
            {
                byId.TryAdd(peer.Id, peer);
            }
        }

        return entry.Sources
            .Where(s => s.Item is not null)
            .Select(s => byId.TryGetValue(s.PeerId, out var peer) ? new RelaySource(peer, s.Item) : null)
            .OfType<RelaySource>()
            .Where(s => s.Peer.Enabled)
            .OrderBy(s => IsFailing(states, s.Peer.Id))
            .ThenBy(s => s.Peer.Priority)
            .ThenBy(s => s.Peer.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Peer.Id)
            .ToList();
    }

    private static bool IsFailing(IReadOnlyDictionary<Guid, PeerState> states, Guid peerId)
        => states.TryGetValue(peerId, out var state) && state?.FailingSinceUtc is not null;
}
