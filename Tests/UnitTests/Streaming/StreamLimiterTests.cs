using JellyShare.Application.Features.Streaming;
using JellyShare.UnitTests.Fakes;

namespace JellyShare.UnitTests.Streaming;

public sealed class StreamLimiterTests
{
    private static readonly Guid ShareA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ShareB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid Item1 = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid Item2 = Guid.Parse("00000002-0000-0000-0000-000000000002");
    private static readonly Guid Item3 = Guid.Parse("00000003-0000-0000-0000-000000000003");

    private readonly ManualTimeProvider _clock = new();

    [Fact]
    public void SecondItem_OverLimit_Rejected()
    {
        var limiter = new StreamLimiter(_clock);

        limiter.TryAcquire(ShareA, Item1, 1).ShouldBeTrue();
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();
        limiter.Active(ShareA).ShouldBe(1);
    }

    [Fact]
    public void SameItemRangeRequests_DoNotConsumeExtraSlots()
    {
        var limiter = new StreamLimiter(_clock);

        limiter.TryAcquire(ShareA, Item1, 2).ShouldBeTrue();
        for (var i = 0; i < 20; i++)
        {
            limiter.TryAcquire(ShareA, Item1, 2).ShouldBeTrue();
        }

        limiter.Active(ShareA).ShouldBe(1);
        limiter.TryAcquire(ShareA, Item2, 2).ShouldBeTrue();
        limiter.TryAcquire(ShareA, Item3, 2).ShouldBeFalse();
    }

    [Fact]
    public void ActiveSession_IsAlwaysAllowed_EvenWhenLimitWasLowered()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquire(ShareA, Item1, 2).ShouldBeTrue();
        limiter.TryAcquire(ShareA, Item2, 2).ShouldBeTrue();

        limiter.TryAcquire(ShareA, Item1, 1).ShouldBeTrue();
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeTrue();
    }

    [Fact]
    public void Session_ExpiresAfter30sIdle()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquire(ShareA, Item1, 1).ShouldBeTrue();

        _clock.Advance(TimeSpan.FromSeconds(29));
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeTrue();
        limiter.Active(ShareA).ShouldBe(1);
    }

    [Fact]
    public void Request_RefreshesSessionExpiry()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquire(ShareA, Item1, 1).ShouldBeTrue();

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            limiter.TryAcquire(ShareA, Item1, 1).ShouldBeTrue();
        }

        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();
    }

    [Fact]
    public void DifferentShares_HaveIndependentLimits()
    {
        var limiter = new StreamLimiter(_clock);

        limiter.TryAcquire(ShareA, Item1, 1).ShouldBeTrue();
        limiter.TryAcquire(ShareB, Item1, 1).ShouldBeTrue();
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();
        limiter.TryAcquire(ShareB, Item2, 1).ShouldBeFalse();
        limiter.Active(ShareA).ShouldBe(1);
        limiter.Active(ShareB).ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxStreamsZeroOrNegative_RejectsNewSessions(int max)
    {
        var limiter = new StreamLimiter(_clock);

        limiter.TryAcquire(ShareA, Item1, max).ShouldBeFalse();
        limiter.Active(ShareA).ShouldBe(0);
    }

    [Fact]
    public void TryAcquire_ReportsNewSessionOnlyOnce()
    {
        var limiter = new StreamLimiter(_clock);

        limiter.TryAcquire(ShareA, Item1, 2, out var first).ShouldBeTrue();
        limiter.TryAcquire(ShareA, Item1, 2, out var second).ShouldBeTrue();
        limiter.TryAcquire(ShareA, Item2, 1, out var rejected).ShouldBeFalse();

        first.ShouldBeTrue();
        second.ShouldBeFalse();
        rejected.ShouldBeFalse();
    }

    [Fact]
    public void Session_HeldWhileBodyInFlight_DoesNotExpire()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquireLease(ShareA, Item1, 1, out var lease).ShouldBeTrue();

        _clock.Advance(TimeSpan.FromMinutes(10));

        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();
        limiter.Active(ShareA).ShouldBe(1);
        lease!.Dispose();
    }

    [Fact]
    public void Session_ReleasedAfterBody_ExpiresAfter30s()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquireLease(ShareA, Item1, 1, out var lease).ShouldBeTrue();
        _clock.Advance(TimeSpan.FromMinutes(10));

        lease!.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(29));
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeTrue();
    }

    [Fact]
    public void Session_WithTwoLeases_StaysHeldUntilBothDisposed()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquireLease(ShareA, Item1, 1, out var first).ShouldBeTrue();
        limiter.TryAcquireLease(ShareA, Item1, 1, out var second).ShouldBeTrue();
        first!.IsNewSession.ShouldBeTrue();
        second!.IsNewSession.ShouldBeFalse();

        first.Dispose();
        first.Dispose();
        _clock.Advance(TimeSpan.FromMinutes(5));
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();

        second.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(30));
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeTrue();
    }

    [Fact]
    public void Abort_OfNewSession_ReleasesSlotImmediately()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquireLease(ShareA, Item1, 1, out var lease).ShouldBeTrue();

        lease!.Abort();

        limiter.Active(ShareA).ShouldBe(0);
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeTrue();
    }

    [Fact]
    public void Abort_OfExistingSession_KeepsIt()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquire(ShareA, Item1, 1).ShouldBeTrue();
        limiter.TryAcquireLease(ShareA, Item1, 1, out var lease).ShouldBeTrue();

        lease!.Abort();

        limiter.Active(ShareA).ShouldBe(1);
        limiter.TryAcquire(ShareA, Item2, 1).ShouldBeFalse();
    }

    [Fact]
    public void TryAcquireLease_OverLimit_ReturnsNoLease()
    {
        var limiter = new StreamLimiter(_clock);
        limiter.TryAcquireLease(ShareA, Item1, 1, out _).ShouldBeTrue();

        limiter.TryAcquireLease(ShareA, Item2, 1, out var rejected).ShouldBeFalse();
        rejected.ShouldBeNull();
    }

    [Fact]
    public void ParallelDistinctItems_NeverExceedLimit()
    {
        const int max = 3;
        var limiter = new StreamLimiter(_clock);
        var allowed = 0;

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            if (limiter.TryAcquire(ShareA, Guid.NewGuid(), max))
            {
                Interlocked.Increment(ref allowed);
            }
        });

        allowed.ShouldBe(max);
        limiter.Active(ShareA).ShouldBe(max);
    }
}
