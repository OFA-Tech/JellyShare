using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Sync;

namespace JellyShare.Application.Features.Relay;

/// <summary>
/// Decides when a relay placeholder must be transcoded: Jellyfin never applies bitrate limits to remote sources
/// itself, so direct play and direct stream are turned off when the caller's max bitrate is below the source bitrate.
/// How the max bitrate is derived is Jellyfin's rule and lives in the Jellyfin project.
/// </summary>
public static class PlaybackBitrateRule
{
    /// <summary>Gets the limit that forces a transcode of a placeholder, if any.</summary>
    /// <param name="sourceBitrate">The source bitrate in bit/s, if known.</param>
    /// <param name="maxBitrate">The max bitrate Jellyfin will apply to the caller, if any.</param>
    /// <returns>The max bitrate when it is positive and below the source bitrate; otherwise <c>null</c>.</returns>
    public static int? ForcedTranscodeLimit(int? sourceBitrate, int? maxBitrate)
    {
        if (sourceBitrate is not { } source || source <= 0)
        {
            return null;
        }

        return maxBitrate is { } limit && limit > 0 && limit < source ? limit : null;
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
