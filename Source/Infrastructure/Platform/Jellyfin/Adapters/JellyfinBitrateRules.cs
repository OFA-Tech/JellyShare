using System.Net;

namespace JellyShare.Jellyfin.Adapters;

/// <summary>
/// Mirrors how Jellyfin decides the bitrate it applies to a stream (<c>MediaInfoHelper.GetMaxBitrate</c>). Recheck
/// these when updating Jellyfin: they must match the version's behaviour or the transcode decision is wrong.
/// </summary>
internal static class JellyfinBitrateRules
{
    /// <summary>
    /// Computes the max bitrate Jellyfin will apply: the requested one (query, then body, then device profile),
    /// capped by the remote-client limit when one applies.
    /// </summary>
    /// <param name="requested">The bitrate the client asked for.</param>
    /// <param name="remoteLimit">The remote-client limit, if it applies.</param>
    /// <returns>The effective limit, or <c>null</c> when none.</returns>
    public static int? EffectiveMaxBitrate(int? requested, int? remoteLimit)
    {
        if (remoteLimit is > 0)
        {
            return Math.Min(requested ?? remoteLimit.Value, remoteLimit.Value);
        }

        return requested;
    }

    /// <summary>
    /// Decides the remote-client bitrate limit that applies to a caller: the user's own limit, else the server's, and
    /// only when the caller is not in the local network.
    /// </summary>
    /// <param name="userLimit">The calling user's remote-client limit, or <c>null</c>/0 when none.</param>
    /// <param name="serverLimit">The server-wide remote-client limit (0 or less means none).</param>
    /// <param name="remoteAddress">The caller's address, if known.</param>
    /// <param name="isInLocalNetwork">Tells whether an address is in the local network.</param>
    /// <returns>The limit, or <c>null</c> when none applies.</returns>
    public static int? RemoteLimit(int? userLimit, int serverLimit, IPAddress? remoteAddress, Func<IPAddress, bool> isInLocalNetwork)
    {
        ArgumentNullException.ThrowIfNull(isInLocalNetwork);
        var limit = userLimit ?? 0;
        if (limit <= 0)
        {
            limit = serverLimit;
        }

        if (limit <= 0 || remoteAddress is null)
        {
            return null;
        }

        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        return isInLocalNetwork(remoteAddress) ? null : limit;
    }
}
