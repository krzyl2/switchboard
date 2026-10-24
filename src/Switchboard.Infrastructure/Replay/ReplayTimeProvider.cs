namespace Switchboard.Infrastructure.Replay;

/// <summary>
/// The recorded data's clock: starts at the first recorded instant and runs <see cref="Speed"/>
/// times as fast as the host clock, timers included. Used by the signal pipeline in development
/// replay mode only; heartbeat, reconnect backoff and keep-alives never see it.
/// </summary>
public sealed class ReplayTimeProvider : TimeProvider
{
    private readonly TimeProvider _inner;
    private readonly DateTimeOffset _dataStart;
    private readonly DateTimeOffset _innerStart;
    private readonly long _innerStartTimestamp;

    public ReplayTimeProvider(TimeProvider inner, DateTimeOffset dataStart, double speed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speed);
        _inner = inner;
        _dataStart = dataStart;
        _innerStart = inner.GetUtcNow();
        _innerStartTimestamp = inner.GetTimestamp();
        Speed = speed;
    }

    public double Speed { get; }

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() =>
        _dataStart + TimeSpan.FromTicks((long)((_inner.GetUtcNow() - _innerStart).Ticks * Speed));

    public override long GetTimestamp() =>
        _innerStartTimestamp + (long)((_inner.GetTimestamp() - _innerStartTimestamp) * Speed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new ScaledTimer(_inner.CreateTimer(callback, state, ToInner(dueTime), ToInner(period)), this);

    // Rounded up, so a timer never fires before replay time has reached its due time.
    private TimeSpan ToInner(TimeSpan replay) =>
        replay == Timeout.InfiniteTimeSpan || replay <= TimeSpan.Zero
            ? replay
            : TimeSpan.FromTicks((long)Math.Ceiling(replay.Ticks / Speed));

    private sealed class ScaledTimer(ITimer inner, ReplayTimeProvider owner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(owner.ToInner(dueTime), owner.ToInner(period));

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
