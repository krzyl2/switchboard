namespace Switchboard.Tests.Support;

/// <summary>
/// A virtual clock that can also step backwards (an NTP step, a restored VM), which
/// FakeTimeProvider refuses. For code driven by explicit steps; it has no timers.
/// </summary>
internal sealed class SteppableTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>The local time zone this clock reports (a test that depends on local time, such as the tariff zones, sets it explicitly); the machine's by default.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public override TimeZoneInfo LocalTimeZone => Zone;

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
