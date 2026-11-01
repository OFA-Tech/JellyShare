using JellyShare.Application.Features.Streaming;
using Xunit;

namespace JellyShare.UnitTests.Streaming;

public sealed class StreamBitrateTests
{
    [Fact]
    public void Sum_AddsKnownRates_IgnoringUnknownAndNonPositive()
        => Assert.Equal(2_632_000, StreamBitrate.Sum([1_800_000, null, 640_000, 0, 192_000, -5]));

    [Fact]
    public void Sum_NothingKnown_IsNull()
        => Assert.Null(StreamBitrate.Sum([null, 0]));

    [Fact]
    public void Sum_Overflow_IsCappedAtIntMax()
        => Assert.Equal(int.MaxValue, StreamBitrate.Sum([int.MaxValue, int.MaxValue]));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(null)]
    public void Effective_NonPositiveTotal_FallsBackToStreams(int? total)
        => Assert.Equal(2_000_000, StreamBitrate.Effective(total, [1_800_000, 200_000]));

    [Fact]
    public void Effective_PositiveTotal_Wins()
        => Assert.Equal(5_000_000, StreamBitrate.Effective(5_000_000, [1]));
}
