using JellyShare.Application.Features.Relay;
using JellyShare.Data.Files.Placeholders;
using JellyShare.Data.Files.Stores;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Exceptions;
using JellyShare.Domain.Models.Placeholders;
using JellyShare.Domain.Keys;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Sync;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Relay;

public sealed class SourceSelectorTests
{
    private static readonly Guid _a = TestItems.PeerA;
    private static readonly Guid _b = TestItems.PeerB;
    private static readonly Guid _c = TestItems.PeerC;

    private static MergedEntry Entry(params Guid[] peers)
        => new(
            "movie:tmdb:1",
            new[] { "movie:tmdb:1" },
            peers.Select(p => (p, TestItems.Movie("M", tmdb: "1", id: "item-" + p.ToString("N")[..4]))).ToList());

    private static RemotePeer Peer(Guid id, string name, int priority = 0, bool enabled = true)
        => new() { Id = id, Name = name, Priority = priority, Enabled = enabled, BaseUrl = "http://" + name };

    private static Dictionary<Guid, PeerState> NoState() => new();

    [Fact]
    public void Order_PrefersLowerPriority()
    {
        var peers = new[] { Peer(_a, "alice", priority: 5), Peer(_b, "bob", priority: 1), Peer(_c, "carol", priority: 3) };

        var order = SourceSelector.Order(Entry(_a, _b, _c), peers, NoState());

        order.Select(s => s.Peer.Id).ShouldBe(new[] { _b, _c, _a });
        order[0].Item.Id.ShouldBe("item-" + _b.ToString("N")[..4]);
    }

    [Fact]
    public void Order_SkipsDisabled_SortsFailingLast()
    {
        var peers = new[] { Peer(_a, "alice", priority: 5), Peer(_b, "bob", enabled: false), Peer(_c, "carol", priority: 1) };
        var states = new Dictionary<Guid, PeerState>
        {
            [_c] = new() { FailingSinceUtc = DateTimeOffset.UnixEpoch, LastError = "HTTP 500" },
            [_a] = new() { LastSuccessUtc = DateTimeOffset.UnixEpoch },
        };

        var order = SourceSelector.Order(Entry(_a, _b, _c), peers, states);

        // _a peer that failed its last sync may be back already: it is still tried, after the healthy ones.
        order.Select(s => s.Peer.Id).ShouldBe(new[] { _a, _c });
    }

    [Fact]
    public void SourceSelector_SkipsPeerNotAcceptingType()
    {
        var onlyShows = Peer(_a, "alice");
        onlyShows.AcceptAllTypes = false;
        onlyShows.AcceptTypes = [MediaType.Shows];
        var peers = new[] { onlyShows, Peer(_b, "bob", priority: 1) };

        var order = SourceSelector.Order(Entry(_a, _b), peers, NoState());

        order.Select(s => s.Peer.Id).ShouldBe(new[] { _b });
    }

    [Fact]
    public void Order_OnlyFailingPeer_IsStillOffered()
    {
        var peers = new[] { Peer(_a, "alice") };
        var states = new Dictionary<Guid, PeerState> { [_a] = new() { FailingSinceUtc = DateTimeOffset.UnixEpoch } };

        SourceSelector.Order(Entry(_a), peers, states).Select(s => s.Peer.Id).ShouldBe(new[] { _a });
    }

    [Fact]
    public void Order_TieBreaksByName()
    {
        var peers = new[] { Peer(_a, "Zed"), Peer(_b, "anna"), Peer(_c, "Bob") };

        var order = SourceSelector.Order(Entry(_a, _b, _c), peers, NoState());

        order.Select(s => s.Peer.Id).ShouldBe(new[] { _b, _c, _a });
    }

    [Fact]
    public void Order_SameNameAndPriority_TieBreaksByPeerId()
    {
        var peers = new[] { Peer(_c, "same"), Peer(_a, "same"), Peer(_b, "SAME") };

        var order = SourceSelector.Order(Entry(_c, _b, _a), peers, NoState());

        order.Select(s => s.Peer.Id).ShouldBe(new[] { _a, _b, _c });
    }

    [Fact]
    public void Order_DropsPeersMissingFromConfig()
    {
        var peers = new[] { Peer(_b, "bob") };

        var order = SourceSelector.Order(Entry(_a, _b), peers, NoState());

        order.Select(s => s.Peer.Id).ShouldBe(new[] { _b });
    }

    [Fact]
    public void Order_FromStore_UsesPersistedFailures()
    {
        using var dir = new TempDir();
        var store = new PeerStateStore(dir.Sub("peer-state.json"));
        store.Save(new Dictionary<Guid, PeerState> { [_a] = new() { FailingSinceUtc = DateTimeOffset.UnixEpoch } });
        var peers = new[] { Peer(_a, "alice"), Peer(_b, "bob", priority: 9) };

        var order = SourceSelector.Order(Entry(_a, _b), peers, store);

        order.Select(s => s.Peer.Id).ShouldBe(new[] { _b, _a });
    }
}
