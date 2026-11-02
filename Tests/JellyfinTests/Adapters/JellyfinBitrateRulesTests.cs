using System.Net;
using JellyShare.Jellyfin.Adapters;

namespace JellyShare.JellyfinTests.Adapters;

public sealed class JellyfinBitrateRulesTests
{
    private static readonly IPAddress _lan = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress _wan = IPAddress.Parse("203.0.113.7");
    private static readonly Func<IPAddress, bool> _isLan = ip => ip.Equals(_lan);

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(8_000_000, null, 8_000_000)]
    [InlineData(8_000_000, 0, 8_000_000)]
    [InlineData(8_000_000, 3_000_000, 3_000_000)]
    [InlineData(2_000_000, 3_000_000, 2_000_000)]
    [InlineData(null, 3_000_000, 3_000_000)]
    public void EffectiveMaxBitrate_IsRequestedCappedByRemoteLimit(int? requested, int? remoteLimit, int? expected)
        => JellyfinBitrateRules.EffectiveMaxBitrate(requested, remoteLimit).ShouldBe(expected);

    [Fact]
    public void RemoteLimit_UserLimitWins_OverTheServerLimit()
        => JellyfinBitrateRules.RemoteLimit(2_000_000, 8_000_000, _wan, _isLan).ShouldBe(2_000_000);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void RemoteLimit_NoUserLimit_UsesTheServerLimit(int? user)
        => JellyfinBitrateRules.RemoteLimit(user, 8_000_000, _wan, _isLan).ShouldBe(8_000_000);

    [Fact]
    public void RemoteLimit_NoLimitAnywhere_IsNull()
        => JellyfinBitrateRules.RemoteLimit(null, 0, _wan, _isLan).ShouldBeNull();

    [Fact]
    public void RemoteLimit_LocalNetworkCaller_IsNotLimited()
        => JellyfinBitrateRules.RemoteLimit(2_000_000, 8_000_000, _lan, _isLan).ShouldBeNull();

    [Fact]
    public void RemoteLimit_UnknownAddress_IsNull()
        => JellyfinBitrateRules.RemoteLimit(2_000_000, 8_000_000, null, _isLan).ShouldBeNull();

    [Fact]
    public void RemoteLimit_IPv4MappedAddress_IsCheckedAsIPv4()
        => JellyfinBitrateRules.RemoteLimit(2_000_000, 0, _lan.MapToIPv6(), _isLan).ShouldBeNull();
}
