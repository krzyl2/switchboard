namespace Switchboard.Application.Resilience;

/// <summary>
/// Reconnect delay that doubles from <c>min</c> up to <c>max</c> and starts over after a
/// successful connection, so a flapping broker or HA restart never produces a tight loop.
/// </summary>
public sealed class ExponentialBackoff
{
    private readonly TimeSpan _min;
    private readonly TimeSpan _max;
    private TimeSpan _next;

    public ExponentialBackoff(TimeSpan min, TimeSpan max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(min, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);
        _min = min;
        _max = max;
        _next = min;
    }

    /// <summary>Returns the delay to wait now and advances to the next one.</summary>
    public TimeSpan Next()
    {
        var current = _next;
        _next = current.Ticks > _max.Ticks / 2 ? _max : current + current;
        return current;
    }

    public void Reset() => _next = _min;
}
