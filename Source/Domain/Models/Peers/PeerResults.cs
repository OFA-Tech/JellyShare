using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Models.Peers;

/// <summary>Result of a catalog fetch.</summary>
/// <param name="Ok">The catalog was fetched and is compatible.</param>
/// <param name="NotModified">The peer answered 304 for the sent ETag.</param>
/// <param name="Catalog">The catalog when <paramref name="Ok"/>.</param>
/// <param name="Error">A short error that never contains the token.</param>
public sealed record PeerFetchResult(bool Ok, bool NotModified, CatalogResponse? Catalog, string? Error);

/// <summary>Result of the anonymous ping.</summary>
/// <param name="Ok">The server is a compatible JellyShare server.</param>
/// <param name="ServerName">The server name when it answered like a JellyShare server.</param>
/// <param name="ProtocolVersion">The protocol version when it answered like a JellyShare server.</param>
/// <param name="Failure">Why the ping failed.</param>
/// <param name="Detail">A short detail (HTTP status, connection error); never contains a token.</param>
public sealed record PeerPingResult(bool Ok, string? ServerName, string? ProtocolVersion, PingFailure Failure, string? Detail);
