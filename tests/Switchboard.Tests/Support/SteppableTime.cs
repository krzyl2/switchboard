namespace Switchboard.Tests.Support;

/// <summary>
/// A virtual clock that can also step backwards (an NTP step, a restored VM), which
/// FakeTimeProvider refuses. For code driven by explicit steps; it has no timers.
/// </summary>
internal sealed class SteppableTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), "Use StepBack to move the clock backwards.");
        }

        Now += by;
    }

    public void StepBack(TimeSpan by) => Now -= by;
}
