using JellyShare.Application.Features.Relay;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Relay;

public sealed class RelaySessionsTests
{
    private static readonly Guid _a = TestItems.PeerA;
    private static readonly Guid _b = TestItems.PeerB;

    private readonly MutableClock _clock = new();
    private readonly RelaySessions _sessions;

    public RelaySessionsTests() => _sessions = new RelaySessions(_clock);

    [Fact]
    public void ActiveTitles_OrdersByStart()
    {
        _sessions.Touch("movie:tmdb:2", _a);
        _clock.Now = _clock.Now.AddSeconds(1);
        _sessions.Touch("movie:tmdb:1", _a);
        _sessions.Touch("movie:tmdb:3", _b);

        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:2", "movie:tmdb:1" });
        _sessions.ActiveTitles(_b).ShouldBe(new[] { "movie:tmdb:3" });
    }

    [Fact]
    public void ActiveTitles_SameStart_OrdersByKey()
    {
        _sessions.Touch("movie:tmdb:2", _a);
        _sessions.Touch("movie:tmdb:1", _a);

        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1", "movie:tmdb:2" });
    }

    [Fact]
    public void Release_FreesSlot()
    {
        _sessions.Touch("movie:tmdb:1", _a);

        _sessions.Release("movie:tmdb:1");

        _sessions.ActiveTitles(_a).ShouldBeEmpty();
        _sessions.ActivePeer("movie:tmdb:1").ShouldBeNull();
    }

    [Fact]
    public void Touch_SameKey_DoesNotRestartStart()
    {
        _sessions.Touch("movie:tmdb:1", _a).ShouldBeTrue();
        _clock.Now = _clock.Now.AddSeconds(5);
        _sessions.Touch("movie:tmdb:2", _a);
        _clock.Now = _clock.Now.AddSeconds(5);

        _sessions.Touch("movie:tmdb:1", _a).ShouldBeFalse();

        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1", "movie:tmdb:2" });
    }

    [Fact]
    public void Touch_OtherPeer_RestartsStartAndMovesTitle()
    {
        _sessions.Touch("movie:tmdb:1", _a);
        _clock.Now = _clock.Now.AddSeconds(5);
        _sessions.Touch("movie:tmdb:2", _b);
        _clock.Now = _clock.Now.AddSeconds(5);

        _sessions.Touch("movie:tmdb:1", _b).ShouldBeTrue();

        _sessions.ActiveTitles(_a).ShouldBeEmpty();
        _sessions.ActiveTitles(_b).ShouldBe(new[] { "movie:tmdb:2", "movie:tmdb:1" });
    }

    [Fact]
    public void ActiveTitles_ExpiredAfterGrace_AreDropped()
    {
        _sessions.Touch("movie:tmdb:1", _a);
        _clock.Now = _clock.Now.Add(RelaySessions.Grace);

        _sessions.ActiveTitles(_a).ShouldBeEmpty();
    }
}
