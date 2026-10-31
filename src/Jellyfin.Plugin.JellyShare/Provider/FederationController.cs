using Jellyfin.Plugin.JellyShare.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>
/// Provider-side federation API consumed by peer servers.
/// </summary>
[ApiController]
[Route("JellyShare/v1")]
public class FederationController : ControllerBase
{
    private readonly IServerIdentity _identity;

    /// <summary>Initializes a new instance of the <see cref="FederationController"/> class.</summary>
    /// <param name="identity">The local server identity.</param>
    public FederationController(IServerIdentity identity)
    {
        _identity = identity;
    }

    /// <summary>Anonymous liveness and protocol-version probe.</summary>
    /// <returns>The protocol version and server name.</returns>
    [HttpGet("ping")]
    [AllowAnonymous]
    [Produces("application/json")]
    public ActionResult<PingResponse> Ping()
    {
        return new PingResponse(ProtocolInfo.Version, _identity.ServerName);
    }
}
