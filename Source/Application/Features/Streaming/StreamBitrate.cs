namespace JellyShare.Application.Features.Streaming;

/// <summary>The bitrate of a library item, from its container total or its streams.</summary>
public static class StreamBitrate
{
    /// <summary>The container total when positive, else the sum of the stream bitrates; <c>null</c> when unknown.</summary>
    /// <param name="total">The container's total bitrate as the library recorded it (0 means unknown).</param>
    /// <param name="videoAndAudioRates">The bitrates of the video and audio streams (others left out).</param>
    /// <returns>The bitrate in bits per second, or <c>null</c>.</returns>
    public static int? Effective(int? total, IEnumerable<int?> videoAndAudioRates)
        => total is > 0 ? total : Sum(videoAndAudioRates);

    /// <summary>Sum of the known (positive) stream bitrates; <c>null</c> when none is known.</summary>
    /// <param name="videoAndAudioRates">The bitrates of the video and audio streams.</param>
    /// <returns>The sum in bits per second (capped at <see cref="int.MaxValue"/>), or <c>null</c>.</returns>
    public static int? Sum(IEnumerable<int?> videoAndAudioRates)
    {
        ArgumentNullException.ThrowIfNull(videoAndAudioRates);
        long sum = 0;
        var known = false;
        foreach (var rate in videoAndAudioRates)
        {
            if (rate is { } r && r > 0)
            {
                sum += r;
                known = true;
            }
        }

        return known ? (int)Math.Min(sum, int.MaxValue) : null;
    }
}
