using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// One pass of the guard over the committed tier walk (tests/fixtures/replay/make-synthetic-overload.py)
/// with the maintainer's example configuration: heater elements (0.35 per kWh) on L1, L2, L3 and the
/// go-e charger (2.03 per kWh, 6 to 20 A) on L1, so on L1 the heater sheds first. Since 0.7.1 the
/// example declares the charger's force-state select as its stop actuator, so below its minimum the
/// charger is stopped too (BRK-03): the only outcomes of the walk that changed with it.
/// </summary>
public sealed class GuardSyntheticReplayRun
{
    public const string File = "synthetic-overload-tier-walk.csv";

    public static readonly DateTimeOffset Start = new(2026, 10, 21, 22, 0, 0, TimeSpan.Zero);

    // The maintainer's example with age-based device states (the default state_max_age of 10 min):
    // the live heaters are declared on_change because their Modbus switches report only on change,
    // but the fixture walks a frozen state through the default 10 min.
    private static string Configuration
    {
        get
        {
            var example = TestConfigurations.Example.ReplaceLineEndings("\n");
            var ageBased = example.Replace("    state_reports: on_change\n", string.Empty, StringComparison.Ordinal);
            Assert.NotEqual(example, ageBased);
            return ageBased;
        }
    }

    public GuardSyntheticReplayRun()
    {
        var data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", File));
        // Protection alone: the tier walk checks the guard's reactions to the tick, which dispatch's
        // own decisions (they change the intended state) would move.
        Harness = new GuardReplayHarness(data, Configuration, dispatch: false);
        Harness.RunUntil(data.End);
        Setup = ProtectionSetupFactory.Build(TestConfigurations.Valid(Configuration).Configuration!);
        Analyzer = new ViolationAnalyzer(Setup, Harness.Observations, Harness.Decisions, Harness.Steps);
    }

    internal GuardReplayHarness Harness { get; }

    internal ProtectionSetup Setup { get; }

    internal ViolationAnalyzer Analyzer { get; }

    /// <summary>The poll at <paramref name="minutes"/>:<paramref name="seconds"/> after the start: currents are written 10 ms after it (L1; L2 +1 ms, L3 +2 ms).</summary>
    public static DateTimeOffset Poll(int minutes, double seconds = 0, int phaseOffsetMs = 0) =>
        Start.AddMinutes(minutes).AddSeconds(seconds).AddMilliseconds(10 + phaseOffsetMs);

    public static DateTimeOffset Minute(int minutes, double seconds = 0) => Start.AddMinutes(minutes).AddSeconds(seconds);

    /// <summary>Protection's sheds of a device (dispatch's would-stops are not sheds).</summary>
    internal IEnumerable<Decision> Sheds(string device, DateTimeOffset from, DateTimeOffset to) =>
        Harness.Decisions.Where(d => d.Device?.Value == device && d.Origin == DecisionOrigin.Guard && d.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum && d.At >= from && d.At <= to);
}

public sealed class GuardSyntheticReplayTests(GuardSyntheticReplayRun run) : IClassFixture<GuardSyntheticReplayRun>
{
    [Fact]
    public void Below_and_exactly_at_rated_current_nothing_is_shed()
    {
        // 0.80 × In may add; exactly 1.00 × In is still the hold band (up_to is inclusive).
        Assert.Empty(L1Sheds(GuardSyntheticReplayRun.Minute(4, 50), GuardSyntheticReplayRun.Minute(20, 59)));
        Assert.Empty(L1Sheds(GuardSyntheticReplayRun.Minute(20, 59), GuardSyntheticReplayRun.Minute(36, 59)));
    }

    [Fact]
    public void Ordinary_cycle_band_sheds_at_the_second_boundary_and_its_timer_after_15_minutes()
    {
        // 1.08 × In from 37:00: reduce during the ordinary cycle, i.e. after persistence_down_cycles
        // (2) cycle boundaries; its 15 min band timer counts from the report before the rise
        // (36:50.010) and then takes the charger to its minimum. The phase is still over at the next
        // poll, and the charger now has a stop (its select, 0.7.1): the next step stops it, one step
        // per evaluation (BRK-03; before 0.7.1 it was left at 6 A, the fixture's own description).
        var heater = Assert.Single(run.Sheds("heater_l1", GuardSyntheticReplayRun.Minute(36, 59), GuardSyntheticReplayRun.Minute(54)));
        Assert.Equal((GuardSyntheticReplayRun.Minute(38), DecisionAction.SwitchOff, ReasonCode.GuardTierExceeded), (heater.At, heater.Action, heater.Reason));

        var charger = run.Sheds("ev_charger", GuardSyntheticReplayRun.Minute(36, 59), GuardSyntheticReplayRun.Minute(54)).ToList();
        Assert.Equal(
            [(GuardSyntheticReplayRun.Poll(36, 50) + TimeSpan.FromMinutes(15), DecisionAction.ReduceToMinimum), (GuardSyntheticReplayRun.Poll(52), DecisionAction.SwitchOff)],
            charger.Select(d => (d.At, d.Action)));
        Assert.All(charger, d => Assert.Equal(ReasonCode.GuardTierExceeded, d.Reason));
    }

    [Fact]
    public void Exactly_at_the_continuous_multiple_reduces_but_never_consumes_budget()
    {
        // 28.25 A is exactly 1.13 × In: the reduce band, but at the continuous threshold the
        // breaker carries it indefinitely, so the budget stays full.
        var heater = Assert.Single(run.Sheds("heater_l1", GuardSyntheticReplayRun.Minute(65, 59), GuardSyntheticReplayRun.Minute(71)));
        Assert.Equal(GuardSyntheticReplayRun.Minute(67), heater.At);
        Assert.Empty(run.Sheds("ev_charger", GuardSyntheticReplayRun.Minute(65, 59), GuardSyntheticReplayRun.Minute(71)));
        Assert.Equal(1.0, run.Harness.StatusAt(GuardSyntheticReplayRun.Minute(69, 55)).Phases.Single(p => p.Phase == Phase.L1).Budget);
    }

    [Fact]
    public void Next_cycle_band_sheds_at_the_first_boundary_and_the_budget_takes_the_charger()
    {
        // 1.20 × In from 83:00 (report before: 82:50.010): reduce next cycle; the budget falls at
        // 1/180 per second, is at 0.5 at t0 + 90 s and below 0.25 at t0 + 135 s, which takes the
        // charger to its minimum (strictly below: the first tick after it is exactly 0.25). With its
        // select stop (0.7.1) the next boundary (85:30) stops it below that minimum (BRK-03; before
        // 0.7.1 the boundary took no further step).
        var t0 = GuardSyntheticReplayRun.Poll(82, 50);
        var heater = Assert.Single(run.Sheds("heater_l1", GuardSyntheticReplayRun.Minute(82, 59), GuardSyntheticReplayRun.Minute(88)));
        Assert.Equal(GuardSyntheticReplayRun.Minute(83, 30), heater.At);

        Assert.Equal(0.5, run.Harness.StatusAt(t0 + TimeSpan.FromSeconds(90)).Phases.Single(p => p.Phase == Phase.L1).Budget, 9);
        var charger = run.Sheds("ev_charger", GuardSyntheticReplayRun.Minute(82, 59), GuardSyntheticReplayRun.Minute(88)).ToList();
        Assert.Equal(
            [(t0 + TimeSpan.FromSeconds(135) + TimeSpan.FromTicks(1), DecisionAction.ReduceToMinimum), (GuardSyntheticReplayRun.Minute(85, 30), DecisionAction.SwitchOff)],
            charger.Select(d => (d.At, d.Action)));
        Assert.All(charger, d => Assert.Equal(ReasonCode.GuardBudgetLow, d.Reason));
    }

    [Fact]
    public void Shed_one_band_sheds_at_the_entering_sample_and_the_budget_empties_in_30_seconds()
    {
        // 1.32 × In from 100:00 (report before: 99:50.010) costs 1/30 of the budget per second:
        // below 0.25 just after t0 + 22.5 s (the charger to its minimum), empty at t0 + 30 s. The
        // heater goes at the entering sample. With its select stop (0.7.1) the shed-one band stops
        // the charger at the next poll (100:20), before the budget is empty (BRK-03: a modulating
        // charger stopped fully below its minimum; before 0.7.1 it stayed at 6 A).
        var t0 = GuardSyntheticReplayRun.Poll(99, 50);
        var heater = Assert.Single(run.Sheds("heater_l1", GuardSyntheticReplayRun.Minute(99, 55), GuardSyntheticReplayRun.Minute(101)));
        Assert.Equal((GuardSyntheticReplayRun.Poll(100), ReasonCode.GuardTierExceeded), (heater.At, heater.Reason));

        var charger = run.Sheds("ev_charger", GuardSyntheticReplayRun.Minute(99, 55), GuardSyntheticReplayRun.Minute(101)).ToList();
        Assert.Equal(
            [(t0 + TimeSpan.FromSeconds(22.5) + TimeSpan.FromTicks(1), DecisionAction.ReduceToMinimum), (GuardSyntheticReplayRun.Poll(100, 20), DecisionAction.SwitchOff)],
            charger.Select(d => (d.At, d.Action)));
        Assert.Equal(0, run.Harness.StatusAt(t0 + TimeSpan.FromSeconds(30)).Phases.Single(p => p.Phase == Phase.L1).Budget, 9);
    }

    [Fact]
    public void Open_band_sheds_everything_on_the_phase_at_the_entering_sample()
    {
        // Everything on L1 at the entering sample: the heater off, and the charger stopped outright
        // through its select (0.7.1; full shed, BRK-03), where before it could only go to its floor.
        var sheds = L1Sheds(GuardSyntheticReplayRun.Minute(113, 55), GuardSyntheticReplayRun.Minute(115)).ToList();

        Assert.Equal(["ev_charger", "heater_l1"], sheds.Select(d => d.Device!.Value.Value).Order(StringComparer.Ordinal));
        Assert.All(sheds, d => Assert.Equal((GuardSyntheticReplayRun.Poll(114), ReasonCode.GuardTierExceeded), (d.At, d.Reason)));
        Assert.Equal(
            [("ev_charger", DecisionAction.SwitchOff), ("heater_l1", DecisionAction.SwitchOff)],
            sheds.Select(d => (d.Device!.Value.Value, d.Action)).Order());
    }

    [Fact]
    public void Thermal_memory_makes_the_second_excursion_empty_the_budget()
    {
        // 16 polls at 1.20 × In count 170 s (from the report before the rise to the first report
        // back below): 1 − 170/180 is left. 8 min 30 s of recovery add 510/600. The same excursion
        // again empties it after 0.906 × 180 s; without the memory it would leave 0.056 again.
        var t0 = GuardSyntheticReplayRun.Poll(127, 50);
        var afterFirst = run.Harness.StatusAt(t0 + TimeSpan.FromSeconds(170)).Phases.Single(p => p.Phase == Phase.L1).Budget;
        Assert.Equal(1 - (170.0 / 180), afterFirst, 9);

        var second = GuardSyntheticReplayRun.Poll(139, 10);
        Assert.Equal(t0 + TimeSpan.FromSeconds(170 + 510), second);
        var budgetAtSecond = afterFirst + (510.0 / 600);
        var empty = second + TimeSpan.FromSeconds(budgetAtSecond * 180);
        Assert.Equal(0, run.Harness.StatusAt(empty + TimeSpan.FromMilliseconds(1)).Phases.Single(p => p.Phase == Phase.L1).Budget, 9);
        Assert.True(run.Harness.StatusAt(empty - TimeSpan.FromMilliseconds(5)).Phases.Single(p => p.Phase == Phase.L1).Budget > 0);

        // Limits were released in between: the second excursion sheds the heater again.
        Assert.Single(run.Sheds("heater_l1", second, second + TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void Frozen_phase_current_is_safe_state_90_seconds_after_its_last_report()
    {
        var lastReport = GuardSyntheticReplayRun.Poll(155, 0, phaseOffsetMs: 1);
        var shed = Assert.Single(run.Sheds("heater_l2", GuardSyntheticReplayRun.Minute(154), GuardSyntheticReplayRun.Minute(160)));

        Assert.Equal((lastReport + TimeSpan.FromSeconds(90), ReasonCode.GuardStaleSignal), (shed.At, shed.Reason));
        Assert.Contains(run.Harness.Decisions, d => d.Action == DecisionAction.SafeStateEntered && d.Phase == Phase.L2 && d.At == shed.At);
        Assert.Empty(run.Sheds("heater_l1", GuardSyntheticReplayRun.Minute(154), GuardSyntheticReplayRun.Minute(160)));
    }

    [Fact]
    public void Exporting_phase_is_never_shed_and_asks_for_no_reduction()
    {
        var from = GuardSyntheticReplayRun.Minute(164, 59);
        var to = GuardSyntheticReplayRun.Minute(168);

        Assert.Empty(run.Sheds("heater_l3", from, to));
        Assert.Contains(run.Harness.Decisions, d => d.Action == DecisionAction.CannotReduceExporting && d.Phase == Phase.L3 && d.At >= from && d.At <= to);
        Assert.DoesNotContain(run.Harness.Decisions, d => d.Action == DecisionAction.RequestReduction && d.Phase == Phase.L3 && d.At >= from && d.At <= to);
    }

    [Fact]
    public void Frozen_device_state_is_unsafe_after_state_max_age()
    {
        // switch.grzalka_3 last reported at 170:00; its state signal's max_age is 10 min.
        var shed = Assert.Single(run.Sheds("heater_l3", GuardSyntheticReplayRun.Minute(170), GuardSyntheticReplayRun.Minute(185)));

        Assert.Equal((GuardSyntheticReplayRun.Minute(180), ReasonCode.GuardStaleSignal), (shed.At, shed.Reason));
        Assert.Equal(Switchboard.Application.Guard.DeviceGuardState.Unsafe, run.Harness.StatusAt(GuardSyntheticReplayRun.Minute(181)).Devices.Single(d => d.Id.Value == "heater_l3").GuardState);
    }

    [Fact]
    public void No_missed_violation()
    {
        Assert.Empty(run.Analyzer.MissedViolations());
    }

    [Fact]
    public void No_shed_on_a_phase_not_over_its_limit()
    {
        Assert.Empty(run.Analyzer.ShedsWithoutOverload());
        Assert.DoesNotContain(run.Harness.Decisions, d => d.Scope == DecisionScope.Device && d.Phase is { } phase && phase != Phase.L1
            && d.Reason is ReasonCode.GuardTierExceeded or ReasonCode.GuardBudgetLow);
    }

    [Fact]
    public void Every_guard_action_has_a_decision_record_and_none_is_commanded()
    {
        // The guard changes what it holds only through recorded decisions; in shadow mode not one
        // of them is commanded.
        Assert.Empty(run.Analyzer.SilentActions());
        Assert.NotEmpty(run.Harness.Decisions);
        Assert.All(run.Harness.Decisions, d => Assert.False(d.Commanded));
    }

    [Fact]
    public void The_analyzer_catches_a_guard_that_sheds_too_late()
    {
        // The check must be able to fail: the same run with every tier and budget shed of the
        // charger on L1 removed leaves the 1.20 × In segment without its required escalation.
        var late = run.Harness.Decisions.Where(d => !(d.Device?.Value == "ev_charger" && d.Reason is ReasonCode.GuardTierExceeded or ReasonCode.GuardBudgetLow)).ToList();
        var analyzer = new ViolationAnalyzer(run.Setup, run.Harness.Observations, late, run.Harness.Steps);

        Assert.Contains(analyzer.MissedViolations(), m => m.StartsWith("L1 band 3", StringComparison.Ordinal));

        // And in the open band one device left on is already a violation.
        var partial = run.Harness.Decisions.Where(d => !(d.Device?.Value == "heater_l1" && d.At == GuardSyntheticReplayRun.Poll(114))).ToList();
        var open = new ViolationAnalyzer(run.Setup, run.Harness.Observations, partial, run.Harness.Steps).MissedViolations();
        Assert.Contains(open, m => m.StartsWith("L1 band 5", StringComparison.Ordinal) && m.Contains("heater_l1", StringComparison.Ordinal));
    }

    private IEnumerable<Decision> L1Sheds(DateTimeOffset from, DateTimeOffset to) =>
        run.Sheds("heater_l1", from, to).Concat(run.Sheds("ev_charger", from, to));
}
