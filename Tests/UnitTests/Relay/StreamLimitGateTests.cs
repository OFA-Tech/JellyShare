using JellyShare.Application.Features.Relay;
using JellyShare.Domain.Enums;
using JellyShare.Domain.Models.Configuration;
using JellyShare.Domain.Models.Relay;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Relay;

public sealed class StreamLimitGateTests
{
    private static readonly Guid _a = TestItems.PeerA;
    private static readonly Guid _b = TestItems.PeerB;

    private readonly MutableClock _clock = new();
    private readonly RelaySessions _sessions;

    public StreamLimitGateTests() => _sessions = new RelaySessions(_clock);

    private static RemotePeer Account(Guid id, string name, int max = 1, StreamLimitAction action = StreamLimitAction.Refuse)
        => new() { Id = id, Name = name, BaseUrl = "http://" + name, Kind = PeerKind.Account, MaxStreams = max, LimitAction = action };

    private static RemotePeer TokenPeer(Guid id, string name)
        => new() { Id = id, Name = name, BaseUrl = "http://" + name };

    private static RelaySource Source(RemotePeer peer) => new(peer, TestItems.Movie("M", tmdb: "1", id: "item-" + peer.Name));

    [Fact]
    public void HasRoom_TokenPeerAlways()
    {
        var peer = TokenPeer(_a, "alice");
        peer.MaxStreams = 1;
        _sessions.Touch("movie:tmdb:1", _a);

        StreamLimitGate.HasRoom(peer, "movie:tmdb:2", _sessions).ShouldBeTrue();
    }

    [Fact]
    public void HasRoom_AccountFull_IsFalse_ForAnotherTitle()
    {
        var peer = Account(_a, "alice");
        _sessions.Touch("movie:tmdb:1", _a);

        StreamLimitGate.HasRoom(peer, "movie:tmdb:2", _sessions).ShouldBeFalse();
    }

    [Fact]
    public void HasRoom_AccountFull_ButSameTitle_IsTrue()
    {
        var peer = Account(_a, "alice");
        _sessions.Touch("movie:tmdb:1", _a);

        StreamLimitGate.HasRoom(peer, "movie:tmdb:1", _sessions).ShouldBeTrue();
    }

    [Fact]
    public void HasRoom_AccountBelowLimit_IsTrue()
    {
        var peer = Account(_a, "alice", max: 2);
        _sessions.Touch("movie:tmdb:1", _a);

        StreamLimitGate.HasRoom(peer, "movie:tmdb:2", _sessions).ShouldBeTrue();
    }

    [Fact]
    public void Decide_NoSources_Plays()
        => StreamLimitGate.Decide([], "movie:tmdb:2", _sessions).Kind.ShouldBe(StreamDecisionKind.Play);

    [Fact]
    public void Decide_UsesOtherSourceWithRoom()
    {
        var alice = Account(_a, "alice");
        var bob = TokenPeer(_b, "bob");
        _sessions.Touch("movie:tmdb:1", _a);

        var decision = StreamLimitGate.Decide([Source(alice), Source(bob)], "movie:tmdb:2", _sessions);

        decision.Kind.ShouldBe(StreamDecisionKind.Play);
    }

    [Fact]
    public void Decide_RefuseByDefault()
    {
        var alice = Account(_a, "alice");
        _sessions.Touch("movie:tmdb:1", _a);

        var decision = StreamLimitGate.Decide([Source(alice)], "movie:tmdb:2", _sessions);

        decision.Kind.ShouldBe(StreamDecisionKind.Refuse);
        decision.Peer.ShouldBe(alice);
        decision.StopKey.ShouldBeNull();
    }

    [Fact]
    public void Decide_StopOldest_PicksOldestKey()
    {
        var alice = Account(_a, "alice", max: 2, action: StreamLimitAction.StopOldest);
        _sessions.Touch("movie:tmdb:7", _a);
        _clock.Now = _clock.Now.AddSeconds(1);
        _sessions.Touch("movie:tmdb:1", _a);

        var decision = StreamLimitGate.Decide([Source(alice)], "movie:tmdb:2", _sessions);

        decision.Kind.ShouldBe(StreamDecisionKind.StopOldest);
        decision.Peer.ShouldBe(alice);
        decision.StopKey.ShouldBe("movie:tmdb:7");
    }

    [Fact]
    public void Decide_FirstStopOldestSourceWins_OverEarlierRefuseSource()
    {
        var alice = Account(_a, "alice");
        var bob = Account(_b, "bob", action: StreamLimitAction.StopOldest);
        _sessions.Touch("movie:tmdb:1", _a);
        _sessions.Touch("movie:tmdb:3", _b);

        var decision = StreamLimitGate.Decide([Source(alice), Source(bob)], "movie:tmdb:2", _sessions);

        decision.Kind.ShouldBe(StreamDecisionKind.StopOldest);
        decision.Peer.ShouldBe(bob);
        decision.StopKey.ShouldBe("movie:tmdb:3");
    }
}
