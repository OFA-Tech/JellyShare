using JellyShare.Application.Features.Relay;

namespace JellyShare.UnitTests.Relay;

public sealed class PlaybackBitrateRuleTests
{
    [Theory]
    [InlineData(5_000_000, 1_000_000, 1_000_000)]
    [InlineData(5_000_000, 4_999_999, 4_999_999)]
    [InlineData(5_000_000, 5_000_000, null)]
    [InlineData(5_000_000, 8_000_000, null)]
    [InlineData(5_000_000, null, null)]
    [InlineData(5_000_000, 0, null)]
    [InlineData(5_000_000, -1, null)]
    [InlineData(null, 1_000_000, null)]
    [InlineData(0, 1_000_000, null)]
    public void ForcedTranscodeLimit_OnlyWhenTheLimitIsBelowTheSource(int? source, int? maxBitrate, int? expected)
        => PlaybackBitrateRule.ForcedTranscodeLimit(source, maxBitrate).ShouldBe(expected);
}
