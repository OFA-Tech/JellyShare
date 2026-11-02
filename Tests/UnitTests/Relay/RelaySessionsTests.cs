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
        var first = _sessions.Lease("movie:tmdb:1", _a);
        first.IsNew.ShouldBeFalse();
        first.Stop.CanBeCanceled.ShouldBeTrue();

        _sessions.Release("movie:tmdb:1");

        first.Stop.IsCancellationRequested.ShouldBeTrue();
        _sessions.TryReserve("movie:tmdb:1", _a, 1, out _).ShouldBeTrue();
        var second = _sessions.Lease("movie:tmdb:1", _a);
        second.Stop.CanBeCanceled.ShouldBeTrue();
        second.Stop.IsCancellationRequested.ShouldBeFalse();
        first.Lease.Dispose();
        second.Lease.Dispose();
    }

    [Fact]
    public void Lease_LikeTouch_SamePeerKeepsToken_OtherPeerStartsNew()
    {
        var first = _sessions.Lease("movie:tmdb:1", _a);
        var again = _sessions.Lease("movie:tmdb:1", _a);
        var other = _sessions.Lease("movie:tmdb:1", _b);

        first.IsNew.ShouldBeTrue();
        again.IsNew.ShouldBeFalse();
        again.Stop.ShouldBe(first.Stop);
        other.IsNew.ShouldBeTrue();
        other.Stop.ShouldNotBe(first.Stop);
        _sessions.ActivePeer("movie:tmdb:1").ShouldBe(_b);
        first.Lease.Dispose();
        again.Lease.Dispose();
        other.Lease.Dispose();
    }

    [Fact]
    public void Lease_KeepsEntryPastGrace_ExpiresGraceAfterDispose()
    {
        var lease = _sessions.Lease("movie:tmdb:1", _a);

        _clock.Now = _clock.Now.Add(RelaySessions.Grace * 3);
        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1" });

        lease.Lease.Dispose();
        _clock.Now = _clock.Now.Add(RelaySessions.Grace).AddSeconds(-1);
        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1" });
        _clock.Now = _clock.Now.AddSeconds(1);
        _sessions.ActiveTitles(_a).ShouldBeEmpty();
    }

    [Fact]
    public void Lease_TwoOpen_EntryStaysUntilBothEnd()
    {
        var one = _sessions.Lease("movie:tmdb:1", _a);
        var two = _sessions.Lease("movie:tmdb:1", _a);
        one.Lease.Dispose();
        one.Lease.Dispose();

        _clock.Now = _clock.Now.Add(RelaySessions.Grace * 3);
        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1" });
        two.Lease.Dispose();
    }

    [Fact]
    public void Release_WithOpenLease_CancelsToken_LateDisposeLeavesNewEntryAlone()
    {
        var old = _sessions.Lease("movie:tmdb:1", _a);

        _sessions.Release("movie:tmdb:1");
        old.Stop.IsCancellationRequested.ShouldBeTrue();
        _sessions.ActiveTitles(_a).ShouldBeEmpty();

        _sessions.Touch("movie:tmdb:1", _a);
        _clock.Now = _clock.Now.AddSeconds(20);
        old.Lease.Dispose();

        // The old lease neither refreshed nor pinned the new entry: it expires a grace after its own touch.
        _clock.Now = _clock.Now.Add(RelaySessions.Grace).AddSeconds(-20);
        _sessions.ActiveTitles(_a).ShouldBeEmpty();
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
    public void CancelReservation_KeepsTheEntryWhileAnotherRequestHoldsALease()
    {
        _sessions.TryReserve("movie:tmdb:1", _a, 1, out var created).ShouldBeTrue();
        created.ShouldBeTrue();
        var (_, stop, lease) = _sessions.Lease("movie:tmdb:1", _a);

        _sessions.CancelReservation("movie:tmdb:1", _a);

        _sessions.ActiveTitles(_a).ShouldBe(new[] { "movie:tmdb:1" });
        _sessions.Release("movie:tmdb:1");
        stop.IsCancellationRequested.ShouldBeTrue();
        lease.Dispose();
    }

    [Fact]
    public void ActiveTitles_ExpiredAfterGrace_AreDropped()
    {
        _sessions.Touch("movie:tmdb:1", _a);
        _clock.Now = _clock.Now.Add(RelaySessions.Grace);

        _sessions.ActiveTitles(_a).ShouldBeEmpty();
    }
}
