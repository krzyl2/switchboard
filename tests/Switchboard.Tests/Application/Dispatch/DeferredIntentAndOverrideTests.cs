using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// DISP-09 (minimum on and off times with a deferred intent applied when the lockout expires,
/// protection still outranking lockouts) and SAFE-04 (manual overrides outrank dispatch, never
/// protection), in shadow mode on the fake clock.
/// </summary>
public sealed class DeferredIntentAndOverrideTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";

    [Fact]
    public void Start_refused_by_min_off_recorded_once_and_applied_at_the_first_boundary_after_expiry()
    {
        // The heater's lockouts start at the first evaluation (12:00:15, a restart): min_off runs
        // until 12:01:45. Dispatch wants it on from the 12:00:30 boundary: one Deferred
        // (lockout_min_off) is recorded, nothing at the next boundaries. At 12:01:45, between
        // boundaries, the deferral is reconciled but an increase is not made there: it waits for
        // the 12:02:00 boundary, where dispatch wants it afresh with every gate re-checked, and is
        // applied at once (the lockout has expired), still carrying its deferral.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], OneBoundary, settledLockouts: false, start: T0 + TimeSpan.FromSeconds(5));
        s.PollUntil(T0 + TimeSpan.FromSeconds(100), 3, 3, 3);

        var deferred = Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((DecisionAction.Deferred, ReasonCode.LockoutMinOff, T0 + Cycle), (deferred.Action, deferred.RefusedBy!.Value, deferred.At));
        Assert.Equal(T0 + TimeSpan.FromSeconds(105), s.Ledger("heater_l1")!.Deferred!.DueAt);

        s.PollUntil(T0 + TimeSpan.FromSeconds(119), 3, 3, 3);
        Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((ReasonCode.LockoutMinOff, (DateTimeOffset?)null), (s.Ledger("heater_l1")!.Deferred!.RefusedBy, s.Ledger("heater_l1")!.Deferred!.DueAt));

        s.PollUntil(T0 + (4 * Cycle), 3, 3, 3);

        var start = s.DecisionsFor("heater_l1").Last();
        Assert.Equal((DecisionAction.SwitchOn, T0 + (4 * Cycle)), (start.Action, start.At));
        Assert.Equal(T0 + Cycle, start.Inputs.Dispatch!.DeferredSince);
        Assert.Null(s.Ledger("heater_l1")!.Deferred);
    }

    [Fact]
    public void Deferred_start_is_not_made_at_expiry_after_the_guard_stopped_increases()
    {
        // The same deferred start (min_off until 12:01:45). From 12:01:40 a kettle holds L1 in the
        // hold band (23 A, 0.92 x In: add nothing): the guard allows no increase there any more.
        // The lockout expiring must not start the heater on the last boundary's wish, and the next
        // boundary, which re-checks every gate, does not want it either: dispatch can never raise a
        // phase above what the guard allows.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], OneBoundary, settledLockouts: false, start: T0 + TimeSpan.FromSeconds(5));
        s.PollUntil(T0 + TimeSpan.FromSeconds(100), 3, 3, 3);
        Assert.Equal(DecisionAction.Deferred, Assert.Single(s.DecisionsFor("heater_l1")).Action);

        s.PollUntil(T0 + TimeSpan.FromSeconds(180), 23, 3, 3);

        Assert.DoesNotContain(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);
        Assert.False(s.Envelope(Phase.L1).IncreasesAllowed);
        Assert.False(s.Ledger("heater_l1")!.Target?.Relays[0] ?? false);
    }

    [Fact]
    public void Deferred_stop_is_applied_at_the_lockout_expiry_between_boundaries()
    {
        // DISP-09 reconciliation for a decrease: another controller switched the heater on at
        // 12:01:05 (its contacts moved, so min_on runs until 12:07:05). L1 fills up and dispatch
        // wants it off; the stop is deferred (lockout_min_on) and applied exactly at 12:07:05,
        // between boundaries: a decrease needs no gate.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 100\n");
        s.PollUntil(T0 + TimeSpan.FromSeconds(65), 3, 3, 3);
        s.SetState("heater_l1", new RelayApplied(true));
        s.Evaluate();
        s.PollUntil(T0 + TimeSpan.FromMinutes(7), 16.6, 3, 3);
        var deferred = Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((DecisionAction.Deferred, ReasonCode.LockoutMinOn, T0 + TimeSpan.FromSeconds(425)), (deferred.Action, deferred.RefusedBy!.Value, s.Ledger("heater_l1")!.Deferred!.DueAt!.Value));

        s.PollUntil(T0 + TimeSpan.FromSeconds(426), 16.6, 3, 3);

        var stop = s.DecisionsFor("heater_l1").Last();
        Assert.Equal((DecisionAction.SwitchOff, T0 + TimeSpan.FromSeconds(425)), (stop.Action, stop.At));
        Assert.Equal(deferred.At, stop.Inputs.Dispatch!.DeferredSince);
    }

    [Fact]
    public void Deferred_intent_dropped_when_headroom_disappears_first()
    {
        // The same deferred start; before the lockout expires L1 fills up, the next boundary no
        // longer wants the heater, and the deferral is dropped silently: nothing at its expiry.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], OneBoundary, settledLockouts: false, start: T0 + TimeSpan.FromSeconds(5));
        s.PollUntil(T0 + TimeSpan.FromSeconds(70), 3, 3, 3);
        Assert.NotNull(s.Ledger("heater_l1")!.Deferred);

        s.PollUntil(T0 + TimeSpan.FromSeconds(120), 12, 3, 3);

        Assert.Null(s.Ledger("heater_l1")!.Deferred);
        Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Null(s.Ledger("heater_l1")!.Target);
    }

    [Fact]
    public void Guard_shed_ignores_min_on_but_dispatch_stop_waits()
    {
        // The heater was (virtually) started at 12:00:30; its target min_on runs 6 min. When L1
        // fills up, dispatch's stop waits (Deferred, lockout_min_on); an overload in the shed-one
        // band sheds it at once, recording the min_on it outranked.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], OneBoundary);
        s.PollUntil(T0 + Cycle, 3, 3, 3);
        Assert.Single(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);

        s.PollUntil(T0 + (4 * Cycle), 10, 3, 3);
        var deferred = Assert.Single(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.Deferred);
        Assert.Equal(ReasonCode.LockoutMinOn, deferred.RefusedBy);

        s.RunFor(TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 27);

        var shed = s.DecisionsFor("heater_l1").Last();
        Assert.Equal((DecisionAction.SwitchOff, DecisionOrigin.Guard, ReasonCode.LockoutMinOn), (shed.Action, shed.Origin, shed.Overridden));
    }

    [Fact]
    public void External_transition_restarts_target_lockouts_because_contacts_switched()
    {
        // Another controller switches the heater on at 12:01:00: its contacts moved, so its target
        // min_on starts then, and a stop dispatch wants right after waits for it.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 100\n");
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);
        s.SetState("heater_l1", new RelayApplied(true));
        s.Evaluate();
        Assert.Equal(TimeSpan.FromMinutes(6), s.Lockouts.MinOnRemaining(DeviceName.Parse("heater_l1"), 1, s.Now, LockoutTimeline.Target));

        s.PollUntil(T0 + TimeSpan.FromMinutes(3), 16.6, 3, 3);

        var deferred = Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((DecisionAction.Deferred, ReasonCode.LockoutMinOn), (deferred.Action, deferred.RefusedBy!.Value));
    }

    [Fact]
    public void Lockouts_restart_at_process_start_on_both_timelines()
    {
        // SAFE-07: after a restart both the protection timeline and the dispatch (target) timeline
        // count from the first evaluation, never from a persisted expiry.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], settledLockouts: false);
        s.SampleAll(3, 3, 3);
        var heater = DeviceName.Parse("heater_l2");

        Assert.Equal(TimeSpan.FromMinutes(6), s.Lockouts.MinOnRemaining(heater, 1, s.Now));
        Assert.Equal(TimeSpan.FromMinutes(6), s.Lockouts.MinOnRemaining(heater, 1, s.Now, LockoutTimeline.Target));
        Assert.Equal(TimeSpan.FromSeconds(90), s.Lockouts.MinOffRemaining(DeviceName.Parse("heater_l1"), 1, s.Now, LockoutTimeline.Target));
    }

    [Fact]
    public void Force_on_outranks_headroom_and_rank()
    {
        // L1 at 13 A: dispatch would never start the heater (1.1 A to the reserve threshold), but a
        // manual force-on may use the reserve: the estimate stays in the may-add band and 6.6 A fit
        // under the breaker allowance (22.5 − 13 A). It starts at the next boundary, manual_override.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"]);
        s.PollUntil(T0 + TimeSpan.FromSeconds(40), 13, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOn);

        s.PollUntil(T0 + (2 * Cycle), 13, 3, 3);

        var lines = s.DecisionsFor("heater_l1").ToList();
        Assert.Equal([DecisionAction.OverrideSet, DecisionAction.SwitchOn], lines.Select(d => d.Action));
        Assert.Equal((OverrideMode.ForceOn, ReasonCode.ManualOverride), (lines[0].Inputs.Dispatch!.Override, lines[0].Reason));
        var start = lines[1];
        Assert.Equal((DecisionAction.SwitchOn, ReasonCode.ManualOverride, DecisionOrigin.Override, T0 + (2 * Cycle)), (start.Action, start.Reason, start.Origin, start.At));
    }

    [Fact]
    public void Force_off_applies_at_once_and_stops_only_through_lockouts()
    {
        var s = DispatchScenario.Create(DispatchDevices.Heaters, []);
        s.PollUntil(T0 + TimeSpan.FromSeconds(40), 9.6, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOff);
        s.Evaluate(GuardTrigger.Override);

        var stop = Assert.Single(s.DecisionsFor("heater_l1"), d => d.Action != DecisionAction.OverrideSet);
        Assert.Equal((DecisionAction.SwitchOff, ReasonCode.ManualOverride, T0 + TimeSpan.FromSeconds(40)), (stop.Action, stop.Reason, stop.At));
    }

    [Fact]
    public void Guard_sheds_forced_on_device_and_records_manual_override()
    {
        // Protection outranks an override: the forced heater is shed by the open band, and the
        // record names the override it outranked.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, []);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + TimeSpan.FromSeconds(40), 9.6, 3, 3);

        s.Sample(Phase.L1, 37.5);

        var shed = s.DecisionsFor("heater_l1").Last();
        Assert.Equal((DecisionAction.SwitchOff, DecisionOrigin.Guard, ReasonCode.ManualOverride), (shed.Action, shed.Origin, shed.Overridden));
    }

    [Fact]
    public void Force_on_with_stale_data_stays_off_until_release_persistence()
    {
        // The forced heater's phase has no valid current: it is held off (safe state), and the
        // override only acts once the data is valid again and the hold was released after
        // persistence_up_cycles good boundaries.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"]);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.SampleAll(3, 3, 3);
        s.Invalidate(Phase.L1, ProtectionInvalidReason.CurrentStale);
        s.RunUntil(T0 + TimeSpan.FromMinutes(2));
        Assert.DoesNotContain(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);

        s.PollUntil(T0 + TimeSpan.FromMinutes(2) + (6 * Cycle) - TimeSpan.FromSeconds(1), 3, 3, 3);
        Assert.DoesNotContain(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);

        s.PollUntil(T0 + TimeSpan.FromMinutes(2) + (8 * Cycle), 3, 3, 3);
        Assert.Contains(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn && d.Reason == ReasonCode.ManualOverride);
    }

    [Fact]
    public void Forced_on_device_shed_by_guard_is_not_restarted_into_an_overload()
    {
        // Active semantics: the forced charger (20 A) is reduced by the guard to its 6 A minimum.
        // Once the guard lets go, the override wants 20 A again, but 14 A more do not fit under the
        // breaker allowance (22.5 − 12 A): it is recorded once as deferred (headroom_insufficient)
        // and never re-applied into an overload.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndCharger, ["heater_l1"], mode: DispatchScenario.Active, port: port);
        s.Charger(20, 20);
        s.Override("ev_charger", OverrideMode.ForceOn);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 26, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 20));
        Assert.Contains(port.Commands, c => c == "ev_charger setpoint 6");
        s.Charger(6, 6);

        s.PollUntil(T0 + TimeSpan.FromMinutes(8), 12, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 6));

        var lines = s.DecisionsFor("ev_charger").ToList();
        Assert.Contains(lines, d => d.Action == DecisionAction.Release);
        Assert.DoesNotContain(port.Commands, c => c == "ev_charger setpoint 20");
        var deferred = Assert.Single(lines, d => d.Action == DecisionAction.Deferred);
        Assert.Equal((ReasonCode.ManualOverride, ReasonCode.HeadroomInsufficient), (deferred.Reason, deferred.RefusedBy!.Value));

        // The deferral waits for the forced 20 A (what the API, the MQTT entity and the SPA show),
        // not for the 6 A the charger is already at; nothing is applied meanwhile.
        Assert.Equal(20, s.Ledger("ev_charger")!.Deferred!.Target.Setpoint!.Value.Value);
        Assert.Equal(6, s.Ledger("ev_charger")!.Target!.Setpoint!.Value.Value);
    }

    [Fact]
    public void Override_for_undeclared_device_is_refused()
    {
        // SAFE-05: only declared devices are ever addressed, through an override too.
        var holder = Holder(TestDevices.Document());
        var overrides = new DeviceOverrides(new GuardInbox(), holder, new SignalClock(new SteppableTime(T0)), NullLogger<DeviceOverrides>.Instance);

        Assert.Equal(OverrideResult.UnknownDevice, overrides.Set(DeviceName.Parse("garage_door"), OverrideMode.ForceOn, OverrideSource.Api));
        Assert.Equal(OverrideResult.Applied, overrides.Set(DeviceName.Parse("heater_l1"), OverrideMode.ForceOn, OverrideSource.Api));
        Assert.Equal(OverrideResult.Unchanged, overrides.Set(DeviceName.Parse("heater_l1"), OverrideMode.ForceOn, OverrideSource.Mqtt));
        Assert.DoesNotContain(overrides.Current.Devices.Keys, k => k.Value == "garage_door");

        var invalid = new DeviceOverrides(new GuardInbox(), Holder("schema_version: 2\n"), new SignalClock(new SteppableTime(T0)), NullLogger<DeviceOverrides>.Instance);
        Assert.Equal(OverrideResult.NoConfiguration, invalid.Set(DeviceName.Parse("heater_l1"), OverrideMode.ForceOn, OverrideSource.Api));
    }

    [Fact]
    public void Override_survives_restart()
    {
        // The override is written to the runtime state at once and restored before the first step
        // of the next run (as restored), so a forced-off device stays forced off across a restart.
        var holder = Holder(TestDevices.Document());
        var clock = new SignalClock(new SteppableTime(T0));
        var overrides = new DeviceOverrides(new GuardInbox(), holder, clock, NullLogger<DeviceOverrides>.Instance);
        var store = new MemoryStateStore();
        var coordinator = new RuntimeStateCoordinator(store, new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.Fresh, null), () => null, Microsoft.Extensions.Options.Options.Create(TestDecisions.Options()), clock.Time, NullLogger<RuntimeStateCoordinator>.Instance);
        coordinator.Attach(overrides);

        overrides.Set(DeviceName.Parse("heater_l2"), OverrideMode.ForceOff, OverrideSource.Mqtt);

        var saved = coordinator.Current.Overrides;
        var restarted = new DeviceOverrides(new GuardInbox(), holder, clock, NullLogger<DeviceOverrides>.Instance);
        restarted.Restore(saved);
        var restored = restarted.Current.For(DeviceName.Parse("heater_l2"));
        Assert.Equal((OverrideMode.ForceOff, OverrideSource.Restored), (restored.Mode, restored.Source!.Value));
    }

    private static SignalSnapshotHolder Holder(string yaml)
    {
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        store.ApplyConfiguration(TestConfigurations.Parse(yaml), T0);
        store.ApplySourceStatus(SignalSourceStatus.Connected);
        return new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(T0), store.ConfigurationResult, ImmutableDictionary<SignalName, EntityId>.Empty, []));
    }

    private sealed class MemoryStateStore : IRuntimeStateStore
    {
        public RuntimeState? Saved { get; private set; }

        public RuntimeStateLoad Load() => new(Saved ?? RuntimeState.Fresh, StateFileStatus.Fresh, null);

        public void Save(RuntimeState state) => Saved = state;
    }
}
