using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Relay;

namespace JellyShare.Application.Features.Relay;

/// <summary>
/// Jellyfin cannot limit the concurrent streams of one login, so account peers are limited here by counting the
/// titles <see cref="RelaySessions"/> sees playing on them. Token peers enforce their own limit.
/// </summary>
public static class StreamLimitGate
{
    /// <summary>Tells whether a peer can serve a title now.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="key">The canonical key of the title.</param>
    /// <param name="sessions">The active playbacks.</param>
    /// <returns><c>true</c> for token peers, for the title already playing on the peer (seeks, range requests), or when a slot is free.</returns>
    public static bool HasRoom(RemotePeer peer, string key, RelaySessions sessions)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(sessions);
        return peer.Kind != PeerKind.Account
            || sessions.ActivePeer(key) == peer.Id
            || sessions.ActiveTitles(peer.Id).Count < peer.MaxStreams;
    }

    /// <summary>
    /// Claims a slot for a title on a peer, atomically with the limit check. Token peers need no claim.
    /// </summary>
    /// <param name="peer">The peer.</param>
    /// <param name="key">The canonical key of the title.</param>
    /// <param name="sessions">The active playbacks.</param>
    /// <param name="created"><c>true</c> when this call took the slot; only then may the caller undo it (<see cref="RelaySessions.CancelReservation"/>).</param>
    /// <returns><c>true</c> when the peer may serve the title now.</returns>
    public static bool TryClaim(RemotePeer peer, string key, RelaySessions sessions, out bool created)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(sessions);
        created = false;
        return peer.Kind != PeerKind.Account || sessions.TryReserve(key, peer.Id, peer.MaxStreams, out created);
    }

    /// <summary>Decides what a new playback of a title needs, given the sources in play order.</summary>
    /// <param name="ordered">The sources, preferred first.</param>
    /// <param name="key">The canonical key of the title.</param>
    /// <param name="sessions">The active playbacks.</param>
    /// <returns>
    /// <see cref="StreamDecisionKind.Play"/> when a source has room (or there are no sources); otherwise
    /// <see cref="StreamDecisionKind.StopOldest"/> for the first source that allows it, else <see cref="StreamDecisionKind.Refuse"/>.
    /// </returns>
    public static StreamDecision Decide(IReadOnlyList<RelaySource> ordered, string key, RelaySessions sessions)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (ordered.Count == 0 || ordered.Any(s => HasRoom(s.Peer, key, sessions)))
        {
            return new StreamDecision(StreamDecisionKind.Play, null, null);
        }

        // A peer with MaxStreams <= 0 is full with nothing playing: there is nothing to stop.
        foreach (var peer in ordered.Select(s => s.Peer).Where(p => p.LimitAction == StreamLimitAction.StopOldest))
        {
            var titles = sessions.ActiveTitles(peer.Id);
            if (titles.Count > 0)
            {
                return new StreamDecision(StreamDecisionKind.StopOldest, peer, titles[0]);
            }
        }

        return new StreamDecision(StreamDecisionKind.Refuse, ordered[0].Peer, null);
    }
}
