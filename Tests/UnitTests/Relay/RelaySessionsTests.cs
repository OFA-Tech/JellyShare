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
    public void Release_CancelsStopToken_NewClaimGetsFreshToken()
    {
        _sessions.TryReserve("movie:tmdb:1", _a, 1, out _).ShouldBeTrue();
        var first = _sessions.StopToken("movie:tmdb:1");
        first.CanBeCanceled.ShouldBeTrue();

        _sessions.Release("movie:tmdb:1");

        first.IsCancellationRequested.ShouldBeTrue();
        _sessions.TryReserve("movie:tmdb:1", _a, 1, out _).ShouldBeTrue();
        var second = _sessions.StopToken("movie:tmdb:1");
        second.CanBeCanceled.ShouldBeTrue();
        second.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void StopToken_SameSessionKeepsToken_UnknownIsNone()
    {
        _sessions.Touch("movie:tmdb:1", _a);
        var token = _sessions.StopToken("movie:tmdb:1");
        _sessions.Touch("movie:tmdb:1", _a);

        _sessions.StopToken("movie:tmdb:1").ShouldBe(token);
        _sessions.StopToken("movie:tmdb:2").ShouldBe(CancellationToken.None);
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
    public void TryReserve_Free_ClaimsAndReportsCreated()
    {
        _sessions.TryReserve("movie:tmdb:1", _a, 1, out var created).ShouldBeTrue();

        created.ShouldBeTrue();
        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1" });
    }

    [Fact]
    public void TryReserve_Full_IsDeniedAndInsertsNothing()
    {
        _sessions.Touch("movie:tmdb:1", _a);

        _sessions.TryReserve("movie:tmdb:2", _a, 1, out var created).ShouldBeFalse();

        created.ShouldBeFalse();
        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1" });
    }

    [Fact]
    public void TryReserve_SameTitleAlreadyActive_IsAllowedButNotCreated()
    {
        _sessions.Touch("movie:tmdb:1", _a);

        _sessions.TryReserve("movie:tmdb:1", _a, 1, out var created).ShouldBeTrue();

        created.ShouldBeFalse();
    }

    [Fact]
    public void TryReserve_ZeroMax_IsDenied()
        => _sessions.TryReserve("movie:tmdb:1", _a, 0, out _).ShouldBeFalse();

    [Fact]
    public void TryReserve_ConcurrentDifferentTitles_OnlyOneWins()
    {
        for (var round = 0; round < 200; round++)
        {
            var sessions = new RelaySessions(_clock);
            using var barrier = new Barrier(2);
            var wins = 0;
            Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
            {
                barrier.SignalAndWait();
                if (sessions.TryReserve("movie:tmdb:" + i, _a, 1, out _))
                {
                    Interlocked.Increment(ref wins);
                }
            });

            wins.ShouldBe(1);
            sessions.ActiveTitles(_a).Count.ShouldBe(1);
        }
    }

    [Fact]
    public void CancelReservation_RemovesOnlyWhenStillOnThatPeer()
    {
        _sessions.Touch("movie:tmdb:1", _b);

        _sessions.CancelReservation("movie:tmdb:1", _a);
        _sessions.ActiveTitles(_b).ShouldBe(new[] { "movie:tmdb:1" });

        _sessions.CancelReservation("movie:tmdb:1", _b);
        _sessions.ActiveTitles(_b).ShouldBeEmpty();
    }

    [Fact]
    public void ActiveTitles_ExpiredAfterGrace_AreDropped()
    {
        _sessions.Touch("movie:tmdb:1", _a);
        _clock.Now = _clock.Now.Add(RelaySessions.Grace);

        _sessions.ActiveTitles(_a).ShouldBeEmpty();
    }
}
