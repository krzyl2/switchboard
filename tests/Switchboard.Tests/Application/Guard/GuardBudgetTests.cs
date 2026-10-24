using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// BRK-02 in the guard: the per-phase overload budget runs on the meter's timeline, is charged
/// conservatively from the previous report, holds across invalid data and clock steps, and its
/// thresholds act at the exact tick they are crossed (04 §1: below 0.5 no increases, below 0.25
/// shed one even with a legal current, at 0 the top tier).
/// </summary>
public sealed class GuardBudgetTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;

    [Fact]
    public void Rise_is_charged_from_the_previous_report()
    {
        // The rise may have happened right after the previous report (12:00:00), so the 10 s
        // until the 30 A sample are charged at 1.20 × In: 10 s of a 3 min band.
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 30);

        Assert.Equal(1 - (10.0 / 180), s.PhaseStatus(Phase.L1).Budget, 9);
        s.RunUntil(T0 + TimeSpan.FromSeconds(30));
        Assert.Equal(1 - (30.0 / 180), s.PhaseStatus(Phase.L1).Budget, 9);
        Assert.Equal(BudgetTrend.Consuming, s.PhaseStatus(Phase.L1).BudgetTrend);
    }

    [Fact]
    public void Clock_step_back_neither_recovers_nor_consumes()
    {
        // An NTP step back by an hour must not credit (or charge) an hour: the budget holds at its
        // value until a new sample starts a new chain, and every lockout restarts.
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 30);
        s.RunUntil(T0 + TimeSpan.FromSeconds(60));
        var before = s.PhaseStatus(Phase.L1).Budget;

        s.Time.StepBack(TimeSpan.FromHours(1));
        s.Evaluate();

        Assert.Equal(before, s.PhaseStatus(Phase.L1).Budget, 12);
        Assert.Equal(BudgetTrend.Held, s.PhaseStatus(Phase.L1).BudgetTrend);
        s.Time.Advance(TimeSpan.FromSeconds(30));
        s.Evaluate();
        Assert.Equal(before, s.PhaseStatus(Phase.L1).Budget, 12);

        s.Sample(Phase.L1, 30);
        s.Time.Advance(TimeSpan.FromSeconds(18));
        s.Evaluate();
        Assert.Equal(before - 0.1, s.PhaseStatus(Phase.L1).Budget, 9);
    }

    [Fact]
    public void Budget_exhaustion_sheds_everything_to_the_tick()
    {
        // 1.32 × In from 12:00:00 (30 s band): empty at exactly 12:00:30. One tick earlier the
        // charger is only at its minimum; at 12:00:30 everything on L1 that can still give is off,
        // by the budget: with a stop actuator the charger goes off then.
        var s = new GuardScenario(TestDevices.Document(TestDevices.Reference.ReplaceLineEndings("\n") + "\n    stop_actuator: switch.charger_enable"));
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 33);
        s.RunUntil(T0 + TimeSpan.FromSeconds(20));
        s.Sample(Phase.L1, 33);

        s.RunUntil(T0 + TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.Equal(DeviceGuardState.Reduced, s.DeviceStatus("ev_charger").GuardState);

        s.RunUntil(T0 + TimeSpan.FromSeconds(30));
        var (at, off) = s.Timeline[^1];
        Assert.Equal((T0 + TimeSpan.FromSeconds(30), "ev_charger", GuardTarget.Off, ReasonCode.GuardBudgetLow), (at, off.Device.Value, off.Target, off.Reason));
        Assert.Equal(0, s.PhaseStatus(Phase.L1).Budget, 12);
        Assert.Equal(DeviceGuardState.Shed, s.DeviceStatus("ev_charger").GuardState);
    }

    [Fact]
    public void Budget_exhaustion_leaves_a_charger_without_stop_path_at_its_floor()
    {
        // Without a stop actuator the charger's setpoint range excludes zero: at its 6 A floor it
        // has nothing more to give, so the empty budget takes no further step on it (no "would
        // switch off" that could never happen) and it stays reduced.
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 33);
        s.RunUntil(T0 + TimeSpan.FromSeconds(20));
        s.Sample(Phase.L1, 33);

        s.RunUntil(T0 + TimeSpan.FromSeconds(30));

        Assert.Equal(0, s.PhaseStatus(Phase.L1).Budget, 12);
        Assert.DoesNotContain(s.Sheds, i => i.Device.Value == "ev_charger" && i.Target == GuardTarget.Off);
        Assert.Equal(DeviceGuardState.Reduced, s.DeviceStatus("ev_charger").GuardState);
    }

    [Fact]
    public void Shed_one_threshold_sheds_with_legal_current()
    {
        // 1.24 × In is legal for 3 min, but from a full budget it is below 0.25 after 135 s: one
        // more device goes then, 45 s before the band itself would have run out.
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 31);
        s.Hold(Phase.L1, 31, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(135));
        Assert.Equal(["heater_l1"], s.Sheds.Select(i => i.Device.Value));

        s.RunUntil(T0 + TimeSpan.FromSeconds(135) + TimeSpan.FromTicks(1));

        var (at, shed) = s.Timeline[^1];
        Assert.Equal((T0 + TimeSpan.FromSeconds(135) + TimeSpan.FromTicks(1), "ev_charger", GuardTarget.AtMinimum, ReasonCode.GuardBudgetLow), (at, shed.Device.Value, shed.Target, shed.Reason));
    }

    [Fact]
    public void Half_budget_stops_increases()
    {
        // 121 s at 1.20 × In leave 0.33; back at 5 A the budget recovers at 1/600 per second, so
        // increases stay refused for 103 s although the current is low, and the stop is recorded
        // once two boundaries saw it (persistence_down_cycles).
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(1));
        s.Sample(Phase.L1, 30);
        s.Hold(Phase.L1, 30, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(111));
        var lowAt = T0 + TimeSpan.FromSeconds(121);
        s.RunUntil(lowAt);
        s.Sample(Phase.L1, 5);

        var budget = s.PhaseStatus(Phase.L1).Budget;
        Assert.Equal(1 - (121.0 / 180), budget, 9);
        Assert.False(s.PhaseStatus(Phase.L1).IncreasesAllowed);

        var recovered = lowAt + TimeSpan.FromSeconds((0.5 - budget) * 600);
        s.Hold(Phase.L1, 5, TimeSpan.FromSeconds(10), recovered - TimeSpan.FromMilliseconds(1));
        Assert.False(s.PhaseStatus(Phase.L1).IncreasesAllowed);
        s.RunUntil(recovered + TimeSpan.FromMilliseconds(1));
        Assert.True(s.PhaseStatus(Phase.L1).IncreasesAllowed);
        Assert.Contains(s.Events, e => e.Action == DecisionAction.StopIncreases && e.Reason == ReasonCode.GuardBudgetLow && e.Phase == Phase.L1);
    }

    [Fact]
    public void Budget_survives_a_stale_blip_without_crediting_the_gap()
    {
        // A phase current that goes stale for a while: the budget holds at the moment the data
        // stopped being valid; the first sample after starts a new chain instead of charging or
        // crediting the gap.
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 30);
        s.RunUntil(T0 + TimeSpan.FromSeconds(40));
        s.Invalidate(Phase.L1, ProtectionInvalidReason.CurrentStale);
        var atInvalid = s.PhaseStatus(Phase.L1).Budget;
        Assert.Equal(1 - (40.0 / 180), atInvalid, 9);

        s.RunUntil(T0 + TimeSpan.FromMinutes(5));
        Assert.Equal(atInvalid, s.PhaseStatus(Phase.L1).Budget, 12);

        s.Sample(Phase.L1, 30);
        Assert.Equal(atInvalid, s.PhaseStatus(Phase.L1).Budget, 12);
    }

    [Fact]
    public void Projection_never_runs_past_data_validity()
    {
        // Between samples the budget is projected with the last current held, but never past the
        // sample's max_age (90 s here): a delayed stale snapshot cannot consume unobserved time.
        // 1.16 × In reported at 12:00:10 and never again: charged until 12:01:40, not beyond.
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 29);
        s.RunUntil(T0 + TimeSpan.FromMinutes(10));

        Assert.Equal(1 - (100.0 / 180), s.PhaseStatus(Phase.L1).Budget, 9);
    }

    [Fact]
    public void Modulating_relief_is_its_measured_draw_above_the_floor()
    {
        // The charger at 12 A: reducing it to its 6 A minimum relieves what it really draws above
        // 6 A. With its draw signal at 12 A that is 6 A (30 → 24 A); without a draw signal a
        // setpoint is a ceiling, not a draw, and the hold relieves nothing (30 A stays 30 A).
        var measured = new GuardScenario(TestDevices.Document(TestDevices.ReferenceWithChargerDraw));
        var unmeasured = new GuardScenario(TestDevices.Document());
        foreach (var s in new[] { measured, unmeasured })
        {
            s.SetState("ev_charger", new SetpointApplied(new Amperes(12)));
            s.SetState("heater_l1", new RelayApplied(false));
            s.SetDraw("ev_charger", Phase.L1, 12);
            s.Sample(Phase.L1, 8);
            s.RunUntil(T0 + TimeSpan.FromSeconds(5));
            s.Sample(Phase.L1, 30);
            s.RunUntil(T0 + TimeSpan.FromSeconds(30));
            Assert.Equal(("ev_charger", GuardTarget.AtMinimum), (s.Sheds.Single().Device.Value, s.Sheds.Single().Target));
        }

        Assert.Equal(24, measured.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value, 9);
        Assert.Equal(30, unmeasured.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value, 9);
    }
}
