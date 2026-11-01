using System.Globalization;

namespace Jellyfin.Plugin.JellyShare.Provider;

/// <summary>Builds the ffmpeg argument list of a capped stream (MPEG-TS, H.264 at cap − audio, AAC stereo).</summary>
public static class CappedStreamArgs
{
    /// <summary>Audio bitrate of a capped stream (AAC stereo).</summary>
    public const long AudioBitrate = 192_000;

    /// <summary>Smallest video bitrate worth transcoding to.</summary>
    public const long MinimumVideoBitrate = 100_000;

    /// <summary>Whether a cap leaves room for video after audio.</summary>
    /// <param name="cap">The cap in bits per second.</param>
    /// <returns><c>true</c> when a transcode can fit.</returns>
    public static bool CanCap(long cap) => cap - AudioBitrate >= MinimumVideoBitrate;

    /// <summary>Builds the argument list (one element per argument, never passed through a shell).</summary>
    /// <param name="inputPath">The input file.</param>
    /// <param name="cap">The total cap in bits per second.</param>
    /// <param name="startSeconds">Start offset, or <c>null</c>.</param>
    /// <param name="hardware">The video encoder.</param>
    /// <param name="vaapiDevice">The VA-API device.</param>
    /// <returns>The arguments.</returns>
    public static IReadOnlyList<string> Build(string inputPath, long cap, double? startSeconds, HardwareEncoder hardware, string? vaapiDevice)
    {
        var video = Math.Max(cap - AudioBitrate, MinimumVideoBitrate);
        var videoText = video.ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (hardware == HardwareEncoder.Vaapi)
        {
            args.Add("-vaapi_device");
            args.Add(string.IsNullOrEmpty(vaapiDevice) ? "/dev/dri/renderD128" : vaapiDevice);
        }

        if (startSeconds is { } start && start > 0)
        {
            args.Add("-ss");
            args.Add(start.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.AddRange(new[] { "-i", inputPath, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn" });
        switch (hardware)
        {
            case HardwareEncoder.Nvenc:
                args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4" });
                break;
            case HardwareEncoder.Qsv:
                args.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "veryfast" });
                break;
            case HardwareEncoder.Vaapi:
                args.AddRange(new[] { "-vf", "format=nv12,hwupload", "-c:v", "h264_vaapi" });
                break;
            case HardwareEncoder.VideoToolbox:
                args.AddRange(new[] { "-c:v", "h264_videotoolbox" });
                break;
            case HardwareEncoder.Amf:
                args.AddRange(new[] { "-c:v", "h264_amf" });
                break;
            default:
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p" });
                break;
        }

        args.AddRange(new[]
        {
            "-b:v", videoText,
            "-maxrate", videoText,
            "-bufsize", (video * 2).ToString(CultureInfo.InvariantCulture),
            "-g", "48",
            "-c:a", "aac",
            "-ac", "2",
            "-b:a", AudioBitrate.ToString(CultureInfo.InvariantCulture),
            "-max_muxing_queue_size", "1024",
            "-f", "mpegts",
            "pipe:1",
        });
        return args;
    }

    /// <summary>Parses the <c>start</c> query value.</summary>
    /// <param name="raw">The raw value.</param>
    /// <param name="runTimeTicks">The item runtime, used as the upper bound.</param>
    /// <param name="seconds">The parsed offset, <c>null</c> when absent.</param>
    /// <returns><c>false</c> when the value is not a non-negative finite number.</returns>
    public static bool TryParseStart(string? raw, long? runTimeTicks, out double? seconds)
    {
        seconds = null;
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            return false;
        }

        if (runTimeTicks is { } ticks && ticks > 0)
        {
            value = Math.Min(value, TimeSpan.FromTicks(ticks).TotalSeconds);
        }

        seconds = value;
        return true;
    }
}
