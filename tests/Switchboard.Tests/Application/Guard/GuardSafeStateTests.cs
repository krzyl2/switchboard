using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// SAFE-01 and SIG-05 (fail-safe table rows, VER-03): without valid data every controlled device
/// the data protects is held off (D8: no missing signal may leave a load energized). A phase
/// without valid current holds its own devices; no valid phase at all, an invalid or missing
/// configuration, no snapshot yet or shutdown hold everything. A device whose own state is not
/// fresh is unsafe and held off, and holding it relieves nothing. Leaving safe state never lets go
/// by itself, and release persistence restarts at every hold cause, between boundaries too.
/// </summary>
public sealed class GuardSafeStateTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;

    [Fact]
    public void Stale_phase_current_only_that_phase()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);

        var evaluation = s.Invalidate(Phase.L2, ProtectionInvalidReason.CurrentStale);

        var held = Assert.Single(evaluation.Intents);
        Assert.Equal(("heater_l2", GuardTarget.Off, ReasonCode.GuardStaleSignal, Phase.L2), (held.Device.Value, held.Target, held.Reason, held.Phase));
        var entered = Assert.Single(evaluation.Events);
        Assert.Equal((DecisionScope.Phase, Phase.L2, DecisionAction.SafeStateEntered), (entered.Scope, entered.Phase!.Value, entered.Action));
        Assert.Equal((SafeStateScope.Phases, ReasonCode.GuardStaleSignal), (evaluation.Status.SafeState.Scope, evaluation.Status.SafeState.Reason));
        Assert.Equal(DeviceGuardState.Unsafe, s.DeviceStatus("heater_l2").GuardState);
        Assert.Equal([Phase.L2], s.DeviceStatus("heater_l2").UnsafePhases);
        Assert.Equal(DeviceGuardState.Allowed, s.DeviceStatus("heater_l1").GuardState);
    }

    [Theory]
    [InlineData(ProtectionInvalidReason.CurrentStale, ReasonCode.GuardStaleSignal)]
    [InlineData(ProtectionInvalidReason.CurrentRejected, ReasonCode.GuardStaleSignal)]
    [InlineData(ProtectionInvalidReason.CurrentUnavailable, ReasonCode.GuardStaleSignal)]
    [InlineData(ProtectionInvalidReason.CurrentMissing, ReasonCode.GuardStaleSignal)]
    [InlineData(ProtectionInvalidReason.CurrentAwaiting, ReasonCode.SafeState)]
    [InlineData(ProtectionInvalidReason.SourceDisconnected, ReasonCode.SafeState)]
    public void Invalid_phase_reason_maps_to_its_code(ProtectionInvalidReason invalid, ReasonCode expected)
    {
        // A signal that went bad is guard_stale_signal; no data for reasons outside the signal
        // (start, reconnect, link down) is safe_state. Either way the devices on the phase go off.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);

        var held = Assert.Single(s.Invalidate(Phase.L3, invalid).Intents);

        Assert.Equal(("heater_l3", GuardTarget.Off, expected), (held.Device.Value, held.Target, held.Reason));
    }

    [Fact]
    public void Whole_meter_stale_everywhere()
    {
        // Every phase stale: safe state everywhere, the charger off too (not at its minimum,
        // although it cannot actually be switched off before Stage 3: the record says so).
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.Invalidate(Phase.L1, ProtectionInvalidReason.CurrentStale, evaluate: false);
        s.Invalidate(Phase.L2, ProtectionInvalidReason.CurrentStale, evaluate: false);

        var evaluation = s.Invalidate(Phase.L3, ProtectionInvalidReason.CurrentStale);

        Assert.Equal(["ev_charger", "heater_l1", "heater_l2", "heater_l3"], evaluation.Intents.Select(i => i.Device.Value).Order());
        Assert.All(evaluation.Intents, i => Assert.Equal((GuardTarget.Off, ReasonCode.GuardStaleSignal), (i.Target, i.Reason)));
        Assert.False(evaluation.Intents.Single(i => i.Device.Value == "ev_charger").Inputs.Device!.CanSwitchOff);
        var entered = Assert.Single(evaluation.Events);
        Assert.Equal((DecisionScope.System, DecisionAction.SafeStateEntered, ReasonCode.GuardStaleSignal), (entered.Scope, entered.Action, entered.Reason));
        Assert.Equal(SafeStateScope.All, evaluation.Status.SafeState.Scope);
    }

    [Fact]
    public void HA_disconnection_everywhere()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        foreach (var phase in new[] { Phase.L1, Phase.L2 })
        {
            s.Invalidate(phase, ProtectionInvalidReason.SourceDisconnected, evaluate: false);
        }

        var evaluation = s.Invalidate(Phase.L3, ProtectionInvalidReason.SourceDisconnected);

        Assert.Equal(4, evaluation.Intents.Length);
        Assert.All(evaluation.Intents, i => Assert.Equal(ReasonCode.SafeState, i.Reason));
        Assert.Equal(ReasonCode.SafeState, evaluation.Status.SafeState.Reason);
    }

    [Fact]
    public void HA_reconnect_awaiting_is_safe_state_and_releases_by_streak()
    {
        // After a reconnect the currents await their first state: still safe state. Once valid
        // again, the holds stay until persistence_up_cycles good boundaries (6 × 30 s), so a
        // flapping connection cannot switch loads on and off.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
        {
            s.Invalidate(phase, ProtectionInvalidReason.CurrentAwaiting, evaluate: false);
        }

        s.Evaluate();
        Assert.All(s.Last.Status.Devices, d => Assert.Equal(DeviceGuardState.Unsafe, d.GuardState));
        s.RunUntil(T0 + TimeSpan.FromSeconds(10));
        s.SampleAll(8, 8, 8);
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);

        s.RunUntil(T0 + TimeSpan.FromSeconds(180) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);
        s.RunUntil(T0 + TimeSpan.FromSeconds(180));

        Assert.Equal(4, s.Intents.Count(i => i.Target == GuardTarget.Released));
        Assert.All(s.Intents.Where(i => i.Target == GuardTarget.Released), i => Assert.Equal(ReasonCode.HeadroomAvailable, i.Reason));
    }

    [Fact]
    public void Safe_state_exit_releases_only_after_persistence_up()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.Invalidate(Phase.L2, ProtectionInvalidReason.CurrentStale);
        s.RunUntil(T0 + TimeSpan.FromSeconds(40));

        var exit = s.Sample(Phase.L2, 8);

        Assert.Contains(exit.Events, e => e.Action == DecisionAction.SafeStateExited && e.Phase == Phase.L2);
        Assert.Empty(exit.Intents);
        Assert.Equal(DeviceGuardState.Shed, s.DeviceStatus("heater_l2").GuardState);
        s.RunUntil(T0 + TimeSpan.FromSeconds(210) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);
        s.RunUntil(T0 + TimeSpan.FromSeconds(210));
        var release = Assert.Single(s.Intents, i => i.Target == GuardTarget.Released);
        Assert.Equal("heater_l2", release.Device.Value);
    }

    [Fact]
    public void Invalid_configuration_keeps_last_valid_devices_off()
    {
        // An invalid document clears the signal store; the guard still knows the devices of the
        // last valid document of this process and holds every one of them off.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.ConfigurationValid(false);

        var evaluation = s.Evaluate();

        Assert.Equal(4, evaluation.Intents.Length);
        Assert.All(evaluation.Intents, i => Assert.Equal((GuardTarget.Off, ReasonCode.SafeState), (i.Target, i.Reason)));
        Assert.Contains(evaluation.Events, e => e.Scope == DecisionScope.System && e.Action == DecisionAction.SafeStateEntered);
    }

    [Fact]
    public void Invalid_configuration_at_start_records_a_system_event_only()
    {
        // Nothing valid was ever read in this process: no device is known, so nothing can be held;
        // the safe state is recorded so the log says why nothing is protected.
        var s = new GuardScenario(TestDevices.Document());
        s.ConfigurationValid(false);

        var evaluation = s.Evaluate();

        Assert.Empty(evaluation.Intents);
        var entered = Assert.Single(evaluation.Events);
        Assert.Equal((DecisionScope.System, DecisionAction.SafeStateEntered, ReasonCode.SafeState), (entered.Scope, entered.Action, entered.Reason));
        Assert.Empty(evaluation.Status.Devices);
        Assert.Null(s.Engine.NextDeadline(s.Now));
    }

    [Fact]
    public void Missing_configuration_is_safe_state()
    {
        // The document deleted while running: the store clears everything, the guard holds the
        // devices it last knew off and records the system-wide safe state (through the real store).
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.PushReference();
        h.Runner.Step();

        h.Store.ApplyConfiguration(
            Switchboard.Application.Configuration.ConfigurationResult.Failed(
                Switchboard.Application.Configuration.ConfigurationStatus.Missing,
                [new Switchboard.Application.Configuration.ConfigurationIssue("file_missing", string.Empty, null, null, "gone")],
                TestConfigurations.Path,
                h.Now),
            h.Now);
        h.Publish();
        h.Runner.Step();

        Assert.Contains(h.Recorder.Decisions, d => d.Scope == DecisionScope.System && d.Action == DecisionAction.SafeStateEntered && d.Reason == ReasonCode.SafeState);
        Assert.Equal(4, h.Recorder.Decisions.Count(d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.SafeState));
        Assert.Equal(SafeStateScope.All, h.Status.Current.SafeState.Scope);
    }

    [Fact]
    public void No_snapshot_yet_is_safe_state()
    {
        var s = new GuardScenario(TestDevices.Document());

        var evaluation = s.Evaluate(withInputs: false);

        Assert.True(evaluation.Status.SafeState.Active);
        Assert.Equal((SafeStateScope.All, ReasonCode.SafeState), (evaluation.Status.SafeState.Scope, evaluation.Status.SafeState.Reason));
        Assert.Equal(SafeStateCause.Starting, Assert.Single(evaluation.Events).Inputs.SafeStateCause);
    }

    [Fact]
    public void Stale_device_state_marks_unsafe_and_relieves_nothing()
    {
        // SIG-05: the heater's state is stale; it may be on. It is held off and shown unsafe, but
        // the hold relieves nothing: the heater may already draw nothing, so the effective current
        // stays the measured 20 A (subtracting 6.6 A would hide load that is really there).
        var s = new GuardScenario(TestDevices.Document());
        s.SetState("heater_l1", null, invalid: DeviceStateInvalidReason.Stale);
        s.SampleAll(20, 8, 8);

        var held = s.Last.Intents.Single();
        Assert.Equal(("heater_l1", GuardTarget.Off, ReasonCode.GuardStaleSignal), (held.Device.Value, held.Target, held.Reason));
        var device = s.DeviceStatus("heater_l1");
        Assert.Equal(DeviceGuardState.Unsafe, device.GuardState);
        Assert.Equal([1], device.UnsafeStages);
        Assert.Equal(DeviceStateInvalidReason.Stale, device.Stages.Single().InvalidReason);
        Assert.Equal(20, s.PhaseStatus(Phase.L1).EffectiveCurrent!.Value.Value, 9);
    }

    [Fact]
    public void Outage_between_boundaries_restarts_release_persistence()
    {
        // 04 §7: a release needs persistence_up_cycles (6) good boundaries after the hold cause.
        // After 5 quiet minutes the streak is far above 6; a 10 s whole-meter outage that starts
        // and ends between two boundaries (12:05:15 to 12:05:25) must not be let go at 12:05:30,
        // but only at the sixth good boundary after it, 12:08:00.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(315));
        foreach (var phase in new[] { Phase.L1, Phase.L2 })
        {
            s.Invalidate(phase, ProtectionInvalidReason.CurrentUnavailable, evaluate: false);
        }

        Assert.Equal(4, s.Invalidate(Phase.L3, ProtectionInvalidReason.CurrentUnavailable).Intents.Length);
        s.RunUntil(T0 + TimeSpan.FromSeconds(325));
        s.SampleAll(8, 8, 8);

        s.RunUntil(T0 + TimeSpan.FromSeconds(480) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);
        s.RunUntil(T0 + TimeSpan.FromSeconds(480));
        Assert.Equal(4, s.Intents.Count(i => i.Target == GuardTarget.Released));
    }

    [Fact]
    public void One_phase_blip_between_boundaries_restarts_release_persistence()
    {
        // The same for a single poll of "unavailable" on L2 after a long quiet run: the heater on
        // L2 is held for six good boundaries after the blip, not released at the next one.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(315));
        s.Invalidate(Phase.L2, ProtectionInvalidReason.CurrentUnavailable);
        s.RunUntil(T0 + TimeSpan.FromSeconds(325));
        s.Sample(Phase.L2, 8);

        s.RunUntil(T0 + TimeSpan.FromSeconds(480) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);
        s.RunUntil(T0 + TimeSpan.FromSeconds(480));
        Assert.Equal("heater_l2", Assert.Single(s.Intents, i => i.Target == GuardTarget.Released).Device.Value);
    }

    [Fact]
    public void Device_state_blip_between_boundaries_restarts_release_persistence()
    {
        // A Modbus relay whose state is unknown for one poll: held off at 12:05:15, fresh again at
        // 12:05:25; a hold recorded between boundaries restarts the persistence of its phase.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(315));
        s.SetState("heater_l3", null, invalid: DeviceStateInvalidReason.Unavailable);
        Assert.Equal("heater_l3", s.Evaluate().Intents.Single().Device.Value);
        s.RunUntil(T0 + TimeSpan.FromSeconds(325));
        s.SetState("heater_l3", new RelayApplied(true));
        s.Evaluate();

        s.RunUntil(T0 + TimeSpan.FromSeconds(480) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);
        s.RunUntil(T0 + TimeSpan.FromSeconds(480));
        Assert.Equal("heater_l3", Assert.Single(s.Intents, i => i.Target == GuardTarget.Released).Device.Value);
    }

    [Fact]
    public void Shutdown_records_safe_state_for_every_device()
    {
        // MODE-03: the log says the add-on was stopping (cause shutdown on the system record and on
        // every hold it made) and keeps the last phase picture, not "data invalid" with no reason.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);

        var evaluation = s.Evaluate(GuardTrigger.Shutdown);

        Assert.Equal(4, evaluation.Intents.Length);
        Assert.All(evaluation.Intents, i => Assert.Equal((GuardTarget.Off, ReasonCode.SafeState, SafeStateCause.Shutdown), (i.Target, i.Reason, i.Inputs.SafeStateCause)));
        Assert.All(evaluation.Intents, i => Assert.Equal((true, (ProtectionInvalidReason?)null, 8.0), (i.Inputs.Phase!.DataValid, i.Inputs.Phase.InvalidReason, i.Inputs.Phase.Current!.Value.Value)));
        var entered = Assert.Single(evaluation.Events);
        Assert.Equal((DecisionScope.System, DecisionAction.SafeStateEntered, SafeStateCause.Shutdown), (entered.Scope, entered.Action, entered.Inputs.SafeStateCause));
        Assert.True(s.Engine.Export().CleanShutdown);
    }

    [Theory]
    [InlineData(ProtectionInvalidReason.CurrentStale, SafeStateCause.MeterInvalid)]
    [InlineData(ProtectionInvalidReason.CurrentAwaiting, SafeStateCause.MeterInvalid)]
    [InlineData(ProtectionInvalidReason.SourceDisconnected, SafeStateCause.SourceDisconnected)]
    public void System_safe_state_names_its_cause(ProtectionInvalidReason invalid, SafeStateCause expected)
    {
        // MODE-03: a whole-meter outage and an HA disconnection both hold everything off; the
        // record says which, on the system event and on each device hold.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.Invalidate(Phase.L1, invalid, evaluate: false);
        s.Invalidate(Phase.L2, invalid, evaluate: false);

        var evaluation = s.Invalidate(Phase.L3, invalid);

        Assert.Equal(expected, Assert.Single(evaluation.Events).Inputs.SafeStateCause);
        Assert.All(evaluation.Intents, i => Assert.Equal(expected, i.Inputs.SafeStateCause));
        s.SampleAll(8, 8, 8);
        var exited = Assert.Single(s.Last.Events);
        Assert.Equal((DecisionAction.SafeStateExited, expected), (exited.Action, exited.Inputs.SafeStateCause));
    }

    [Theory]
    [InlineData(SetupAbsence.Invalid, SafeStateCause.ConfigurationInvalid)]
    [InlineData(SetupAbsence.Missing, SafeStateCause.ConfigurationMissing)]
    [InlineData(SetupAbsence.NotRead, SafeStateCause.Starting)]
    public void Configuration_safe_state_tells_missing_from_invalid(SetupAbsence absence, SafeStateCause expected)
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.ConfigurationValid(false, absence);

        var evaluation = s.Evaluate();

        Assert.Equal(expected, Assert.Single(evaluation.Events, e => e.Action == DecisionAction.SafeStateEntered).Inputs.SafeStateCause);
        Assert.All(evaluation.Intents, i => Assert.Equal(expected, i.Inputs.SafeStateCause));
    }

    [Fact]
    public void Shutdown_during_invalid_configuration_is_recorded_as_shutdown()
    {
        // The devices are already held off by safe_state; a stop changes the cause, not the holds,
        // and is still recorded, so the log never ends on "configuration invalid" for a stop.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.ConfigurationValid(false);
        s.Evaluate();

        var evaluation = s.Evaluate(GuardTrigger.Shutdown);

        Assert.Empty(evaluation.Intents);
        var entered = Assert.Single(evaluation.Events);
        Assert.Equal((DecisionAction.SafeStateEntered, ReasonCode.SafeState, SafeStateCause.Shutdown), (entered.Action, entered.Reason, entered.Inputs.SafeStateCause));
    }

    [Fact]
    public void Missing_device_state_is_unsafe_too()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SetState("ev_charger", null, invalid: DeviceStateInvalidReason.Missing);

        s.SampleAll(8, 8, 8);

        Assert.Equal(("ev_charger", GuardTarget.Off), (s.Last.Intents.Single().Device.Value, s.Last.Intents.Single().Target));
    }

    [Fact]
    public void Reload_removing_a_limited_device_drops_its_limit()
    {
        // A device no longer declared can never be commanded again; protection stops holding it
        // and never mentions it again, while the remaining devices keep their holds.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.Sample(Phase.L1, 37.5);
        Assert.Equal(2, s.Sheds.Count());

        s.Reconfigure(TestDevices.Document(TestDevices.Reference[..TestDevices.Reference.IndexOf("  - id: ev_charger", StringComparison.Ordinal)]));
        s.RunFor(TimeSpan.FromMinutes(30));

        Assert.DoesNotContain(s.Last.Status.Devices, d => d.Id.Value == "ev_charger");
        Assert.DoesNotContain(s.Intents.Skip(2), i => i.Device.Value == "ev_charger");
        Assert.Equal(DeviceGuardState.Shed, s.DeviceStatus("heater_l1").GuardState);
    }

    [Fact]
    public void A_no_shed_device_is_still_held_off_in_safe_state()
    {
        // no_shed keeps a device out of shedding, not out of D8: without data everything
        // controlled is off.
        var s = new GuardScenario(TestDevices.Document(TestDevices.Reference.Replace("    value_per_kwh: 2.03", "    value_per_kwh: 2.03\n    no_shed: true")));
        s.SampleAll(8, 8, 8);
        s.Sample(Phase.L1, 37.5);
        Assert.DoesNotContain(s.Sheds, i => i.Device.Value == "ev_charger");

        s.Invalidate(Phase.L1, ProtectionInvalidReason.CurrentStale);

        Assert.Contains(s.Last.Intents, i => i.Device.Value == "ev_charger" && i.Target == GuardTarget.Off);
    }
}
