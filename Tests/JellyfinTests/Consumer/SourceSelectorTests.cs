using JellyShare.Jellyfin.Configuration;
using JellyShare.Jellyfin.Consumer;
using JellyShare.Jellyfin.Keys;
using JellyShare.JellyfinTests.Keys;
using Xunit;

namespace JellyShare.JellyfinTests.Consumer;

public class SourceSelectorTests
{
    private static readonly Guid A = TestItems.PeerA;
    private static readonly Guid B = TestItems.PeerB;
    private static readonly Guid C = TestItems.PeerC;

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
        var peers = new[] { Peer(A, "alice", priority: 5), Peer(B, "bob", priority: 1), Peer(C, "carol", priority: 3) };

        var order = SourceSelector.Order(Entry(A, B, C), peers, NoState());

        Assert.Equal(new[] { B, C, A }, order.Select(s => s.Peer.Id));
        Assert.Equal("item-" + B.ToString("N")[..4], order[0].Item.Id);
    }

    [Fact]
    public void Order_SkipsDisabled_SortsFailingLast()
    {
        var peers = new[] { Peer(A, "alice", priority: 5), Peer(B, "bob", enabled: false), Peer(C, "carol", priority: 1) };
        var states = new Dictionary<Guid, PeerState>
        {
            [C] = new() { FailingSinceUtc = DateTimeOffset.UnixEpoch, LastError = "HTTP 500" },
            [A] = new() { LastSuccessUtc = DateTimeOffset.UnixEpoch },
        };

        var order = SourceSelector.Order(Entry(A, B, C), peers, states);

        // A peer that failed its last sync may be back already: it is still tried, after the healthy ones.
        Assert.Equal(new[] { A, C }, order.Select(s => s.Peer.Id));
    }

    [Fact]
    public void Order_OnlyFailingPeer_IsStillOffered()
    {
        var peers = new[] { Peer(A, "alice") };
        var states = new Dictionary<Guid, PeerState> { [A] = new() { FailingSinceUtc = DateTimeOffset.UnixEpoch } };

        Assert.Equal(new[] { A }, SourceSelector.Order(Entry(A), peers, states).Select(s => s.Peer.Id));
    }

    [Fact]
    public void Order_TieBreaksByName()
    {
        var peers = new[] { Peer(A, "Zed"), Peer(B, "anna"), Peer(C, "Bob") };

        var order = SourceSelector.Order(Entry(A, B, C), peers, NoState());

        Assert.Equal(new[] { B, C, A }, order.Select(s => s.Peer.Id));
    }

    [Fact]
    public void Order_SameNameAndPriority_TieBreaksByPeerId()
    {
        var peers = new[] { Peer(C, "same"), Peer(A, "same"), Peer(B, "SAME") };

        var order = SourceSelector.Order(Entry(C, B, A), peers, NoState());

        Assert.Equal(new[] { A, B, C }, order.Select(s => s.Peer.Id));
    }

    [Fact]
    public void Order_DropsPeersMissingFromConfig()
    {
        var peers = new[] { Peer(B, "bob") };

        var order = SourceSelector.Order(Entry(A, B), peers, NoState());

        Assert.Equal(new[] { B }, order.Select(s => s.Peer.Id));
    }

    [Fact]
    public void Order_FromStore_UsesPersistedFailures()
    {
        using var dir = new TempDir();
        var store = new PeerStateStore(dir.Sub("peer-state.json"));
        store.Save(new Dictionary<Guid, PeerState> { [A] = new() { FailingSinceUtc = DateTimeOffset.UnixEpoch } });
        var peers = new[] { Peer(A, "alice"), Peer(B, "bob", priority: 9) };

        var order = SourceSelector.Order(Entry(A, B), peers, store);

        Assert.Equal(new[] { B, A }, order.Select(s => s.Peer.Id));
    }
}
