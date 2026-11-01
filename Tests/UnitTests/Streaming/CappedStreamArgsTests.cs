using System.Globalization;
using JellyShare.Application.Features.Streaming;
using JellyShare.Domain.Enums;

namespace JellyShare.UnitTests.Streaming;

public sealed class CappedStreamArgsTests
{
    private const string Input = "/media/Movies/A Movie (2020)/a.mkv";

    private static string After(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        (index >= 0 && index + 1 < args.Count).ShouldBeTrue(flag + " missing");
        return args[index + 1];
    }

    [Theory]
    [InlineData(2_000_000L, "1808000", "3616000")]
    [InlineData(500_000L, "308000", "616000")]
    [InlineData(8_000_000L, "7808000", "15616000")]
    public void VideoBitrate_IsCapMinusAudio(long cap, string video, string bufsize)
    {
        var args = CappedStreamArgs.Build(Input, cap, null, HardwareEncoder.None, null);

        After(args, "-b:v").ShouldBe(video);
        After(args, "-maxrate").ShouldBe(video);
        After(args, "-bufsize").ShouldBe(bufsize);
        After(args, "-b:a").ShouldBe("192000");
    }

    [Fact]
    public void Software_UsesLibx264Veryfast_AacStereo_MpegTsToStdout()
    {
        var args = CappedStreamArgs.Build(Input, 2_000_000, null, HardwareEncoder.None, null);

        After(args, "-c:v").ShouldBe("libx264");
        After(args, "-preset").ShouldBe("veryfast");
        After(args, "-pix_fmt").ShouldBe("yuv420p");
        After(args, "-c:a").ShouldBe("aac");
        After(args, "-ac").ShouldBe("2");
        After(args, "-f").ShouldBe("mpegts");
        args[^1].ShouldBe("pipe:1");
        args.ShouldContain("-nostdin");
        args.ShouldContain("-sn");
    }

    [Fact]
    public void InputPath_IsOneVerbatimArgumentAfterDashI()
    {
        const string nasty = "/media/x\"; rm -rf / ; echo \"$(id) -f null.mkv";

        var args = CappedStreamArgs.Build(nasty, 2_000_000, null, HardwareEncoder.None, null);

        After(args, "-i").ShouldBe("file:" + nasty);
        args.Where(a => a.Contains(nasty, StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void InputPath_IsFileProtocolPrefixed_SoItCannotBeReadAsOptionOrProtocol()
    {
        After(CappedStreamArgs.Build("-i.mkv", 2_000_000, null, HardwareEncoder.None, null), "-i").ShouldBe("file:-i.mkv");
        After(CappedStreamArgs.Build("concat:/a|/b", 2_000_000, null, HardwareEncoder.None, null), "-i").ShouldBe("file:concat:/a|/b");
    }

    [Fact]
    public void Start_IsPlacedBeforeInput_InInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var args = CappedStreamArgs.Build(Input, 2_000_000, 12.5, HardwareEncoder.None, null).ToList();

            After(args, "-ss").ShouldBe("12.5");
            (args.IndexOf("-ss") < args.IndexOf("-i")).ShouldBeTrue();
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
        CappedStreamArgs.Build(Input, 2_000_000, start, HardwareEncoder.None, null).ShouldNotContain("-ss");
    }

    [Theory]
    [InlineData(HardwareEncoder.Nvenc, "h264_nvenc")]
    [InlineData(HardwareEncoder.Qsv, "h264_qsv")]
    [InlineData(HardwareEncoder.VideoToolbox, "h264_videotoolbox")]
    [InlineData(HardwareEncoder.Amf, "h264_amf")]
    public void Hardware_UsesHardwareEncoder_WithSameCap(HardwareEncoder hw, string encoder)
    {
        var args = CappedStreamArgs.Build(Input, 2_000_000, null, hw, null);

        After(args, "-c:v").ShouldBe(encoder);
        args.ShouldNotContain("libx264");
        After(args, "-maxrate").ShouldBe("1808000");
    }

    [Fact]
    public void Vaapi_UploadsFramesToDevice()
    {
        var args = CappedStreamArgs.Build(Input, 2_000_000, null, HardwareEncoder.Vaapi, "/dev/dri/renderD128").ToList();

        After(args, "-vaapi_device").ShouldBe("/dev/dri/renderD128");
        (args.IndexOf("-vaapi_device") < args.IndexOf("-i")).ShouldBeTrue();
        After(args, "-vf").ShouldBe("format=nv12,hwupload");
        After(args, "-c:v").ShouldBe("h264_vaapi");
    }

    [Theory]
    [InlineData(291_999L, false)]
    [InlineData(292_000L, true)]
    public void CanCap_RequiresRoomForVideo(long cap, bool expected)
    {
        CappedStreamArgs.CanCap(cap).ShouldBe(expected);
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

        CappedStreamArgs.TryParseStart(raw, runtimeTicks, out var seconds).ShouldBe(ok);
        seconds.ShouldBe(expected);
    }

    [Fact]
    public void TryParseStart_UnknownRuntime_IsClampedToSevenDays()
    {
        CappedStreamArgs.TryParseStart("5000", null, out var seconds).ShouldBeTrue();
        seconds.ShouldBe(5000.0);
        CappedStreamArgs.TryParseStart("99999999", null, out var clamped).ShouldBeTrue();
        clamped.ShouldBe(TimeSpan.FromDays(7).TotalSeconds);
    }
}
