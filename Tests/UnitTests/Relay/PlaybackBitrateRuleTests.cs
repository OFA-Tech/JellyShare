using JellyShare.Application.Features.Relay;
using Xunit;

namespace JellyShare.UnitTests.Relay;

public sealed class PlaybackBitrateRuleTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(8_000_000, null, 8_000_000)]
    [InlineData(8_000_000, 0, 8_000_000)]
    [InlineData(8_000_000, 3_000_000, 3_000_000)]
    [InlineData(2_000_000, 3_000_000, 2_000_000)]
    [InlineData(null, 3_000_000, 3_000_000)]
    public void EffectiveMaxBitrate_IsRequestedCappedByRemoteLimit(int? requested, int? remoteLimit, int? expected)
        => Assert.Equal(expected, PlaybackBitrateRule.EffectiveMaxBitrate(requested, remoteLimit));

    [Theory]
    [InlineData(5_000_000, 1_000_000, null, 1_000_000)]
    [InlineData(5_000_000, 120_000_000, 1_000_000, 1_000_000)]
    [InlineData(5_000_000, 5_000_000, null, null)]
    [InlineData(5_000_000, 8_000_000, null, null)]
    [InlineData(5_000_000, null, null, null)]
    [InlineData(5_000_000, 0, null, null)]
    [InlineData(5_000_000, -1, null, null)]
    [InlineData(null, 1_000_000, null, null)]
    [InlineData(0, 1_000_000, null, null)]
    public void ForcedTranscodeLimit_OnlyWhenTheLimitIsBelowTheSource(int? source, int? requested, int? remoteLimit, int? expected)
        => Assert.Equal(expected, PlaybackBitrateRule.ForcedTranscodeLimit(source, requested, remoteLimit));
}
