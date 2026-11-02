using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;

namespace JellyShare.Domain.Models.Relay;

/// <summary>The outcome of the stream limit check for a new playback.</summary>
/// <param name="Kind">The decision.</param>
/// <param name="Peer">The peer that is full (null for <see cref="StreamDecisionKind.Play"/>).</param>
/// <param name="StopKey">The title to stop (only for <see cref="StreamDecisionKind.StopOldest"/>).</param>
public sealed record StreamDecision(StreamDecisionKind Kind, RemotePeer? Peer, string? StopKey);
