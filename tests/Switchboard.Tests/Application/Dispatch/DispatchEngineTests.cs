using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The dispatch loop of 04 §3 on the reference installation (3 × 25 A, reserve 2.5 kW per phase:
/// the reserve threshold is 14.13 A), in shadow mode on the virtual clock: increases wait for
/// persistence_up_cycles boundaries, the guard's permission, a closed settling window and the
/// simultaneous-increase limit; decreases are unlimited; the guard always outranks dispatch; and the
/// shadow model neither repeats an intent nor charges a started device twice for itself.
/// </summary>
public sealed class DispatchEngineTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";

    [Fact]
    public void Increases_wait_for_persistence_up_confirmation()
    {
        // With 11 A free on L1 the heater would start at the first boundary; it starts at the sixth
        // (persistence_up_cycles = 6, asymmetric safety), not one boundary earlier.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1", "heater_l2", "heater_l3"]);
        s.PollUntil(T0 + (6 * Cycle) - TimeSpan.FromTicks(1), 3, 20, 20);
        Assert.Empty(s.DecisionsFor("heater_l1"));
        Assert.Equal(ReasonCode.AwaitingConfirmation, s.Dispatches[^1].Devices.Single(d => d.Device.Value == "heater_l1").Pending);

        s.PollUntil(T0 + (6 * Cycle), 3, 20, 20);

        var start = Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((T0 + (6 * Cycle), DecisionAction.SwitchOn, ReasonCode.HeadroomAvailable, DecisionOrigin.Dispatch), (start.At, start.Action, start.Reason, start.Origin));
        Assert.Equal((false, ReasonCode.ModeShadow), (start.Commanded, start.RefusedBy));
    }

    [Fact]
    public void Only_one_increase_in_flight_by_default()
    {
        // DISP-08: three heaters could all start at the sixth boundary; one does, and the next only
        // after its settling window closed (here the meter is silent, so the window stays open for
        // its 90 s timeout and the second heater waits with settling_window_open).
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1", "heater_l2", "heater_l3"]);
        s.PollUntil(T0 + (6 * Cycle), 3, 3, 3);
        Assert.Single(s.Decisions, d => d.Action == DecisionAction.SwitchOn);

        s.RunUntil(T0 + (7 * Cycle));
        Assert.Single(s.Decisions, d => d.Action == DecisionAction.SwitchOn);
        Assert.Contains(s.Dispatches[^1].Devices, d => d.Pending == ReasonCode.SettlingWindowOpen);

        s.PollUntil(T0 + (10 * Cycle), 3, 3, 3);
        Assert.Equal(3, s.Decisions.Count(d => d.Action == DecisionAction.SwitchOn));
    }

    [Fact]
    public void Decreases_ignore_the_increase_limit_and_open_windows()
    {
        // Two running heaters lose their headroom at once: both are reduced at the same boundary
        // (after persistence_down_cycles), although a settling window is open on another phase.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l3"]);
        s.PollUntil(T0 + (6 * Cycle), 9.6, 9.6, 3);
        Assert.Single(s.Decisions, d => d.Action == DecisionAction.SwitchOn && d.Device!.Value.Value == "heater_l3");

        // L3 stays silent, so the heater_l3 window stays open until its 90 s timeout.
        s.PollUntil(T0 + (8 * Cycle), 16.6, 16.6, null);
        Assert.NotNull(s.Ledger("heater_l3")!.Window);

        var stops = s.Decisions.Where(d => d.Action == DecisionAction.SwitchOff && d.Origin == DecisionOrigin.Dispatch).ToList();
        Assert.Equal(["heater_l1", "heater_l2"], stops.Select(d => d.Device!.Value.Value).Order());
        Assert.Single(stops.Select(d => d.At).Distinct());
        Assert.All(stops, d => Assert.Equal(ReasonCode.ReserveThreshold, d.Reason));
    }

    [Fact]
    public void Stepped_device_moves_one_stage_per_cycle()
    {
        // 07-PITFALLS (shutdown avalanche): a three-stage boiler wanted fully on climbs one stage per
        // cycle, and wanted off comes down one stage per cycle.
        var s = Scenario(DispatchDevices.Boiler, off: [], control: "  dispatch:\n    persistence_up_cycles: 1\n");
        s.SetState(DeviceName.Parse("boiler"), 1, new RelayApplied(false));
        s.SetState(DeviceName.Parse("boiler"), 2, new RelayApplied(false));
        s.SetState(DeviceName.Parse("boiler"), 3, new RelayApplied(false));
        s.PollUntil(T0 + (5 * Cycle), 1, 1, 1);

        var ons = s.DecisionsFor("boiler").Where(d => d.Action == DecisionAction.SwitchOn).ToList();
        Assert.Equal([1, 2, 3], ons.Select(d => d.Stage!.Value));
        Assert.Equal(3, ons.Select(d => d.At).Distinct().Count());
    }

    [Fact]
    public void Guard_stop_increases_blocks_every_start_on_that_phase()
    {
        // At 23 A (0.92 × In, the hold band) the guard allows no increase on L1: the heater there
        // waits with headroom_insufficient while the one on L2 starts.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1", "heater_l2"]);
        s.PollUntil(T0 + (8 * Cycle), 23, 3, 3);

        Assert.Empty(s.DecisionsFor("heater_l1"));
        Assert.Single(s.DecisionsFor("heater_l2"), d => d.Action == DecisionAction.SwitchOn);
        Assert.False(s.Envelope(Phase.L1).IncreasesAllowed);
    }

    [Fact]
    public void Guard_reduction_request_reduces_lowest_value_first()
    {
        // L1 above In − reserve for persistence_down_cycles boundaries: the guard asks for a
        // reduction; dispatch takes the cheaper device (the heater) and keeps the charger.
        var s = Scenario(DispatchDevices.HeaterAndCharger, off: []);
        s.SetState("ev_charger", new SetpointApplied(new Amperes(6)));
        s.SetDraw("ev_charger", Phase.L1, 6);
        s.PollUntil(T0 + (3 * Cycle), 15.6, 3, 3);

        Assert.Contains(s.Events, e => e.Action == DecisionAction.RequestReduction && e.Phase == Phase.L1);
        var stop = Assert.Single(s.Decisions, d => d.Origin == DecisionOrigin.Dispatch && d.Action is DecisionAction.SwitchOff or DecisionAction.SetSetpoint);
        Assert.Equal(("heater_l1", ReasonCode.ReserveThreshold), (stop.Device!.Value.Value, stop.Reason));
    }

    [Fact]
    public void Guard_hold_outranks_dispatch_target_in_the_same_step()
    {
        // The kill switch engages at the very boundary at which dispatch starts the heater: the
        // guard's hold wins in the same step; no start is recorded, only the hold.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1"]);
        s.PollUntil(T0 + (6 * Cycle) - TimeSpan.FromSeconds(1), 3, 3, 3);
        s.RunUntil(T0 + (6 * Cycle) - TimeSpan.FromTicks(1));
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Api);
        s.RunUntil(T0 + (6 * Cycle));

        Assert.DoesNotContain(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);
        Assert.Contains(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.KillSwitch && d.Origin == DecisionOrigin.Guard);
        Assert.False(DeviceLevelOn(s.Ledger("heater_l1")!.Target));
    }

    [Fact]
    public void Unset_target_follows_reported_and_records_nothing_at_start()
    {
        // At start nothing is targeted: every device follows its reported state, nothing counts in
        // flight and no decision is recorded until dispatch or protection changes something.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l2"]);
        s.SampleAll(9.6, 3, 9.6);
        s.RunUntil(T0 + TimeSpan.FromSeconds(29));

        Assert.Empty(s.Decisions);
        Assert.All(new[] { "heater_l1", "heater_l2", "heater_l3" }, d => Assert.Null(s.Ledger(d)?.Target));
        Assert.Equal(0, s.Envelope(Phase.L1).InFlight.Value);
        Assert.Equal(9.6, s.Envelope(Phase.L1).EstimatedCurrent!.Value.Value, 9);
    }

    [Fact]
    public void Invalid_phase_power_blocks_increases_not_decreases()
    {
        // Without valid dispatch data on L1 (power stale) the heater there does not start
        // (guard_stale_signal pending); the running heater on L2 is still reduced when L2 fills up.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1"]);
        s.InvalidatePower(Phase.L1);
        s.PollUntil(T0 + (8 * Cycle), 3, 16.6, 3);

        Assert.Empty(s.DecisionsFor("heater_l1"));
        Assert.Equal(ReasonCode.GuardStaleSignal, s.Dispatches[^1].Devices.Single(d => d.Device.Value == "heater_l1").Pending);
        Assert.Contains(s.DecisionsFor("heater_l2"), d => d.Action == DecisionAction.SwitchOff && d.Origin == DecisionOrigin.Dispatch);
    }

    [Fact]
    public void Started_device_is_not_charged_twice_for_its_own_in_flight_target()
    {
        // In shadow mode a started heater stays "started" (target on, reported off) and its 6.6 A
        // count in the estimate. The allocation adds it back once, so with the headroom unchanged it
        // is never stopped again: no start → stop → start chatter over min_on and beyond.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1"]);
        s.PollUntil(T0 + (6 * Cycle), 3, 3, 3);
        Assert.Single(s.DecisionsFor("heater_l1"));

        s.PollUntil(T0 + (6 * Cycle) + TimeSpan.FromMinutes(12), 3, 3, 3);

        Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal(3 + 6.6, s.Envelope(Phase.L1).EstimatedCurrent!.Value.Value, 9);
    }

    [Fact]
    public void Running_device_survives_a_dip_shorter_than_persistence_down_and_reduces_after_it()
    {
        // A running heater on L1 with 0.5 A to spare: a kettle-sized dip for one boundary leaves it
        // running (persistence_down_cycles = 2); the same dip for two boundaries stops it, with
        // reason reserve_threshold (the guard's reserve request is binding: reduce until E ≥ 0).
        var s = Scenario(DispatchDevices.Heaters, off: []);
        s.PollUntil(T0 + (2 * Cycle), 13.6, 3, 3);
        s.PollUntil(T0 + (3 * Cycle) + TimeSpan.FromSeconds(5), 15.6, 3, 3);
        s.PollUntil(T0 + (5 * Cycle), 13.6, 3, 3);
        Assert.Empty(s.DecisionsFor("heater_l1"));

        s.PollUntil(T0 + (7 * Cycle) + TimeSpan.FromSeconds(5), 15.6, 3, 3);

        var stop = Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((DecisionAction.SwitchOff, ReasonCode.ReserveThreshold), (stop.Action, stop.Reason));
    }

    [Fact]
    public void Lower_value_device_is_not_preempted_while_the_higher_value_increase_awaits_confirmation()
    {
        // The charger (worth more) wants 12 A, which only fits if the heater stops. While its
        // increase waits for confirmation the heater keeps running; at the boundary the increase
        // becomes eligible, the heater is stopped in that same step (value_rank_below_cutoff).
        var s = Scenario(DispatchDevices.HeaterAndCharger, off: []);
        s.SetState("ev_charger", new SetpointApplied(new Amperes(6)));
        s.SetDraw("ev_charger", Phase.L1, 6);
        s.PollUntil(T0 + (6 * Cycle) - TimeSpan.FromSeconds(1), 12.6, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 6));
        Assert.DoesNotContain(s.Decisions, d => d.Origin == DecisionOrigin.Dispatch);

        s.PollUntil(T0 + (6 * Cycle), 12.6, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 6));

        var changes = s.Decisions.Where(d => d.Origin == DecisionOrigin.Dispatch).ToList();
        Assert.Equal([("ev_charger", DecisionAction.SetSetpoint), ("heater_l1", DecisionAction.SwitchOff)], changes.Select(d => (d.Device!.Value.Value, d.Action)).Order());
        Assert.Single(changes.Select(d => d.At).Distinct());
        Assert.Equal(ReasonCode.ValueRankBelowCutoff, changes.Single(d => d.Device!.Value.Value == "heater_l1").Reason);
        Assert.Equal(12, changes.Single(d => d.Device!.Value.Value == "ev_charger").Target!.Setpoint!.Value.Value);
    }

    [Fact]
    public void Guard_sheds_a_would_started_device_before_a_higher_value_running_one()
    {
        // In shadow mode the started heater (target on, reported off) is in the estimate; when a
        // kettle pushes L1 into the shed-one band the guard sheds that heater, the cheapest device
        // as the estimate sees it, not the charger the house really runs.
        var s = Scenario(DispatchDevices.HeaterAndPool, off: ["heater_l1"]);
        s.PollUntil(T0 + (6 * Cycle), 6, 3, 3);
        Assert.Single(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);
        s.RunFor(TimeSpan.FromSeconds(5));

        s.Sample(Phase.L1, 27.5);

        var shed = Assert.Single(s.Last.Intents);
        Assert.Equal(("heater_l1", GuardTarget.Off), (shed.Device.Value, shed.Target));
        Assert.DoesNotContain(s.Intents, i => i.Device.Value == "pool");
    }

    [Fact]
    public void Stale_draw_blocks_that_devices_increases()
    {
        // The charger's own draw signal is stale: it is no longer trusted for relief or for an
        // increase (guard_stale_signal pending), though nothing holds it off.
        var s = Scenario(DispatchDevices.HeaterAndCharger, off: ["heater_l1"]);
        s.SetState("ev_charger", new SetpointApplied(new Amperes(6)));
        s.SetDraw("ev_charger", Phase.L1, null);
        s.PollUntil(T0 + (8 * Cycle), 2, 3, 3);

        Assert.Empty(s.DecisionsFor("ev_charger"));
        Assert.Equal(ReasonCode.GuardStaleSignal, s.Dispatches[^1].Devices.Single(d => d.Device.Value == "ev_charger").Pending);
        Assert.Equal(Switchboard.Application.Guard.DeviceGuardState.Allowed, s.DeviceStatus("ev_charger").GuardState);
    }

    [Fact]
    public void Dispatch_never_targets_storage_discharge()
    {
        // With no headroom at all a charging battery is brought to zero charge and stays there: no
        // negative setpoint is ever targeted (discharge is a later version).
        var s = Scenario(DispatchDevices.Battery, off: []);
        s.SetState("battery", new SetpointApplied(new Amperes(8)));
        s.PollUntil(T0 + (10 * Cycle), 20, 3, 3);

        var targets = s.DecisionsFor("battery").Select(d => d.Target).ToList();
        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.True(t!.Setpoint!.Value.Value >= 0));
        Assert.Equal(0, s.Ledger("battery")!.Target!.Setpoint!.Value.Value);
    }

    [Fact]
    public void Discharging_battery_is_left_alone_when_no_charge_is_planned()
    {
        // A battery its own EMS discharges at 10 A has no headroom to charge. Stopping the discharge
        // would raise L1 by 10 A with no headroom, guard or settling check: dispatch never does it.
        // It wants nothing, records nothing, and the battery's target stays unset (it follows the
        // reported state).
        var s = Scenario(DispatchDevices.Battery, off: []);
        s.SetState("battery", new SetpointApplied(new Amperes(-10)));
        s.PollUntil(T0 + (10 * Cycle), 20, 3, 3);

        Assert.Empty(s.DecisionsFor("battery"));
        Assert.Null(s.Ledger("battery")!.Target);
    }

    [Fact]
    public void Leaving_a_discharge_is_budgeted_and_estimated_including_the_discharge()
    {
        // L1 measures 0.5 A with the battery discharging 10 A into it. Charging at x A raises L1 by
        // 10 + x A, so only 2 A fit under the reserve threshold (14.13 A, start hysteresis 1.3 A);
        // budgeting the charge ceiling alone would plan 12 A and a real step of 22 A. The guard's
        // estimate counts the whole step in flight.
        var s = Scenario(DispatchDevices.Battery, off: []);
        s.SetState("battery", new SetpointApplied(new Amperes(-10)));
        s.PollUntil(T0 + (8 * Cycle), 0.5, 3, 3);

        var charge = Assert.Single(s.DecisionsFor("battery"));
        Assert.Equal((DecisionAction.SetSetpoint, ReasonCode.HeadroomAvailable, 2.0), (charge.Action, charge.Reason, charge.Target!.Setpoint!.Value.Value));
        Assert.Equal(12.0, s.Envelope(Phase.L1).InFlight.Value, 9);
        Assert.Equal(12.5, s.Envelope(Phase.L1).EstimatedCurrent!.Value.Value, 9);
    }

    [Fact]
    public void A_paused_charger_below_its_floor_is_not_raised_to_it_without_the_gates()
    {
        // The charger reports 0 A (paused below its 6 A floor) while L1 is above the reserve
        // threshold: the guard allows no increase. Snapping the reported 0 A onto the 6 A grid
        // level is no reason to command 6 A: no set_setpoint may be recorded.
        var s = Scenario(DispatchDevices.HeaterAndCharger, off: ["heater_l1"]);
        s.Charger(0, 0);
        s.PollUntil(T0 + (8 * Cycle), 16, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 0));

        Assert.False(s.Envelope(Phase.L1).IncreasesAllowed);
        Assert.DoesNotContain(s.DecisionsFor("ev_charger"), d => d.Action == DecisionAction.SetSetpoint);
        Assert.Null(s.Ledger("ev_charger")!.Target);
    }

    [Fact]
    public void Restores_after_a_guard_hold_are_recorded_starts_in_start_order()
    {
        // DISP-03 in shadow mode. The three heaters run in reality; the meter goes silent and the
        // guard holds them all off (the would-be world is off). Once the data is valid again the
        // holds are released together; the held level stays the intended one and dispatch restores
        // the heaters as ordinary recorded would-starts, one settling window at a time (DISP-08), in
        // start order. Nothing may be recorded as a dispatch stop "because headroom is available",
        // and no restore may happen silently.
        var s = Scenario(DispatchDevices.Heaters, off: []);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 9.6, 9.6, 9.6);
        foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
        {
            s.Invalidate(phase, ProtectionInvalidReason.CurrentStale, evaluate: false);
        }

        s.Evaluate();
        Assert.All(new[] { "heater_l1", "heater_l2", "heater_l3" }, h => Assert.False(s.Ledger(h)!.Target!.Relays[0]));
        s.RunUntil(T0 + TimeSpan.FromMinutes(2));
        var released = s.Decisions.Count;

        s.PollUntil(T0 + TimeSpan.FromMinutes(10), 9.6, 9.6, 9.6);

        var after = s.Decisions.Skip(released).Where(d => d.Scope == DecisionScope.Device).ToList();
        Assert.Equal(3, after.Count(d => d.Action == DecisionAction.Release));
        Assert.DoesNotContain(after, d => d.Origin == DecisionOrigin.Dispatch && d.Action == DecisionAction.SwitchOff);
        var starts = after.Where(d => d.Action == DecisionAction.SwitchOn).ToList();
        Assert.Equal(3, starts.Count);
        Assert.All(starts, d => Assert.Equal((DecisionOrigin.Dispatch, ReasonCode.HeadroomAvailable), (d.Origin!.Value, d.Reason)));
        Assert.Equal(3, starts.Select(d => d.At).Distinct().Count());
        var ranks = s.Dispatches[^1].Devices.ToDictionary(d => d.Device.Value, d => d.StartRank);
        Assert.Equal(starts.Select(d => d.Device!.Value.Value).OrderBy(d => ranks[d]), starts.Select(d => d.Device!.Value.Value));
        Assert.All(new[] { "heater_l1", "heater_l2", "heater_l3" }, h => Assert.True(s.Ledger(h)!.Target!.Relays[0]));
    }

    [Fact]
    public void A_stepped_device_redeclared_with_another_stage_count_starts_unset_before_dispatch_reads_it()
    {
        // The boiler is (virtually) at stage 1 of 3. The user redeclares it with two stages and the
        // reload lands on a cycle boundary: the old three-relay target means nothing for the new
        // declaration and must be gone before the guard and dispatch read the ledger. Dispatch keeps
        // running, and never plans a level of the old declaration.
        var s = Scenario(DispatchDevices.Boiler, off: ["boiler"], control: OneBoundary);
        s.PollUntil(T0 + Cycle, 3, 3, 3);
        Assert.Equal(3, s.Ledger("boiler")!.Target!.Relays.Length);

        var twoStages = DispatchDevices.Boiler.ReplaceLineEndings("\n");
        twoStages = twoStages[..twoStages.LastIndexOf("      - { actuator: switch.boiler_3", StringComparison.Ordinal)];
        s.RunUntil(T0 + (2 * Cycle) - TimeSpan.FromSeconds(1));
        s.Reconfigure(TestDevices.Document(twoStages, OneBoundary));
        s.Time.Advance(TimeSpan.FromSeconds(1));
        s.SampleAll(3, 3, 3);
        Assert.True(s.Last.AtBoundary);
        s.PollUntil(T0 + (4 * Cycle), 3, 3, 3);

        Assert.All(s.Dispatches.Where(d => d.At >= T0 + (2 * Cycle)).SelectMany(d => d.Wishes), w => Assert.Equal(2, w.Level.Relays.Length));
        Assert.True(s.Ledger("boiler")!.Target is null || s.Ledger("boiler")!.Target!.Relays.Length == 2);
    }

    [Fact]
    public void Holding_one_increase_back_never_lets_another_through_beyond_the_limit()
    {
        // DISP-08 with an override, found on the dev-replay host: after the kill switch, the forced
        // L1 heater, the L2 heater and the charger (worth most) all want to go up at the same
        // boundary. The first count gives the one increase to the L2 heater and holds the forced
        // heater back, which frees the 7 A it had reserved on L1; the redone walk then offers that
        // to the charger. That is a second increase in one window: it must wait too.
        var s = Scenario(HeatersAndCharger, off: ["heater_l1", "heater_l2", "ev_charger"]);
        s.Charger(6, 0);
        void Refresh() => s.SetDraw("ev_charger", Phase.L1, 0);
        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 2.6, 1.8, 2, Refresh);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 2.6, 1.8, 2, Refresh);
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Api);
        s.PollUntil(T0 + TimeSpan.FromMinutes(8), 2.6, 1.8, 2, Refresh);
        s.KillSwitch = new KillSwitchStatus(false, s.Now, KillSwitchSource.Api);

        s.PollUntil(T0 + TimeSpan.FromMinutes(20), 2.6, 1.8, 2, Refresh);

        var increases = s.Decisions.Where(d => d.Origin is DecisionOrigin.Dispatch or DecisionOrigin.Override && d.Inputs.Dispatch?.Settling == SettlingState.Open
            && (d.Action == DecisionAction.SwitchOn || (d.Action == DecisionAction.SetSetpoint && d.Reason == ReasonCode.HeadroomAvailable))).ToList();
        Assert.Contains(increases, d => d.Device!.Value.Value == "heater_l2" && d.At > T0 + TimeSpan.FromMinutes(8));
        Assert.All(increases.GroupBy(d => d.At), g => Assert.Single(g));
    }

    [Fact]
    public void A_forced_on_device_keeps_its_headroom_reserved_after_its_lockout()
    {
        // The forced L1 heater runs (intended); once its min_on has expired it could in principle be
        // switched off, but the override pins it on. Its 7 A must stay reserved: the charger, worth
        // more and first in start order, may not be raised into headroom the heater keeps using
        // (L1 would be planned past the reserve threshold, then lowered again: chatter).
        var s = Scenario(HeatersAndCharger, off: ["heater_l1", "heater_l2", "ev_charger"]);
        s.Charger(6, 0);
        void Refresh() => s.SetDraw("ev_charger", Phase.L1, 0);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 2.6, 1.8, 2, Refresh);
        Assert.True(s.Ledger("heater_l1")!.Target!.Relays[0]);

        s.PollUntil(T0 + TimeSpan.FromMinutes(20), 2.6, 1.8, 2, Refresh);

        Assert.DoesNotContain(s.DecisionsFor("ev_charger"), d => d.Action == DecisionAction.SetSetpoint);
        Assert.Equal(6, s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 6);
        Assert.True(s.Envelope(Phase.L1).Available!.Value.Value >= 0);
    }

    /// <summary>The charger and the L1 heater on L1 (the charger worth more), and a second heater on L2.</summary>
    private static string HeatersAndCharger =>
        DispatchDevices.HeaterAndCharger.ReplaceLineEndings("\n").TrimEnd('\n') + "\n" + """
          - id: heater_l2
            type: onoff
            actuator: switch.heater_2
            phase: L2
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """.ReplaceLineEndings("\n");

    [Fact]
    public void Rotation_starts_the_element_on_the_phase_with_most_headroom()
    {
        // DISP-10: three identical elements; L3 has the most headroom, so its element starts first.
        var s = Scenario(DispatchDevices.RotatedHeaters, off: ["heater_l1", "heater_l2", "heater_l3"]);
        s.PollUntil(T0 + (6 * Cycle), 4, 3, 1);

        var start = Assert.Single(s.Decisions, d => d.Action == DecisionAction.SwitchOn);
        Assert.Equal("heater_l3", start.Device!.Value.Value);
        Assert.Equal([Phase.L3, Phase.L2, Phase.L1], s.Dispatches[^1].Ranking);
    }

    [Fact]
    public void No_identical_intent_is_ever_recorded_twice_in_a_row()
    {
        // MODE-03 and the shadow model: an hour of steady conditions with one start writes one line.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1"]);
        s.PollUntil(T0 + TimeSpan.FromHours(1), 3, 3, 3);

        var lines = s.DecisionsFor("heater_l1").ToList();
        Assert.Single(lines);
        foreach (var device in s.Decisions.Where(d => d.Scope == DecisionScope.Device).GroupBy(d => d.Device))
        {
            var list = device.ToList();
            for (var i = 1; i < list.Count; i++)
            {
                Assert.False(list[i].Action == list[i - 1].Action && Equals(list[i].Target, list[i - 1].Target) && list[i].Reason == list[i - 1].Reason, $"{device.Key} repeated {list[i].Action}");
            }
        }
    }

    [Fact]
    public void Shadow_target_is_not_undone_by_another_controller()
    {
        // The house's own controller switches the heater on and off again after Switchboard would
        // have started it: in shadow mode the would-be world keeps its target (no second identical
        // "would switch on" because reality differs), while a device without a target keeps
        // following its reported state.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1", "heater_l2"], control: OneBoundary);
        s.PollUntil(T0 + Cycle, 3, 13, 3);
        Assert.Single(s.DecisionsFor("heater_l1"));
        s.SetState("heater_l1", new RelayApplied(true));
        s.PollUntil(T0 + (2 * Cycle), 9.6, 13, 3);
        s.SetState("heater_l1", new RelayApplied(false));
        s.SetState("heater_l2", new RelayApplied(true));

        s.PollUntil(T0 + (10 * Cycle), 3, 13, 3);

        Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.True(s.Ledger("heater_l1")!.Target!.Relays[0]);
        Assert.Equal(6.6, s.Envelope(Phase.L1).InFlight.Value, 9);
        Assert.Null(s.Ledger("heater_l2")!.Target);
    }

    [Fact]
    public void A_lockout_episode_is_deferred_once_even_when_the_wish_comes_and_goes()
    {
        // A cycling appliance on the phase makes dispatch want the started heater off, then not, then
        // off again, all within its min_on: one Deferred line for that lockout, not one per wish.
        var s = Scenario(DispatchDevices.Heaters, off: ["heater_l1"], control: OneBoundary + "    persistence_down_cycles: 1\n");
        s.PollUntil(T0 + Cycle, 3, 3, 3);
        s.PollUntil(T0 + (2 * Cycle), 12, 3, 3);
        s.PollUntil(T0 + (3 * Cycle), 3, 3, 3);
        s.PollUntil(T0 + (4 * Cycle), 12, 3, 3);

        Assert.Single(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.Deferred);
    }

    private static bool DeviceLevelOn(DeviceLevel? level) => level is { } l && l.StagesOn > 0;

    private static GuardScenario Scenario(string devices, string[] off, string control = "") =>
        DispatchScenario.Create(devices, off, control);
}
