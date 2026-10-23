using Microsoft.Extensions.Time.Testing;

namespace Switchboard.Tests.Support;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports every timer it arms. Tests wait until the code under
/// test has armed the timer they are about to fire, so advancing the virtual clock never races the
/// code that is about to start waiting on it.
/// </summary>
internal sealed class ObservableTimeProvider : FakeTimeProvider
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _armed = [];
    private readonly Dictionary<TimeSpan, int> _unclaimed = [];
    private readonly List<(TimeSpan Due, TaskCompletionSource Signal)> _waiters = [];

    public ObservableTimeProvider()
        : base(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero))
    {
    }

    /// <summary>Every due time armed so far, in order.</summary>
    public IReadOnlyList<TimeSpan> Armed
    {
        get
        {
            lock (_gate)
            {
                return [.. _armed];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new RecordingTimer(base.CreateTimer(callback, state, dueTime, period), this);
        OnArmed(dueTime);
        return timer;
    }

    /// <summary>Completes once a timer with exactly this due time has been armed (each arming is claimed once).</summary>
    public Task WaitForTimerAsync(TimeSpan dueTime)
    {
        lock (_gate)
        {
            if (_unclaimed.TryGetValue(dueTime, out var count) && count > 0)
            {
                _unclaimed[dueTime] = count - 1;
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((dueTime, signal));
            return signal.Task.Soon();
        }
    }

    private void OnArmed(TimeSpan dueTime)
    {
        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        TaskCompletionSource? release = null;
        lock (_gate)
        {
            _armed.Add(dueTime);
            var index = _waiters.FindIndex(w => w.Due == dueTime);
            if (index >= 0)
            {
                release = _waiters[index].Signal;
                _waiters.RemoveAt(index);
            }
            else
            {
                _unclaimed[dueTime] = _unclaimed.GetValueOrDefault(dueTime) + 1;
            }
        }

        release?.TrySetResult();
    }

    private sealed class RecordingTimer(ITimer inner, ObservableTimeProvider owner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var changed = inner.Change(dueTime, period);
            owner.OnArmed(dueTime);
            return changed;
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
