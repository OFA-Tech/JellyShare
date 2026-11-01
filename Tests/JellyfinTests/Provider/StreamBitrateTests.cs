using JellyShare.Jellyfin.Provider;
using MediaBrowser.Model.Entities;
using Xunit;

namespace JellyShare.JellyfinTests.Provider;

public sealed class StreamBitrateTests
{
    private static MediaStream S(MediaStreamType type, int? rate) => new() { Type = type, BitRate = rate };

    [Fact]
    public void Sum_IsVideoPlusAudio_IgnoringSubtitles()
    {
        var streams = new[] { S(MediaStreamType.Video, 1_800_000), S(MediaStreamType.Audio, 640_000), S(MediaStreamType.Audio, 192_000), S(MediaStreamType.Subtitle, 5_000) };

        Assert.Equal(2_632_000, JellyfinLocalItemSource.SumOfStreamBitrates(streams));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(null)]
    public void Effective_NonPositiveTotal_FallsBackToStreams(int? total)
    {
        var streams = new[] { S(MediaStreamType.Video, 1_800_000), S(MediaStreamType.Audio, 200_000) };

        Assert.Equal(2_000_000, JellyfinLocalItemSource.EffectiveBitrate(total, streams));
    }

    [Fact]
    public void Effective_ZeroTotal_NoStreamRates_IsUnknown()
    {
        Assert.Null(JellyfinLocalItemSource.EffectiveBitrate(0, new[] { S(MediaStreamType.Video, 0) }));
    }

    [Fact]
    public void Effective_PositiveTotal_Wins()
    {
        Assert.Equal(5_000_000, JellyfinLocalItemSource.EffectiveBitrate(5_000_000, new[] { S(MediaStreamType.Video, 1) }));
    }

    [Fact]
    public void Sum_NothingKnown_IsNull()
    {
        Assert.Null(JellyfinLocalItemSource.SumOfStreamBitrates(new[] { S(MediaStreamType.Video, null), S(MediaStreamType.Audio, 0) }));
    }
}
