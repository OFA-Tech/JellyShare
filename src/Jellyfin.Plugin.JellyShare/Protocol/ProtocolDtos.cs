using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyShare.Protocol;

/// <summary>
/// Wire protocol constants shared by the provider and consumer sides.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>The federation protocol version served under <c>/JellyShare/v1</c>.</summary>
    public const string Version = "1.0";
}

/// <summary>
/// Response of the anonymous <c>GET /JellyShare/v1/ping</c> endpoint.
/// </summary>
/// <param name="ProtocolVersion">The protocol version the server speaks.</param>
/// <param name="ServerName">The friendly name of the Jellyfin server.</param>
public sealed record PingResponse(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("serverName")] string ServerName);
