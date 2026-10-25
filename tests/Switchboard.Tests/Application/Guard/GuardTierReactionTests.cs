using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// BRK-03: the band a phase current reaches decides how fast the guard reacts (04 §1 tier table,
/// 04 §2 step 2), on the reference installation (3 × 25 A, B): ordinary cycle after two
/// boundaries, next cycle, one device per new sample, everything at once. Every rule is checked to
/// the tick on the virtual clock.
/// </summary>
public sealed class GuardTierReactionTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;

    [Fact]
    public void May_add_and_hold_never_shed()
    {
        // Up to In the breaker carries the current indefinitely: nothing is ever shed there.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(21.25, 21.25, 21.25);
        s.Hold(Phase.L1, 21.25, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(30));
        s.Hold(Phase.L1, 25.0, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(90));

        Assert.Empty(s.Sheds);
    }

    [Fact]
    public void Reduce_ordinary_sheds_after_persistence_down_boundaries()
    {
        // 1.08 × In from 12:00:05: the 12:00:30 boundary is the first in the band, 12:01:00 the
        // second (persistence_down_cycles = 2), and only then the cheapest device on L1 goes.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 27);

        s.RunUntil(T0 + TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        Assert.Empty(s.Sheds);

        s.RunUntil(T0 + TimeSpan.FromSeconds(60));
        var (at, shed) = Assert.Single(s.Timeline);
        Assert.Equal(T0 + TimeSpan.FromSeconds(60), at);
        Assert.Equal(("heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded, Phase.L1), (shed.Device.Value, shed.Target, shed.Reason, shed.Phase));
    }

    [Fact]
    public void Reduce_next_sheds_at_the_next_boundary()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 30);

        s.RunUntil(T0 + TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.Empty(s.Sheds);
        s.RunUntil(T0 + TimeSpan.FromSeconds(30));

        var (at, shed) = Assert.Single(s.Timeline);
        Assert.Equal((T0 + TimeSpan.FromSeconds(30), "heater_l1"), (at, shed.Device.Value));
    }

    [Fact]
    public void Shed_one_sheds_one_device_per_new_sample()
    {
        // Three 1 A loads on L1 at 1.35 × In: each new meter sample sheds the next one, and no
        // tick in between sheds anything (the reaction is per sample, not per tick).
        var s = new GuardScenario(TestDevices.Document(SmallLoads));
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(1));
        s.Sample(Phase.L1, 33.75);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 33.75);
        s.RunUntil(T0 + TimeSpan.FromSeconds(20));
        s.Sample(Phase.L1, 33.75);

        Assert.Equal(
            [(T0 + TimeSpan.FromSeconds(1), "load_a"), (T0 + TimeSpan.FromSeconds(10), "load_b"), (T0 + TimeSpan.FromSeconds(20), "load_c")],
            s.Timeline.Select(t => (t.At, t.Intent.Device.Value)));
    }

    [Fact]
    public void Open_band_sheds_everything_on_the_phase_at_once()
    {
        // Above 1.45 × In: every controlled device on the phase in one evaluation; the heater off,
        // the charger (no stop actuator: its setpoint range excludes zero) to its 6 A floor, the
        // most it can give. With a stop actuator it goes off (below).
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));

        var evaluation = s.Sample(Phase.L1, 37.5);

        Assert.Equal(
            [("ev_charger", GuardTarget.AtMinimum), ("heater_l1", GuardTarget.Off)],
            evaluation.Intents.Select(i => (i.Device.Value, i.Target)).Order());
        Assert.All(evaluation.Intents, i => Assert.Equal(ReasonCode.GuardTierExceeded, i.Reason));
    }

    [Fact]
    public void Open_band_switches_off_a_charger_with_a_stop_actuator()
    {
        // DISP-01 stop_actuator: a modulating device that can be disabled is switched off in the open band.
        var s = new GuardScenario(TestDevices.Document(StopCharger));
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));

        var evaluation = s.Sample(Phase.L1, 37.5);

        Assert.Equal([("ev_charger", GuardTarget.Off), ("heater_l1", GuardTarget.Off)], evaluation.Intents.Select(i => (i.Device.Value, i.Target)).Order());
    }

    [Fact]
    public void Full_shed_stops_the_charger_through_its_select_below_its_minimum()
    {
        // BRK-03 with a select stop (0.7.1, the go-e force state): the open band takes everything on
        // L1 at once, and the charger goes below its 6 A minimum, stopped by its declared stop option
        // in the allow-list's shape, besides its floor setpoint.
        var path = new PlantCommandPath(TestConfigurations.Valid(TestDevices.Document(SelectCharger)).Configuration!, plant: null);
        var s = new GuardScenario(TestDevices.Document(SelectCharger), mode: DispatchScenario.Active, port: path.Port);
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));

        var evaluation = s.Sample(Phase.L1, 37.5);

        Assert.Equal([("ev_charger", GuardTarget.Off), ("heater_l1", GuardTarget.Off)], evaluation.Intents.Select(i => (i.Device.Value, i.Target)).Order());
        var stop = Assert.Single(path.Calls, c => c.Call.Service == Switchboard.Application.Configuration.ActuatorService.SelectOption);
        Assert.Equal(("select.charger_force", "1"), (stop.Call.Entity.Value, stop.Call.Option));
        Assert.Contains(path.Calls, c => c.Call.Entity.Value == "number.charger_current" && c.Call.Value == 6);
        Assert.Contains(s.DecisionsFor("ev_charger"), d => d.Action == DecisionAction.SwitchOff && d.Commanded);
    }

    [Fact]
    public void Shed_one_reduces_to_minimum_before_stopping()
    {
        // BRK-03 one step per new sample (1.35 × In): the heater first (lower value), then the charger
        // to its minimum, then, since it can be stopped through its select, below it; never the
        // stop before the reduction.
        var path = new PlantCommandPath(TestConfigurations.Valid(TestDevices.Document(SelectCharger)).Configuration!, plant: null);
        var s = new GuardScenario(TestDevices.Document(SelectCharger), mode: DispatchScenario.Active, port: path.Port);
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(1));
        foreach (var seconds in new[] { 1, 10, 20 })
        {
            s.RunUntil(T0 + TimeSpan.FromSeconds(seconds));
            s.Sample(Phase.L1, 33.75);
        }

        Assert.Equal(
            [("heater_l1", GuardTarget.Off), ("ev_charger", GuardTarget.AtMinimum), ("ev_charger", GuardTarget.Off)],
            s.Timeline.Select(t => (t.Intent.Device.Value, t.Intent.Target)));
        Assert.Equal(
            ["number.charger_current 6", "select.charger_force 1"],
            path.Calls.Where(c => c.Command.Device.Value == "ev_charger").Select(c => $"{c.Call.Entity.Value} {c.Call.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? c.Call.Option}"));
    }

    [Fact]
    public void Shed_on_one_phase_never_touches_another()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 37.5);
        s.Hold(Phase.L1, 37.5, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(5));

        Assert.NotEmpty(s.Sheds);
        Assert.All(s.Sheds, i => Assert.Contains(i.Device.Value, new[] { "heater_l1", "ev_charger" }));
    }

    [Fact]
    public void Pending_relief_prevents_a_cascade()
    {
        // 1.32 × In: the first sample sheds the heater (6.6 A). In shadow nothing changes, so the
        // next sample measures the same 33 A; counting the heater as already gone (33 − 6.6 A =
        // 1.06 × In) keeps the guard from shedding the charger for the same overload. The next
        // shed comes from the budget (below 0.25 at 22.5 s), not from the tier.
        var s = new GuardScenario(TestDevices.Document());
        s.SetState("ev_charger", new SetpointApplied(new Amperes(16)));
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.Sample(Phase.L1, 33);
        s.RunUntil(T0 + TimeSpan.FromSeconds(20));
        s.Sample(Phase.L1, 33);

        s.RunUntil(T0 + TimeSpan.FromSeconds(22.5));
        Assert.Equal(["heater_l1"], s.Sheds.Select(i => i.Device.Value));

        s.RunUntil(T0 + TimeSpan.FromSeconds(22.5) + TimeSpan.FromTicks(1));
        var second = s.Sheds.Last();
        Assert.Equal(("ev_charger", GuardTarget.AtMinimum, ReasonCode.GuardBudgetLow), (second.Device.Value, second.Target, second.Reason));
    }

    [Fact]
    public void Allowed_duration_expiry_escalates_to_the_tick()
    {
        // 1.08 × In is within the 15 min band. The tier reaction sheds the heater at the second
        // boundary, which makes the effective current legal, but the measured current stays
        // above 1.00 × In: when the band's 15 min run out (counted from the last report before the
        // rise) the next step is taken at that tick: the charger to its minimum. It has no stop
        // path, so no further step is spent on it (Stage 2 open issue: it would relieve nothing).
        var s = new GuardScenario(TestDevices.Document());
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 27);
        var expiry = T0 + TimeSpan.FromMinutes(15);

        s.Hold(Phase.L1, 27, TimeSpan.FromSeconds(10), expiry - TimeSpan.FromTicks(1));
        Assert.Equal(["heater_l1"], s.Sheds.Select(i => i.Device.Value));

        s.RunUntil(expiry);
        Assert.Equal((expiry, "ev_charger", GuardTarget.AtMinimum), (s.Timeline[^1].At, s.Timeline[^1].Intent.Device.Value, s.Timeline[^1].Intent.Target));

        s.RunUntil(expiry + TimeSpan.FromSeconds(5));
        var before = s.Intents.Count;
        s.Sample(Phase.L1, 27);
        Assert.Equal(before, s.Intents.Count);
    }

    [Fact]
    public void Exporting_phase_is_never_shed_but_recorded()
    {
        // 30 A of export on L3 (1.20 × In): the breaker heats, but shedding load would only raise
        // the export. No step; one record that a reaction was due and could not help.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L3, 30, FlowDirection.Export);
        s.Hold(Phase.L3, 30, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(5), FlowDirection.Export);

        Assert.DoesNotContain(s.Sheds, i => i.Device.Value == "heater_l3");
        var blocked = Assert.Single(s.Events, e => e.Action == DecisionAction.CannotReduceExporting);
        Assert.Equal((Phase.L3, ReasonCode.GuardTierExceeded), (blocked.Phase!.Value, blocked.Reason));
    }

    [Fact]
    public void Cannot_reduce_exporting_recorded_once_per_excursion_through_a_pending_direction_poll()
    {
        // Stage 2 open issue: one exporting overload is one record, even when one poll in the middle
        // of it arrives with its direction still pending (counted as import, cautious mode). Only a
        // confirmed import or the end of the overload ends the excursion.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L3, 33.75, FlowDirection.Export);
        s.RunFor(TimeSpan.FromSeconds(10));
        s.Sample(Phase.L3, 33.75, FlowDirection.Unknown);
        s.RunFor(TimeSpan.FromSeconds(10));
        s.Sample(Phase.L3, 33.75, FlowDirection.Export);
        s.RunFor(TimeSpan.FromSeconds(10));
        s.Sample(Phase.L3, 33.75, FlowDirection.Export);

        Assert.Single(s.Events, e => e.Action == DecisionAction.CannotReduceExporting);

        s.RunFor(TimeSpan.FromSeconds(10));
        s.Sample(Phase.L3, 8, FlowDirection.Import);
        s.RunFor(TimeSpan.FromSeconds(10));
        s.Sample(Phase.L3, 33.75, FlowDirection.Export);
        Assert.Equal(2, s.Events.Count(e => e.Action == DecisionAction.CannotReduceExporting));
    }

    [Fact]
    public void Exporting_phase_never_requests_reduction_and_reports_signed_headroom()
    {
        // Adding load to a phase exporting 30 A lowers its current: the headroom is the signed
        // distance (14.13 A threshold + 30 A), and dispatch is never asked to reduce there.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(1));
        s.Sample(Phase.L3, 30, FlowDirection.Export);
        s.RunUntil(T0 + TimeSpan.FromSeconds(31));

        var l3 = s.PhaseStatus(Phase.L3);
        Assert.Equal(-30, l3.Current!.Value.Value);
        Assert.Equal(14.1304 + 30, l3.Headroom!.Value.Value, 3);
        Assert.Equal(28.25 + 30, l3.BreakerHeadroom!.Value.Value, 6);
        Assert.True(l3.IncreasesAllowed);

        s.RunFor(TimeSpan.FromMinutes(10));
        Assert.DoesNotContain(s.Events, e => e.Action == DecisionAction.RequestReduction && e.Phase == Phase.L3);
    }

    [Fact]
    public void Open_band_uses_measured_current_despite_pending_relief()
    {
        // The heater and the charger's reduction are already decided; the charger really draws
        // 20 A (its own draw signal), so the relief is real: 6.6 + 14 A, and the estimate of 37.5 A
        // measured would be may_add. The open band follows the measured current, so the charger
        // (with a stop actuator) goes off by the tier.
        var s = new GuardScenario(TestDevices.Document(StopCharger + "\n    draw: { current: sensor.charger_draw }"));
        s.SetDraw("ev_charger", Phase.L1, 20);
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 31);
        s.Hold(Phase.L1, 31, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(140));
        Assert.Equal([("heater_l1", GuardTarget.Off), ("ev_charger", GuardTarget.AtMinimum)], s.Sheds.Select(i => (i.Device.Value, i.Target)));

        var evaluation = s.Sample(Phase.L1, 37.5);

        var off = Assert.Single(evaluation.Intents);
        Assert.Equal(("ev_charger", GuardTarget.Off, ReasonCode.GuardTierExceeded), (off.Device.Value, off.Target, off.Reason));
        Assert.True(s.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value < 0.9 * 25);
    }

    [Fact]
    public void Unknown_charger_state_hides_no_overload_at_reduce_next()
    {
        // The charger's current number goes unavailable (no car, 0 A): it is held off as unsafe,
        // but that hold relieves nothing. L1 at 30 A (1.20 × In) is reduce next cycle on the
        // measured current, so the heater goes at the 12:00:30 boundary, and the phase refuses
        // increases meanwhile. Counting the charger's 20 A as relief made it 10 A (may add): no
        // step until the budget, 135 s later, and an envelope inviting load onto an overloaded phase.
        var s = new GuardScenario(TestDevices.Document());
        s.SetState("ev_charger", null, invalid: DeviceStateInvalidReason.Unavailable);
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 30);

        Assert.Equal(30, s.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value, 9);
        Assert.False(s.PhaseStatus(Phase.L1).IncreasesAllowed);
        Assert.True(s.PhaseStatus(Phase.L1).EnvelopeToReserve!.Value.Value < 0);
        s.RunUntil(T0 + TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Sheds, i => i.Device.Value == "heater_l1");

        s.RunUntil(T0 + TimeSpan.FromSeconds(30));
        var (at, shed) = s.Timeline[^1];
        Assert.Equal((T0 + TimeSpan.FromSeconds(30), "heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded), (at, shed.Device.Value, shed.Target, shed.Reason));
    }

    [Fact]
    public void Unknown_charger_state_hides_no_overload_at_shed_one()
    {
        // The same at 33.75 A (1.35 × In): shed one immediately, so the heater goes with the very
        // sample that enters the band, not 22 s later through the budget.
        var s = new GuardScenario(TestDevices.Document());
        s.SetState("ev_charger", null, invalid: DeviceStateInvalidReason.Unavailable);
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(1));

        var entering = s.Sample(Phase.L1, 33.75);

        var shed = Assert.Single(entering.Intents);
        Assert.Equal(("heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded), (shed.Device.Value, shed.Target, shed.Reason));
    }

    [Fact]
    public void Unknown_heater_state_hides_no_overload_at_shed_one()
    {
        // A Modbus error leaves the L1 heater's state unknown: held off, no relief. 33 A
        // (1.32 × In) with the charger at 20 A sheds the next real candidate, the charger to its
        // minimum, at the entering sample; with 6.6 A of phantom relief the phase read 1.06 × In.
        var s = new GuardScenario(TestDevices.Document());
        s.SetState("heater_l1", null, invalid: DeviceStateInvalidReason.Unavailable);
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(1));

        var entering = s.Sample(Phase.L1, 33);

        var shed = Assert.Single(entering.Intents);
        Assert.Equal(("ev_charger", GuardTarget.AtMinimum, ReasonCode.GuardTierExceeded), (shed.Device.Value, shed.Target, shed.Reason));
    }

    [Fact]
    public void Charger_without_draw_relieves_nothing_when_held()
    {
        // A setpoint is a ceiling, not a draw: without its own draw signal a charger held at its
        // minimum relieves nothing (it may draw 0 A already). Above 1.45 × In with the heater on,
        // the relief is the heater's 6.6 A alone, so the phase still reads 30.9 A and refuses increases.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));

        s.Sample(Phase.L1, 37.5);

        Assert.Equal([("ev_charger", GuardTarget.AtMinimum), ("heater_l1", GuardTarget.Off)], s.Sheds.Select(i => (i.Device.Value, i.Target)).Order());
        Assert.Equal(37.5 - 6.6, s.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value, 9);
        Assert.False(s.PhaseStatus(Phase.L1).IncreasesAllowed);
    }

    [Fact]
    public void Idle_charger_at_20A_setpoint_still_produces_would_shed()
    {
        // Live finding on 0.3.0: the go-e sat at a 20 A setpoint while the car drew nothing; the
        // guard counted (setpoint − min) = 14 A as pending relief forever, so L1 measured 19 A but
        // read 5 A, and a later overload produced no would-shed. Relief never exceeds the measured
        // draw: with the charger's draw at 0 A the estimate stays at the measured current, and a
        // shed_one overload sheds the heater at its entering sample.
        var s = new GuardScenario(TestDevices.Document(TestDevices.ReferenceWithChargerDraw));
        s.SetDraw("ev_charger", Phase.L1, 0);
        s.SetState("heater_l1", new RelayApplied(false));
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 31);
        s.RunUntil(T0 + TimeSpan.FromSeconds(30));
        Assert.Equal([("ev_charger", GuardTarget.AtMinimum)], s.Sheds.Select(i => (i.Device.Value, i.Target)));

        s.Hold(Phase.L1, 19, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(2));
        Assert.Equal(19, s.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value, 9);

        s.RunFor(TimeSpan.FromSeconds(5));
        s.SetState("heater_l1", new RelayApplied(true));
        var entering = s.Sample(Phase.L1, 33.75);

        Assert.Contains(entering.Intents, i => i.Device.Value == "heater_l1" && i.Target == GuardTarget.Off && i.Reason == ReasonCode.GuardTierExceeded);
    }

    [Fact]
    public void Idle_charger_ranked_lowest_never_spends_the_shed_step()
    {
        // The charger is the cheapest load on L1 here, at a 20 A setpoint with the car drawing
        // nothing. Reducing it would relieve 0 A (relief never exceeds the measured draw), so the
        // one step the shed-one band allows at its entering sample goes to the heater, the next
        // candidate that really relieves L1; the 30 s band allowance is not spent on paper relief.
        var s = new GuardScenario(TestDevices.Document(TestDevices.ReferenceWithChargerDraw.Replace("value_per_kwh: 2.03", "value_per_kwh: 0.1", StringComparison.Ordinal)));
        s.SetDraw("ev_charger", Phase.L1, 0);
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));

        var entering = s.Sample(Phase.L1, 33.75);

        var shed = Assert.Single(entering.Intents);
        Assert.Equal(("heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded), (shed.Device.Value, shed.Target, shed.Reason));
    }

    [Fact]
    public void Hold_relief_is_visible_in_the_same_evaluation()
    {
        // The guard's own holds count in the estimate of the evaluation that made them (not one
        // step later through the effector's ledger): the entering 33.75 A sheds the heater, and the
        // published estimate of that very evaluation is already 33.75 − 6.6 A.
        var s = new GuardScenario(TestDevices.Document());
        s.SetState("ev_charger", new SetpointApplied(new Amperes(6)));
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(1));

        var entering = s.Sample(Phase.L1, 33.75);

        Assert.Contains(entering.Intents, i => i.Device.Value == "heater_l1");
        Assert.Equal(33.75 - 6.6, entering.Status.Phases.Single(p => p.Phase == Phase.L1).EffectiveCurrent!.Value.Value, 9);
        Assert.Equal(33.75 - 6.6, entering.Envelope.For(Switchboard.Domain.Dispatch.ConstraintKey.Main(Phase.L1))!.EstimatedCurrent!.Value.Value, 9);
    }

    [Fact]
    public void Unknown_direction_counts_as_import()
    {
        // Cautious mode (SIG-03): an unsigned current without its direction is counted as import.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);

        var evaluation = s.Sample(Phase.L1, 37.5, FlowDirection.Unknown);

        Assert.Equal(2, evaluation.Intents.Length);
        Assert.Equal(37.5, s.PhaseStatus(Phase.L1).Current!.Value.Value);
    }

    [Fact]
    public void Guard_never_starts_a_device()
    {
        // 04 §2: the guard only ever reduces. Through an overload, a recovery and a release, every
        // intent holds a stage off or at minimum, or lets go of a stage it held; none raises one.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 33);
        s.Hold(Phase.L1, 33, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(1));
        s.Hold(Phase.L1, 5, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(30));

        Assert.Contains(s.Intents, i => i.Target == GuardTarget.Released);
        var held = new HashSet<(DeviceName, int?)>();
        foreach (var intent in s.Intents)
        {
            Assert.Contains(intent.Target, new[] { GuardTarget.Off, GuardTarget.AtMinimum, GuardTarget.Released });
            if (intent.Target == GuardTarget.Released)
            {
                Assert.True(held.Remove((intent.Device, intent.Stage)), $"{intent.Device} released without being held.");
            }
            else
            {
                held.Add((intent.Device, intent.Stage));
            }
        }
    }

    [Fact]
    public void Most_severe_reaction_wins()
    {
        // At 1.30 × In from a full budget the 30 s band timer and the empty budget come due at the
        // same tick: one step from the timer, everything from the budget. Everything wins, and the
        // record names the budget (the tier only wins a tie of equal severity). The charger has a
        // stop actuator here, so "everything" can still take it off.
        var s = new GuardScenario(TestDevices.Document(StopCharger));
        s.Sample(Phase.L1, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 32.5);
        s.RunUntil(T0 + TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        var before = s.Sheds.Count();

        s.RunUntil(T0 + TimeSpan.FromSeconds(30));

        var last = s.Last.Intents.Single();
        Assert.Equal(("ev_charger", GuardTarget.Off, ReasonCode.GuardBudgetLow), (last.Device.Value, last.Target, last.Reason));
        Assert.Equal(before + 1, s.Sheds.Count());
    }

    /// <summary>The reference devices with a stop actuator on the charger (DISP-01): it can be switched off.</summary>
    private static readonly string StopCharger = TestDevices.Reference.ReplaceLineEndings("\n") + "\n    stop_actuator: switch.charger_enable";

    /// <summary>The reference devices with a select stop on the charger (0.7.1): "1" stops it, "0" lets it run.</summary>
    private static readonly string SelectCharger = TestDevices.Reference.ReplaceLineEndings("\n") + "\n    stop_actuator: select.charger_force\n    stop_option: \"1\"\n    resume_option: \"0\"";

    private const string SmallLoads = """
          - id: load_a
            type: onoff
            actuator: switch.load_a
            phase: L1
            rated_current_a: 1
            rated_power_w: 230
            value_per_kwh: 0.1
          - id: load_b
            type: onoff
            actuator: switch.load_b
            phase: L1
            rated_current_a: 1
            rated_power_w: 230
            value_per_kwh: 0.2
          - id: load_c
            type: onoff
            actuator: switch.load_c
            phase: L1
            rated_current_a: 1
            rated_power_w: 230
            value_per_kwh: 0.3
        """;
}
