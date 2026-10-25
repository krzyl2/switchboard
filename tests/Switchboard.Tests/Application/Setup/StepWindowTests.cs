using Switchboard.Application.Setup.SelfTest;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// The window around a switching: long enough for a meter polled every 30 s (the reference M1 before
/// 2026-10-24, and many cloud meters) to be classifiable, and an isolation rule that a charging car's
/// draw noise does not trip, so a heater switched while the car charges still counts.
/// </summary>
public sealed class StepWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 7, 39, 31, TimeSpan.Zero);

    [Fact]
    public void Thirty_second_meter_is_classifiable()
    {
        // (2 + one lag poll) × 30 s = 90 s, longer than the 60 s isolation: the second report after
        // the switching (at +59 s) falls inside, so the step is measured instead of "meter silent".
        var window = StepWindow.Window(TimeSpan.FromSeconds(60), 1, TimeSpan.FromSeconds(30))!.Value;
        var series = Enumerable.Range(-4, 8).Select(i => new ReportPoint(T0 + TimeSpan.FromSeconds((30 * i) - 1), i <= 0 ? 400 : 2100)).ToList();

        var measured = StepWindow.Measure(series, T0, window, 1, 25);

        Assert.Equal(TimeSpan.FromSeconds(90), window);
        Assert.Equal((400.0, 400.0, 2100.0), (measured.Previous!.Value, measured.Before!.Value, measured.After!.Value));
        Assert.Null(StepWindow.Window(TimeSpan.FromSeconds(60), 1, null));
    }

    [Fact]
    public void A_ten_second_meter_keeps_the_isolation_time()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), StepWindow.Window(TimeSpan.FromSeconds(60), 1, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Charger_draw_noise_does_not_make_a_heater_switching_non_isolated()
    {
        // ±0.15 A of a charging car (synthetic-dispatch-day.csv) against T_quiet of a 7 A heater
        // (0.25 × 7 A = 1.75 A): not a draw change; a charger stepping by 2 A is.
        var noise = Enumerable.Range(-30, 60).Select(i => new ReportPoint(T0 + TimeSpan.FromSeconds(2.5 * i), 16 + (i % 2 == 0 ? 0.15 : -0.15))).ToList();
        var stepping = noise.Select(p => p.At > T0 ? p with { Value = p.Value + 2 } : p).ToList();

        Assert.False(StepWindow.Changed(noise, T0, TimeSpan.FromSeconds(60), 1.75));
        Assert.True(StepWindow.Changed(stepping, T0, TimeSpan.FromSeconds(60), 1.75));
    }

    [Fact]
    public void A_phase_that_does_not_report_in_the_window_has_no_after()
    {
        var series = new List<ReportPoint> { new(T0 - TimeSpan.FromSeconds(70), 400), new(T0 - TimeSpan.FromSeconds(5), 400), new(T0 + TimeSpan.FromSeconds(70), 2100) };

        Assert.Null(StepWindow.Measure(series, T0, TimeSpan.FromSeconds(60), 1, 25).After);
    }
}
