using System.Net;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Application.Features.Relay;

/// <summary>
/// Decides when a relay placeholder must be transcoded: Jellyfin never applies bitrate limits to remote sources
/// itself, so direct play and direct stream are turned off when the caller's effective max bitrate is below the
/// source bitrate.
/// </summary>
public static class PlaybackBitrateRule
{
    /// <summary>
    /// Computes the max bitrate Jellyfin will apply: the requested one (query, then body, then device profile),
    /// capped by the remote-client limit when one applies (Jellyfin's <c>MediaInfoHelper.GetMaxBitrate</c>).
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

    /// <summary>Gets the limit that forces a transcode of a placeholder, if any.</summary>
    /// <param name="sourceBitrate">The source bitrate in bit/s, if known.</param>
    /// <param name="requested">The bitrate the client asked for (query, body, or device profile).</param>
    /// <param name="remoteLimit">The remote-client limit, if it applies.</param>
    /// <returns>The effective limit when it is positive and below the source bitrate; otherwise <c>null</c>.</returns>
    public static int? ForcedTranscodeLimit(int? sourceBitrate, int? requested, int? remoteLimit)
    {
        if (sourceBitrate is not { } source || source <= 0)
        {
            return null;
        }

        var max = EffectiveMaxBitrate(requested, remoteLimit);
        return max is { } limit && limit > 0 && limit < source ? limit : null;
    }

    /// <summary>
    /// Decides the remote-client bitrate limit that applies to a caller (Jellyfin's <c>MediaInfoHelper.GetMaxBitrate</c>):
    /// the user's own limit, else the server's, and only when the caller is not in the local network.
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

    /// <summary>
    /// Gets the bitrate of a placeholder's title from the merged index before the first probe: the preferred source's
    /// catalog bitrate, else the highest bitrate any source reports.
    /// </summary>
    /// <param name="entry">The merged title.</param>
    /// <param name="peers">The configured peers.</param>
    /// <param name="states">The peer states by peer id.</param>
    /// <returns>The bitrate in bit/s, or <c>null</c> when no source reports one.</returns>
    public static int? CatalogBitrate(MergedEntry entry, IReadOnlyList<RemotePeer> peers, IReadOnlyDictionary<Guid, PeerState> states)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var ordered = SourceSelector.Order(entry, peers, states);
        var preferred = ordered.Count > 0 ? ordered[0] : null;
        return preferred?.Item.Media?.BitRate
            ?? entry.Sources.Select(s => s.Item.Media?.BitRate).Where(b => b is > 0).DefaultIfEmpty().Max();
    }
}
