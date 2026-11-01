using System.Net;
using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Relay;

public sealed class PlaybackBitrateRuleDecisionTests
{
    private static readonly IPAddress _lan = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress _wan = IPAddress.Parse("203.0.113.7");
    private static readonly Func<IPAddress, bool> _isLan = ip => ip.Equals(_lan);

    [Fact]
    public void RemoteLimit_UserLimitWins_OverTheServerLimit()
        => PlaybackBitrateRule.RemoteLimit(2_000_000, 8_000_000, _wan, _isLan).ShouldBe(2_000_000);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void RemoteLimit_NoUserLimit_UsesTheServerLimit(int? user)
        => PlaybackBitrateRule.RemoteLimit(user, 8_000_000, _wan, _isLan).ShouldBe(8_000_000);

    [Fact]
    public void RemoteLimit_NoLimitAnywhere_IsNull()
        => PlaybackBitrateRule.RemoteLimit(null, 0, _wan, _isLan).ShouldBeNull();

    [Fact]
    public void RemoteLimit_LocalNetworkCaller_IsNotLimited()
        => PlaybackBitrateRule.RemoteLimit(2_000_000, 8_000_000, _lan, _isLan).ShouldBeNull();

    [Fact]
    public void RemoteLimit_UnknownAddress_IsNull()
        => PlaybackBitrateRule.RemoteLimit(2_000_000, 8_000_000, null, _isLan).ShouldBeNull();

    [Fact]
    public void RemoteLimit_IPv4MappedAddress_IsCheckedAsIPv4()
        => PlaybackBitrateRule.RemoteLimit(2_000_000, 0, _lan.MapToIPv6(), _isLan).ShouldBeNull();

    private static MergedEntry Entry(params (Guid Peer, int? Bitrate)[] sources)
        => new(
            "movie:tmdb:1",
            new[] { "movie:tmdb:1" },
            sources.Select(s => (s.Peer, TestItems.Movie("M", tmdb: "1", id: "i-" + s.Peer.ToString("N")[..4]) with
            {
                Media = new MediaInfo(null, null, null, null, s.Bitrate, [], []),
            })).ToList());

    private static RemotePeer Peer(Guid id, int priority) => new() { Id = id, Name = "p" + priority, Priority = priority, BaseUrl = "http://p" };

    [Fact]
    public void CatalogBitrate_PreferredSourceWins()
    {
        var entry = Entry((TestItems.PeerA, 9_000_000), (TestItems.PeerB, 4_000_000));
        var peers = new[] { Peer(TestItems.PeerA, 5), Peer(TestItems.PeerB, 1) };

        PlaybackBitrateRule.CatalogBitrate(entry, peers, new Dictionary<Guid, PeerState>()).ShouldBe(4_000_000);
    }

    [Fact]
    public void CatalogBitrate_PreferredHasNone_FallsBackToTheHighestKnown()
    {
        var entry = Entry((TestItems.PeerA, 9_000_000), (TestItems.PeerB, null), (TestItems.PeerC, 6_000_000));
        var peers = new[] { Peer(TestItems.PeerB, 0), Peer(TestItems.PeerA, 5), Peer(TestItems.PeerC, 6) };

        PlaybackBitrateRule.CatalogBitrate(entry, peers, new Dictionary<Guid, PeerState>()).ShouldBe(9_000_000);
    }

    [Fact]
    public void CatalogBitrate_NothingKnown_IsNull()
    {
        var entry = Entry((TestItems.PeerA, null), (TestItems.PeerB, 0));
        var peers = new[] { Peer(TestItems.PeerA, 0), Peer(TestItems.PeerB, 1) };

        PlaybackBitrateRule.CatalogBitrate(entry, peers, new Dictionary<Guid, PeerState>()).ShouldBeNull();
    }
}
