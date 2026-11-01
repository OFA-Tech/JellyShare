using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Domain.Interfaces.Sync;

/// <summary>Read access to the consumer's persisted merged index and peer states (written by <c>SyncService</c>).</summary>
public interface IConsumerIndex
{
    /// <summary>Finds a merged title by its canonical key (or any other key of the entry).</summary>
    /// <param name="canonicalKey">The key from the placeholder URL.</param>
    /// <returns>The entry, or <c>null</c> when unknown.</returns>
    MergedEntry? Find(string canonicalKey);

    /// <summary>Gets the peer states by peer id. Empty when the state file is missing or unreadable.</summary>
    /// <returns>The states.</returns>
    IReadOnlyDictionary<Guid, PeerState> PeerStates();
}
