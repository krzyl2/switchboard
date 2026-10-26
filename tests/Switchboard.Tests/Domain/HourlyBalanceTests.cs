using Switchboard.Domain.Balance;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The balance of the running hour (BAL-01, BAL-06, 13-HOURLY-BALANCE): what the utility bills is the
/// energy netted over the clock hour, so the balance must start from the last counter report at or
/// before :00, never from the first one after it, and must say "unknown" rather than guess.
/// </summary>
public sealed class HourlyBalanceTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    // 2026-10-26 is winter time (+01:00); 10:00 local is 09:00Z.
    private static readonly DateTimeOffset TenOclock = new(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));

    private static HourlyBalanceTracker Tracker() => new(Warsaw);

    private static EnergyCounters At(DateTimeOffset when, double exportKwh, double importKwh) => new(exportKwh, importKwh, when);

    [Fact]
    public void Balance_is_export_minus_import_since_the_last_report_at_or_before_the_hour_in_Wh()
    {
        var tracker = Tracker();
        tracker.Observe(TenOclock.AddSeconds(-5), At(TenOclock.AddSeconds(-5), 100.000, 200.000), MaxAge);

        var reading = tracker.Observe(TenOclock.AddMinutes(20), At(TenOclock.AddMinutes(20), 100.500, 200.200), MaxAge);

        Assert.True(reading.IsKnown);
        Assert.Equal(300, reading.Wh!.Value, 6);
        Assert.Equal(TenOclock, reading.HourStart);
    }

    [Fact]
    public void Baseline_is_the_report_before_the_boundary_not_the_first_one_after_it()
    {
        var tracker = Tracker();
        tracker.Observe(TenOclock.AddSeconds(-8), At(TenOclock.AddSeconds(-8), 100.000, 200.000), MaxAge);
        tracker.Observe(TenOclock.AddSeconds(-1), At(TenOclock.AddSeconds(-8), 100.000, 200.000), MaxAge);

        // The first report after :00 arrives 4 s late and already holds 0.02 kWh of import of the new hour.
        var reading = tracker.Observe(TenOclock.AddSeconds(4), At(TenOclock.AddSeconds(3), 100.000, 200.020), MaxAge);

        Assert.Equal(-20, reading.Wh!.Value, 6);
        Assert.Equal(200.000, tracker.Baseline!.ImportKwh);
    }

    [Fact]
    public void A_start_inside_the_hour_has_no_balance_until_the_next_hour()
    {
        var tracker = Tracker();

        var midHour = tracker.Observe(TenOclock.AddMinutes(25), At(TenOclock.AddMinutes(25), 100, 200), MaxAge);
        Assert.False(midHour.IsKnown);
        Assert.Equal(BalanceUnknown.NoBaseline, midHour.Reason);

        tracker.Observe(TenOclock.AddMinutes(59).AddSeconds(55), At(TenOclock.AddMinutes(59).AddSeconds(55), 100.1, 200), MaxAge);
        var next = tracker.Observe(TenOclock.AddHours(1).AddSeconds(10), At(TenOclock.AddHours(1).AddSeconds(5), 100.1, 200.05), MaxAge);

        Assert.Equal(-50, next.Wh!.Value, 6);
    }

    [Fact]
    public void A_persisted_baseline_of_the_same_hour_survives_a_restart()
    {
        var before = Tracker();
        before.Observe(TenOclock.AddSeconds(-5), At(TenOclock.AddSeconds(-5), 100.000, 200.000), MaxAge);
        before.Observe(TenOclock.AddMinutes(10), At(TenOclock.AddMinutes(10), 100.100, 200.000), MaxAge);
        var saved = before.Baseline;

        var after = Tracker();
        after.Restore(saved);
        var reading = after.Observe(TenOclock.AddMinutes(30), At(TenOclock.AddMinutes(30), 100.400, 200.000), MaxAge);

        Assert.Equal(400, reading.Wh!.Value, 6);
    }

    [Fact]
    public void A_persisted_baseline_of_an_earlier_hour_is_ignored()
    {
        var tracker = Tracker();
        tracker.Restore(new HourBaseline(TenOclock.AddHours(-1), 50, 50));

        var reading = tracker.Observe(TenOclock.AddMinutes(30), At(TenOclock.AddMinutes(30), 100, 200), MaxAge);

        Assert.Equal(BalanceUnknown.NoBaseline, reading.Reason);
    }

    [Fact]
    public void A_counter_that_decreases_makes_the_hour_unknown_and_the_next_hour_recovers()
    {
        var tracker = Tracker();
        tracker.Observe(TenOclock.AddSeconds(-5), At(TenOclock.AddSeconds(-5), 100, 200), MaxAge);
        tracker.Observe(TenOclock.AddMinutes(10), At(TenOclock.AddMinutes(10), 100.2, 200), MaxAge);

        var reset = tracker.Observe(TenOclock.AddMinutes(20), At(TenOclock.AddMinutes(20), 0.1, 0.3), MaxAge);
        Assert.Equal(BalanceUnknown.CounterReset, reset.Reason);
        var later = tracker.Observe(TenOclock.AddMinutes(40), At(TenOclock.AddMinutes(40), 0.5, 0.3), MaxAge);
        Assert.False(later.IsKnown);

        tracker.Observe(TenOclock.AddMinutes(59).AddSeconds(55), At(TenOclock.AddMinutes(59).AddSeconds(55), 0.9, 0.3), MaxAge);
        var next = tracker.Observe(TenOclock.AddHours(1).AddSeconds(30), At(TenOclock.AddHours(1).AddSeconds(20), 1.0, 0.3), MaxAge);
        Assert.Equal(100, next.Wh!.Value, 6);
    }

    [Fact]
    public void Stale_counters_make_the_balance_unknown_and_it_is_right_again_with_the_next_report()
    {
        var tracker = Tracker();
        tracker.Observe(TenOclock.AddSeconds(-5), At(TenOclock.AddSeconds(-5), 100, 200), MaxAge);
        tracker.Observe(TenOclock.AddMinutes(10), At(TenOclock.AddMinutes(10), 100.1, 200), MaxAge);

        // 3 minutes of silence: the same last report, older than max_age.
        var stale = tracker.Observe(TenOclock.AddMinutes(13), At(TenOclock.AddMinutes(10), 100.1, 200), MaxAge);
        Assert.Equal(BalanceUnknown.Stale, stale.Reason);

        // Counters are cumulative: what was drawn during the gap is in the next report.
        var back = tracker.Observe(TenOclock.AddMinutes(14), At(TenOclock.AddMinutes(14), 100.3, 200.1), MaxAge);
        Assert.Equal(200, back.Wh!.Value, 6);
    }

    [Fact]
    public void Unavailable_counters_are_unknown_and_never_a_zero()
    {
        var tracker = Tracker();
        tracker.Observe(TenOclock.AddSeconds(-5), At(TenOclock.AddSeconds(-5), 100, 200), MaxAge);

        var reading = tracker.Observe(TenOclock.AddMinutes(5), null, MaxAge);

        Assert.Equal(BalanceUnknown.Unavailable, reading.Reason);
        Assert.Null(reading.Wh);
    }

    [Fact]
    public void A_clock_step_back_makes_the_rest_of_the_hour_unknown()
    {
        var tracker = Tracker();
        tracker.Observe(TenOclock.AddSeconds(-5), At(TenOclock.AddSeconds(-5), 100, 200), MaxAge);
        tracker.Observe(TenOclock.AddMinutes(30), At(TenOclock.AddMinutes(30), 100.2, 200), MaxAge);

        var stepped = tracker.Observe(TenOclock.AddMinutes(10), At(TenOclock.AddMinutes(30), 100.2, 200), MaxAge);

        Assert.Equal(BalanceUnknown.ClockStepBack, stepped.Reason);
    }

    [Theory]
    [InlineData("2026-10-26T10:37:12+01:00", "2026-10-26T10:00:00+01:00")]
    [InlineData("2026-10-25T02:30:00+02:00", "2026-10-25T02:00:00+02:00")] // first 02:xx of the 25-hour day
    [InlineData("2026-10-25T02:30:00+01:00", "2026-10-25T02:00:00+01:00")] // the repeated 02:xx is another hour
    [InlineData("2026-03-29T03:10:00+02:00", "2026-03-29T03:00:00+02:00")] // the 23-hour day skips 02:xx
    public void Hours_are_local_clock_hours_also_across_a_clock_change(string instant, string expectedStart)
    {
        var start = HourlyBalanceTracker.HourStartOf(DateTimeOffset.Parse(instant), Warsaw);

        Assert.Equal(DateTimeOffset.Parse(expectedStart), start);
        Assert.Equal(DateTimeOffset.Parse(expectedStart).Offset, start.Offset);
    }

    [Fact]
    public void The_repeated_clock_hour_of_the_25_hour_day_is_billed_as_its_own_hour()
    {
        var first = new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2));
        var second = new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(1));
        var tracker = Tracker();
        tracker.Observe(first.AddSeconds(-5), At(first.AddSeconds(-5), 100, 200), MaxAge);
        var inFirst = tracker.Observe(first.AddMinutes(50), At(first.AddMinutes(50), 100.4, 200), MaxAge);
        tracker.Observe(second.AddSeconds(-5), At(second.AddSeconds(-5), 100.9, 200), MaxAge);

        var inSecond = tracker.Observe(second.AddMinutes(5), At(second.AddMinutes(5), 101.0, 200), MaxAge);

        Assert.Equal(400, inFirst.Wh!.Value, 6);
        Assert.Equal(100, inSecond.Wh!.Value, 6);
        Assert.NotEqual(inFirst.HourStart, inSecond.HourStart);
    }
}
