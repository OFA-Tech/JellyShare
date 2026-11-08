using System.Net;

namespace JellyShare.Jellyfin.Adapters;

/// <summary>A library item that is a JellyShare relay placeholder.</summary>
/// <param name="CanonicalKey">The merged-index key from its <c>.strm</c> URL.</param>
/// <param name="SourceBitrate">The source bitrate in bit/s (stored media info, else the peer's catalog), if known.</param>
/// <param name="TitleKey">The key its stream is counted under (the album for a track); the canonical key when <c>null</c>.</param>
internal sealed record PlaceholderMedia(string CanonicalKey, int? SourceBitrate, string? TitleKey = null)
{
    /// <summary>Gets the key its stream is counted under.</summary>
    public string StreamKey => TitleKey ?? CanonicalKey;
}

/// <summary>Bitrate limits of a caller that Jellyfin applies besides the request values.</summary>
/// <param name="RemoteLimit">
/// The remote-client limit (user policy, else server setting), or <c>null</c> when the caller is in the local network
/// or no limit is set (Jellyfin's <c>MediaInfoHelper.GetMaxBitrate</c>).
/// </param>
/// <param name="StoredProfileBitrate">
/// <c>MaxStreamingBitrate</c> of the device profile Jellyfin stored for the caller's device, which it uses when the
/// request carries no profile (<c>IDeviceManager.GetCapabilities</c>).
/// </param>
internal sealed record CallerLimits(int? RemoteLimit, int? StoredProfileBitrate);

/// <summary>The caller of a request, as far as the bitrate limits depend on it.</summary>
/// <param name="UserRemoteBitrateLimit">The calling user's remote-client bitrate limit, or <c>null</c>/0 when none.</param>
/// <param name="DeviceId">The calling device id, if known.</param>
/// <param name="RemoteAddress">The caller's IP address, if known.</param>
internal sealed record CallerContext(int? UserRemoteBitrateLimit, string? DeviceId, IPAddress? RemoteAddress);
