using JellyShare.Application.Features.Streaming;

namespace JellyShare.UnitTests.Streaming;

public sealed class CappedStreamArgsOverCapTests
{
    [Theory]
    [InlineData(null, 5_000_000L, false)]
    [InlineData(null, null, false)]
    [InlineData(2_000_000, 1_000_000L, false)]
    [InlineData(2_000_000, 2_000_000L, false)]
    [InlineData(2_000_000, 2_000_001L, true)]
    [InlineData(2_000_000, null, true)]
    [InlineData(2_000_000, 0L, true)]
    [InlineData(2_000_000, -5L, true)]
    public void IsOverCap_UnknownBitrateCountsAsOver(int? cap, long? bitrate, bool expected)
        => CappedStreamArgs.IsOverCap(cap, bitrate).ShouldBe(expected);
}
