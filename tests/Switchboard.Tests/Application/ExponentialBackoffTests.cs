using Switchboard.Application.Resilience;

namespace Switchboard.Tests.Application;

/// <summary>A dead broker or HA must be retried patiently, never in a tight loop, and quickly again once it is back.</summary>
public sealed class ExponentialBackoffTests
{
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(10);

    [Fact]
    public void Delay_doubles_from_min_and_caps_at_max()
    {
        var backoff = new ExponentialBackoff(Min, Max);

        var delays = Enumerable.Range(0, 6).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([1, 2, 4, 8, 10, 10], delays);
    }

    [Fact]
    public void Reset_starts_again_from_min()
    {
        var backoff = new ExponentialBackoff(Min, Max);
        backoff.Next();
        backoff.Next();

        backoff.Reset();

        Assert.Equal(Min, backoff.Next());
    }

    [Fact]
    public void Nonsense_bounds_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(TimeSpan.Zero, Max));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(Max, Min));
    }

    [Fact]
    public void A_huge_max_does_not_overflow()
    {
        var backoff = new ExponentialBackoff(TimeSpan.FromDays(1), TimeSpan.MaxValue);

        for (var i = 0; i < 64; i++)
        {
            Assert.True(backoff.Next() > TimeSpan.Zero);
        }
    }
}
