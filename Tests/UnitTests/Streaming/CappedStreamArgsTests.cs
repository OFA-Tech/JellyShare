using System.Globalization;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Enums;
using Xunit;

namespace JellyShare.UnitTests.Streaming;

public sealed class CappedStreamArgsTests
{
    private const string Input = "/media/Movies/A Movie (2020)/a.mkv";

    private static string After(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < args.Count, flag + " missing");
        return args[index + 1];
    }

    [Theory]
    [InlineData(2_000_000L, "1808000", "3616000")]
    [InlineData(500_000L, "308000", "616000")]
    [InlineData(8_000_000L, "7808000", "15616000")]
    public void VideoBitrate_IsCapMinusAudio(long cap, string video, string bufsize)
    {
        var args = CappedStreamArgs.Build(Input, cap, null, HardwareEncoder.None, null);

        Assert.Equal(video, After(args, "-b:v"));
        Assert.Equal(video, After(args, "-maxrate"));
        Assert.Equal(bufsize, After(args, "-bufsize"));
        Assert.Equal("192000", After(args, "-b:a"));
    }

    [Fact]
    public void Software_UsesLibx264Veryfast_AacStereo_MpegTsToStdout()
    {
        var args = CappedStreamArgs.Build(Input, 2_000_000, null, HardwareEncoder.None, null);

        Assert.Equal("libx264", After(args, "-c:v"));
        Assert.Equal("veryfast", After(args, "-preset"));
        Assert.Equal("yuv420p", After(args, "-pix_fmt"));
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("2", After(args, "-ac"));
        Assert.Equal("mpegts", After(args, "-f"));
        Assert.Equal("pipe:1", args[^1]);
        Assert.Contains("-nostdin", args);
        Assert.Contains("-sn", args);
    }

    [Fact]
    public void InputPath_IsOneVerbatimArgumentAfterDashI()
    {
        const string nasty = "/media/x\"; rm -rf / ; echo \"$(id) -f null.mkv";

        var args = CappedStreamArgs.Build(nasty, 2_000_000, null, HardwareEncoder.None, null);

        Assert.Equal("file:" + nasty, After(args, "-i"));
        Assert.Single(args, a => a.Contains(nasty, StringComparison.Ordinal));
    }

    [Fact]
    public void InputPath_IsFileProtocolPrefixed_SoItCannotBeReadAsOptionOrProtocol()
    {
        Assert.Equal("file:-i.mkv", After(CappedStreamArgs.Build("-i.mkv", 2_000_000, null, HardwareEncoder.None, null), "-i"));
        Assert.Equal("file:concat:/a|/b", After(CappedStreamArgs.Build("concat:/a|/b", 2_000_000, null, HardwareEncoder.None, null), "-i"));
    }

    [Fact]
    public void Start_IsPlacedBeforeInput_InInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var args = CappedStreamArgs.Build(Input, 2_000_000, 12.5, HardwareEncoder.None, null).ToList();

            Assert.Equal("12.5", After(args, "-ss"));
            Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void NoOrZeroStart_HasNoSeek(double? start)
    {
        Assert.DoesNotContain("-ss", CappedStreamArgs.Build(Input, 2_000_000, start, HardwareEncoder.None, null));
    }

    [Theory]
    [InlineData(HardwareEncoder.Nvenc, "h264_nvenc")]
    [InlineData(HardwareEncoder.Qsv, "h264_qsv")]
    [InlineData(HardwareEncoder.VideoToolbox, "h264_videotoolbox")]
    [InlineData(HardwareEncoder.Amf, "h264_amf")]
    public void Hardware_UsesHardwareEncoder_WithSameCap(HardwareEncoder hw, string encoder)
    {
        var args = CappedStreamArgs.Build(Input, 2_000_000, null, hw, null);

        Assert.Equal(encoder, After(args, "-c:v"));
        Assert.DoesNotContain("libx264", args);
        Assert.Equal("1808000", After(args, "-maxrate"));
    }

    [Fact]
    public void Vaapi_UploadsFramesToDevice()
    {
        var args = CappedStreamArgs.Build(Input, 2_000_000, null, HardwareEncoder.Vaapi, "/dev/dri/renderD128").ToList();

        Assert.Equal("/dev/dri/renderD128", After(args, "-vaapi_device"));
        Assert.True(args.IndexOf("-vaapi_device") < args.IndexOf("-i"));
        Assert.Equal("format=nv12,hwupload", After(args, "-vf"));
        Assert.Equal("h264_vaapi", After(args, "-c:v"));
    }

    [Theory]
    [InlineData(291_999L, false)]
    [InlineData(292_000L, true)]
    public void CanCap_RequiresRoomForVideo(long cap, bool expected)
    {
        Assert.Equal(expected, CappedStreamArgs.CanCap(cap));
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("0", true, 0.0)]
    [InlineData("12.5", true, 12.5)]
    [InlineData("90", true, 90.0)]
    [InlineData("5000", true, 100.0)]
    [InlineData("-1", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("12,5", false, null)]
    [InlineData("NaN", false, null)]
    [InlineData("Infinity", false, null)]
    [InlineData("1e400", false, null)]
    [InlineData(" 1 -i /etc/passwd", false, null)]
    public void TryParseStart_NonNegativeInvariant_CappedToRuntime(string? raw, bool ok, double? expected)
    {
        var runtimeTicks = TimeSpan.FromSeconds(100).Ticks;

        Assert.Equal(ok, CappedStreamArgs.TryParseStart(raw, runtimeTicks, out var seconds));
        Assert.Equal(expected, seconds);
    }

    [Fact]
    public void TryParseStart_UnknownRuntime_IsClampedToSevenDays()
    {
        Assert.True(CappedStreamArgs.TryParseStart("5000", null, out var seconds));
        Assert.Equal(5000.0, seconds);
        Assert.True(CappedStreamArgs.TryParseStart("99999999", null, out var clamped));
        Assert.Equal(TimeSpan.FromDays(7).TotalSeconds, clamped);
    }
}
