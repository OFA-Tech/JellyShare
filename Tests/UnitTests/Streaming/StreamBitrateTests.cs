using JellyShare.Application.Features.Streaming;

namespace JellyShare.UnitTests.Streaming;

public sealed class StreamBitrateTests
{
    [Fact]
    public void Sum_AddsKnownRates_IgnoringUnknownAndNonPositive()
        => StreamBitrate.Sum([1_800_000, null, 640_000, 0, 192_000, -5]).ShouldBe(2_632_000);

    [Fact]
    public void Sum_NothingKnown_IsNull()
        => StreamBitrate.Sum([null, 0]).ShouldBeNull();

    [Fact]
    public void Sum_Overflow_IsCappedAtIntMax()
        => StreamBitrate.Sum([int.MaxValue, int.MaxValue]).ShouldBe(int.MaxValue);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(null)]
    public void Effective_NonPositiveTotal_FallsBackToStreams(int? total)
        => StreamBitrate.Effective(total, [1_800_000, 200_000]).ShouldBe(2_000_000);

    [Fact]
    public void Effective_PositiveTotal_Wins()
        => StreamBitrate.Effective(5_000_000, [1]).ShouldBe(5_000_000);
}
