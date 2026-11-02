using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Models.Protocol;

namespace JellyShare.Domain.Models.Requests.Federation;

/// <summary>Anonymous liveness and protocol-version probe (<c>GET ping</c>).</summary>
public sealed record PingQuery : IQuery<PingResponse>;
