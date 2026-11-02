using JellyShare.Domain.Models.Sync;

namespace JellyShare.Domain.Interfaces.Storage;

/// <summary>Persists the per-peer sync state (<c>peer-state.json</c>).</summary>
public interface IPeerStateStore
{
    /// <summary>
    /// Loads the states. Only a missing file means "no state"; a file that exists but cannot be read throws
    /// <see cref="Exceptions.PeerStateUnreadableException"/> (treating it as empty would drop every failing peer's last
    /// good catalog).
    /// </summary>
    /// <returns>Peer id to state.</returns>
    Dictionary<Guid, PeerState> Load();

    /// <summary>Saves the states atomically.</summary>
    /// <param name="states">Peer id to state.</param>
    void Save(IReadOnlyDictionary<Guid, PeerState> states);
}
