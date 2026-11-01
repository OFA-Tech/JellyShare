using Jellyfin.Plugin.JellyShare.Provider;
using Xunit;

namespace Jellyfin.Plugin.JellyShare.Tests.Provider;

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

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

        Assert.True(limiter.TryAcquire(ShareA, Item1, 1));
        Assert.False(limiter.TryAcquire(ShareA, Item2, 1));
        Assert.Equal(1, limiter.Active(ShareA));
    }

    [Fact]
    public void SameItemRangeRequests_DoNotConsumeExtraSlots()
    {
        var limiter = new StreamLimiter(_clock);

        Assert.True(limiter.TryAcquire(ShareA, Item1, 2));
        for (var i = 0; i < 20; i++)
        {
            Assert.True(limiter.TryAcquire(ShareA, Item1, 2));
        }

        Assert.Equal(1, limiter.Active(ShareA));
        Assert.True(limiter.TryAcquire(ShareA, Item2, 2));
        Assert.False(limiter.TryAcquire(ShareA, Item3, 2));
    }

    [Fact]
    public void ActiveSession_IsAlwaysAllowed_EvenWhenLimitWasLowered()
    {
        var limiter = new StreamLimiter(_clock);
        Assert.True(limiter.TryAcquire(ShareA, Item1, 2));
        Assert.True(limiter.TryAcquire(ShareA, Item2, 2));

        Assert.True(limiter.TryAcquire(ShareA, Item1, 1));
        Assert.True(limiter.TryAcquire(ShareA, Item2, 1));
    }

    [Fact]
    public void Session_ExpiresAfter30sIdle()
    {
        var limiter = new StreamLimiter(_clock);
        Assert.True(limiter.TryAcquire(ShareA, Item1, 1));

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(limiter.TryAcquire(ShareA, Item2, 1));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire(ShareA, Item2, 1));
        Assert.Equal(1, limiter.Active(ShareA));
    }

    [Fact]
    public void Request_RefreshesSessionExpiry()
    {
        var limiter = new StreamLimiter(_clock);
        Assert.True(limiter.TryAcquire(ShareA, Item1, 1));

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            Assert.True(limiter.TryAcquire(ShareA, Item1, 1));
        }

        Assert.False(limiter.TryAcquire(ShareA, Item2, 1));
    }

    [Fact]
    public void DifferentShares_HaveIndependentLimits()
    {
        var limiter = new StreamLimiter(_clock);

        Assert.True(limiter.TryAcquire(ShareA, Item1, 1));
        Assert.True(limiter.TryAcquire(ShareB, Item1, 1));
        Assert.False(limiter.TryAcquire(ShareA, Item2, 1));
        Assert.False(limiter.TryAcquire(ShareB, Item2, 1));
        Assert.Equal(1, limiter.Active(ShareA));
        Assert.Equal(1, limiter.Active(ShareB));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxStreamsZeroOrNegative_RejectsNewSessions(int max)
    {
        var limiter = new StreamLimiter(_clock);

        Assert.False(limiter.TryAcquire(ShareA, Item1, max));
        Assert.Equal(0, limiter.Active(ShareA));
    }

    [Fact]
    public void TryAcquire_ReportsNewSessionOnlyOnce()
    {
        var limiter = new StreamLimiter(_clock);

        Assert.True(limiter.TryAcquire(ShareA, Item1, 2, out var first));
        Assert.True(limiter.TryAcquire(ShareA, Item1, 2, out var second));
        Assert.False(limiter.TryAcquire(ShareA, Item2, 1, out var rejected));

        Assert.True(first);
        Assert.False(second);
        Assert.False(rejected);
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

        Assert.Equal(max, allowed);
        Assert.Equal(max, limiter.Active(ShareA));
    }
}
