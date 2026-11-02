using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Interfaces.Platform;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Requests.Federation;
using JellyShare.Domain.Results;

namespace JellyShare.Application.Features.Federation;

/// <summary>Answers the anonymous ping with the protocol version and the server name.</summary>
public sealed class PingQueryHandler : IQueryHandler<PingQuery, PingResponse>
{
    private readonly IServerIdentity _identity;

    /// <summary>Initializes a new instance of the <see cref="PingQueryHandler"/> class.</summary>
    /// <param name="identity">The local server identity.</param>
    public PingQueryHandler(IServerIdentity identity)
    {
        _identity = identity;
    }

    /// <inheritdoc />
    public Task<Result<PingResponse>> Handle(PingQuery request, CancellationToken cancellationToken)
        => Task.FromResult(Result.Success(new PingResponse(ProtocolInfo.Version, _identity.ServerName)));
}
