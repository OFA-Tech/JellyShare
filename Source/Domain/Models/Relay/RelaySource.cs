using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Models.Relay;

/// <summary>A peer item the relay can stream from.</summary>
/// <param name="Peer">The configured peer.</param>
/// <param name="Item">The peer's catalog item.</param>
public sealed record RelaySource(RemotePeer Peer, CatalogItem Item);