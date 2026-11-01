using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JellyShare.Jellyfin.Adapters;

/// <summary>Turns a Jellyfin request into the plain <see cref="CallerContext"/> the bitrate limits depend on.</summary>
internal sealed class JellyfinCallerResolver
{
    private readonly IAuthorizationContext _auth;
    private readonly ILogger<JellyfinCallerResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinCallerResolver"/> class.</summary>
    /// <param name="auth">The authorization context.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinCallerResolver(IAuthorizationContext auth, ILogger<JellyfinCallerResolver> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    /// <summary>Reads the caller's user limit, device and address from the request.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The caller, or <c>null</c> when its authorization cannot be read (then no caller limits apply).</returns>
    public async Task<CallerContext?> ResolveAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var info = await _auth.GetAuthorizationInfo(context).ConfigureAwait(false);
            return new CallerContext(info.User?.RemoteClientBitrateLimit, info.DeviceId, context.Connection.RemoteIpAddress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "JellyShare: could not read the caller's authorization");
            return null;
        }
    }
}
