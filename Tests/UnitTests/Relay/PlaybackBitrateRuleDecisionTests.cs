using System.Net;
using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using JellyShare.UnitTests.Fakes;
using Xunit;

namespace JellyShare.UnitTests.Relay;

public sealed class PlaybackBitrateRuleDecisionTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Wan = IPAddress.Parse("203.0.113.7");
    private static readonly Func<IPAddress, bool> IsLan = ip => ip.Equals(Lan);

    [Fact]
    public void RemoteLimit_UserLimitWins_OverTheServerLimit()
        => Assert.Equal(2_000_000, PlaybackBitrateRule.RemoteLimit(2_000_000, 8_000_000, Wan, IsLan));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void RemoteLimit_NoUserLimit_UsesTheServerLimit(int? user)
        => Assert.Equal(8_000_000, PlaybackBitrateRule.RemoteLimit(user, 8_000_000, Wan, IsLan));

    [Fact]
    public void RemoteLimit_NoLimitAnywhere_IsNull()
        => Assert.Null(PlaybackBitrateRule.RemoteLimit(null, 0, Wan, IsLan));

    [Fact]
    public void RemoteLimit_LocalNetworkCaller_IsNotLimited()
        => Assert.Null(PlaybackBitrateRule.RemoteLimit(2_000_000, 8_000_000, Lan, IsLan));

    [Fact]
    public void RemoteLimit_UnknownAddress_IsNull()
        => Assert.Null(PlaybackBitrateRule.RemoteLimit(2_000_000, 8_000_000, null, IsLan));

    [Fact]
    public void RemoteLimit_IPv4MappedAddress_IsCheckedAsIPv4()
        => Assert.Null(PlaybackBitrateRule.RemoteLimit(2_000_000, 0, Lan.MapToIPv6(), IsLan));

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

        Assert.Equal(4_000_000, PlaybackBitrateRule.CatalogBitrate(entry, peers, new Dictionary<Guid, PeerState>()));
    }

    [Fact]
    public void CatalogBitrate_PreferredHasNone_FallsBackToTheHighestKnown()
    {
        var entry = Entry((TestItems.PeerA, 9_000_000), (TestItems.PeerB, null), (TestItems.PeerC, 6_000_000));
        var peers = new[] { Peer(TestItems.PeerB, 0), Peer(TestItems.PeerA, 5), Peer(TestItems.PeerC, 6) };

        Assert.Equal(9_000_000, PlaybackBitrateRule.CatalogBitrate(entry, peers, new Dictionary<Guid, PeerState>()));
    }

    [Fact]
    public void CatalogBitrate_NothingKnown_IsNull()
    {
        var entry = Entry((TestItems.PeerA, null), (TestItems.PeerB, 0));
        var peers = new[] { Peer(TestItems.PeerA, 0), Peer(TestItems.PeerB, 1) };

        Assert.Null(PlaybackBitrateRule.CatalogBitrate(entry, peers, new Dictionary<Guid, PeerState>()));
    }
}
