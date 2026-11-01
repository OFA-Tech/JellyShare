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
}
