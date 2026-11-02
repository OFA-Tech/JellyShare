using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Protocol;
using JellyShare.Domain.Models.Sync;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Relay;

public sealed class PlaybackBitrateRuleDecisionTests
{
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
