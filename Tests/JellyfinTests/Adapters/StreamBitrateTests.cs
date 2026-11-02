using JellyShare.Jellyfin.Adapters;
using MediaBrowser.Model.Entities;

namespace JellyShare.JellyfinTests.Adapters;

public sealed class StreamBitrateTests
{
    private static MediaStream S(MediaStreamType type, int? rate) => new() { Type = type, BitRate = rate };

    [Fact]
    public void Sum_IsVideoPlusAudio_IgnoringSubtitles()
    {
        var streams = new[] { S(MediaStreamType.Video, 1_800_000), S(MediaStreamType.Audio, 640_000), S(MediaStreamType.Audio, 192_000), S(MediaStreamType.Subtitle, 5_000) };

        JellyfinLocalItemSource.SumOfStreamBitrates(streams).ShouldBe(2_632_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(null)]
    public void Effective_NonPositiveTotal_FallsBackToStreams(int? total)
    {
        var streams = new[] { S(MediaStreamType.Video, 1_800_000), S(MediaStreamType.Audio, 200_000) };

        JellyfinLocalItemSource.EffectiveBitrate(total, streams).ShouldBe(2_000_000);
    }

    [Fact]
    public void Effective_ZeroTotal_NoStreamRates_IsUnknown()
    {
        JellyfinLocalItemSource.EffectiveBitrate(0, new[] { S(MediaStreamType.Video, 0) }).ShouldBeNull();
    }

    [Fact]
    public void Effective_PositiveTotal_Wins()
    {
        JellyfinLocalItemSource.EffectiveBitrate(5_000_000, new[] { S(MediaStreamType.Video, 1) }).ShouldBe(5_000_000);
    }

    [Fact]
    public void Sum_NothingKnown_IsNull()
    {
        JellyfinLocalItemSource.SumOfStreamBitrates(new[] { S(MediaStreamType.Video, null), S(MediaStreamType.Audio, 0) }).ShouldBeNull();
    }
}
