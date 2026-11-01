using Jellyfin.Plugin.JellyShare.Provider;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

public sealed class StreamBitrateTests
{
    private static MediaStream S(MediaStreamType type, int? rate) => new() { Type = type, BitRate = rate };

    [Fact]
    public void Sum_IsVideoPlusAudio_IgnoringSubtitles()
    {
        var streams = new[] { S(MediaStreamType.Video, 1_800_000), S(MediaStreamType.Audio, 640_000), S(MediaStreamType.Audio, 192_000), S(MediaStreamType.Subtitle, 5_000) };

        Assert.Equal(2_632_000, JellyfinLocalItemSource.SumOfStreamBitrates(streams));
    }

    [Fact]
    public void Sum_NothingKnown_IsNull()
    {
        Assert.Null(JellyfinLocalItemSource.SumOfStreamBitrates(new[] { S(MediaStreamType.Video, null), S(MediaStreamType.Audio, 0) }));
    }
}
