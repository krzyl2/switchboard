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
        // Above 1.45 × In: every controlled device on the phase off in one evaluation, the
        // modulating charger straight to off (not to its minimum).
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));

        var evaluation = s.Sample(Phase.L1, 37.5);

        Assert.Equal(
            [("ev_charger", GuardTarget.Off), ("heater_l1", GuardTarget.Off)],
            evaluation.Intents.Select(i => (i.Device.Value, i.Target)).Order());
        Assert.All(evaluation.Intents, i => Assert.Equal(ReasonCode.GuardTierExceeded, i.Reason));
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
        // rise) the next step is taken at that tick, then one per new sample.
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
        s.Sample(Phase.L1, 27);
        Assert.Equal(("ev_charger", GuardTarget.Off), (s.Sheds.Last().Device.Value, s.Sheds.Last().Target));
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
        // The heater and the charger's reduction are already decided (20.6 A of relief, all of it
        // still "on" in shadow). 37.5 A measured is the open band; the effective 16.9 A would be
        // may_add. The open band follows the measured current, so the charger goes off by the
        // tier (a charger at 20 A with no car would otherwise hide a real overload).
        var s = new GuardScenario(TestDevices.Document());
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
    public void Charger_held_off_relieves_only_down_to_its_minimum()
    {
        // Before Stage 3 a modulating device has no stop path (CanSwitchOff false): an Off hold
        // can at most bring it to its 6 A minimum. Above 1.45 × In with the heater on and the
        // charger at 20 A, everything on L1 goes off; the relief is 6.6 + 14 A, not 6.6 + 20 A,
        // so the phase still reads 16.9 A effective and refuses increases.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));

        s.Sample(Phase.L1, 37.5);

        Assert.Equal(2, s.Sheds.Count(i => i.Target == GuardTarget.Off));
        Assert.Equal(37.5 - 6.6 - 14, s.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value, 9);
        Assert.False(s.PhaseStatus(Phase.L1).IncreasesAllowed);
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
        // record names the budget (the tier only wins a tie of equal severity).
        var s = new GuardScenario(TestDevices.Document());
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
