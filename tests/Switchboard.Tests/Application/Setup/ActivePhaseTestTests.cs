using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// CONN-05, the active phase self-test (Stage C): on demand, in effective active mode only,
/// Switchboard switches one declared, idle relay stage on and off itself and judges the meter's
/// steps with the passive self-test's classifier. Over the committed synthetic morning with the
/// simulated plant: the element really on L1 confirms its declaration, the same element wired to L3
/// is caught as a mismatch naming L3. Refused in shadow, with control off, without headroom and
/// under min_off, each with its reason; protection outranks it (the guard acting aborts it); and
/// whatever ends it (a stop by the user, the guard, control switched off, an off Home Assistant
/// refused, a relay that reports late) the device is left off as it was and dispatch is not held back.
/// </summary>
public sealed class ActivePhaseTestTests
{
    private static readonly DateTimeOffset Ready = new(2026, 10, 22, 6, 25, 0, TimeSpan.Zero);

    [Fact]
    public void In_shadow_the_active_test_is_refused_with_the_reason()
    {
        var run = new Run("synthetic-plant.json", TestModes.State(OperatingMode.Shadow, controlAllowed: false));
        run.Harness.RunUntil(Ready);

        var (test, preconditions) = run.Service.Start(DeviceName.Parse("heater_l1"), 1);

        Assert.Null(test);
        Assert.Contains(preconditions.Refusals, r => r.Code == ActiveTestRefusalCode.ControlNotActive);
        Assert.Empty(run.Path.Calls);
    }

    [Fact]
    public void With_the_option_active_but_control_off_the_active_test_is_refused()
    {
        var run = new Run("synthetic-plant.json", TestModes.State(OperatingMode.Active, controlAllowed: false));
        run.Harness.RunUntil(Ready);

        var (test, preconditions) = run.Service.Start(HeaterL1, 1);

        Assert.Null(test);
        Assert.Contains(preconditions.Refusals, r => r.Code == ActiveTestRefusalCode.ControlNotActive);
        Assert.Empty(run.Path.Calls);
    }

    [Fact]
    public void Without_headroom_of_the_stages_rating_the_test_is_refused_naming_the_phase()
    {
        // 09:20 of the synthetic morning: the charger draws 16 A on L1, which leaves less than the
        // heater's 7 A (plus the reserve) there; switching it on could push L1 toward its breaker.
        var run = new Run("synthetic-plant.json");
        run.Harness.RunUntil(new DateTimeOffset(2026, 10, 22, 9, 20, 30, TimeSpan.Zero));

        var (test, preconditions) = run.Service.Start(HeaterL1, 1);

        Assert.Null(test);
        Assert.Contains(preconditions.Refusals, r => r.Code == ActiveTestRefusalCode.HeadroomInsufficient && r.Phase == Phase.L1);
        Assert.DoesNotContain(run.Path.Calls, c => c.Call.Entity.Value == "switch.grzalka_2");
    }

    [Fact]
    public void Under_a_running_min_off_the_test_is_refused_with_the_time_left()
    {
        // The recording switches heater_l2 (switch.grzalka_1) off at 07:46:05: its min_off runs, and
        // the test may not shorten it, so it says how long is left instead of starting.
        var run = new Run("synthetic-plant.json");
        run.Harness.RunUntil(new DateTimeOffset(2026, 10, 22, 7, 46, 30, TimeSpan.Zero));

        var (test, preconditions) = run.Service.Start(DeviceName.Parse("heater_l2"), 1);

        Assert.Null(test);
        var refusal = Assert.Single(preconditions.Refusals, r => r.Code == ActiveTestRefusalCode.LockoutMinOff);
        Assert.InRange(refusal.Remaining!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(90));
        Assert.DoesNotContain(run.Path.Calls, c => c.Call.Entity.Value == "switch.grzalka_1");
    }

    [Fact]
    public void When_the_guard_acts_during_the_test_it_aborts_and_the_device_ends_off()
    {
        // Protection outranks the test: an overload on L1 while the heater is on for the test makes
        // the guard act, the test ends with guard_acted, and the heater is off afterwards.
        var run = new Run("synthetic-plant.json");
        run.Harness.RunUntil(Ready);
        var (test, _) = run.Service.Start(HeaterL1, 1);
        Assert.NotNull(test);
        run.Harness.RunUntil(Ready + TimeSpan.FromSeconds(40));
        Assert.Equal(ActiveTestPhase.On, run.Service.Get(test.Id)!.Phase);

        // One meter poll far above the breaker (1.6 × In: no budget at all), seen by the guard
        // before the next recorded poll.
        run.Harness.Replay.Source.Inject(EntityId.Parse("sensor.solaredge_m1_ac_power_a"), "-9200");
        run.Harness.Replay.Source.Inject(EntityId.Parse("sensor.solaredge_m1_ac_current_a"), "40");
        run.Harness.RunUntil(run.Harness.Now + TimeSpan.FromMilliseconds(1));
        Assert.Equal((ActiveTestState.Aborted, (ActiveTestAbortReason?)ActiveTestAbortReason.GuardActed), (run.Service.Get(test.Id)!.State, run.Service.Get(test.Id)!.AbortReason));

        run.Harness.RunUntil(Ready + TimeSpan.FromMinutes(15));
        AssertLeftOff(run);
    }

    [Fact]
    public void A_stop_before_the_relay_reported_its_switch_on_still_leaves_the_device_off()
    {
        // The relay reports a few steps after the step that commanded it. The user stops the test in
        // between: the switch-on shows only later, as the target already in force, so the test must
        // keep asking for its off (after min_on) until the effector no longer intends the stage on and
        // the device reports it off; letting go earlier would leave the 1.7 kW element on for good.
        var run = new Run("synthetic-plant.json", reportDelaySteps: 3);
        run.Harness.RunUntil(Ready);
        var (test, _) = run.Service.Start(HeaterL1, 1);
        Assert.NotNull(test);
        run.Harness.Runner.Step();
        Assert.Equal([ActuatorService(true)], run.Path.Calls.Where(c => c.Call.Entity.Value == "switch.grzalka_2").Select(c => c.Call.Service));
        Assert.Equal(ActiveTestPhase.PendingOn, run.Service.Get(test.Id)!.Phase);

        Assert.True(run.Service.Cancel(test.Id));
        run.Harness.RunUntil(Ready + TimeSpan.FromMinutes(15));

        Assert.Equal((ActiveTestState.Aborted, (ActiveTestAbortReason?)ActiveTestAbortReason.Cancelled), (run.Service.Get(test.Id)!.State, run.Service.Get(test.Id)!.AbortReason));
        AssertLeftOff(run);

        // The test's own off did it (origin self_test, once min_on allowed), not some later
        // reduction of dispatch that happened to come along.
        var off = run.Harness.Decisions.Last(d => d.Device == HeaterL1 && d.Action == Switchboard.Application.Decisions.DecisionAction.SwitchOff);
        Assert.Equal((Switchboard.Application.Decisions.DecisionOrigin.SelfTest, true), (off.Origin, off.Commanded));
    }

    [Fact]
    public void An_off_home_assistant_refused_is_sent_again_and_the_test_still_finishes()
    {
        // A Modbus write error on the test's off: the effector keeps sending the off at each
        // boundary instead of believing it happened, so the element does not stay on.
        var run = new Run("synthetic-plant.json");
        var refused = false;
        run.Path.Fail = call => call.Service == Switchboard.Application.Configuration.ActuatorService.TurnOff && !refused && (refused = true);
        run.Harness.RunUntil(Ready);

        var view = Finish(run);

        Assert.Equal(ActiveTestState.Finished, view.State);
        Assert.Equal(
            [ActuatorService(true), ActuatorService(false), ActuatorService(false)],
            run.Path.Calls.Where(c => c.Call.Entity.Value == "switch.grzalka_2").Select(c => c.Call.Service));
        AssertLeftOff(run);
    }

    [Fact]
    public void An_off_that_keeps_failing_lets_go_of_dispatch_after_the_cleanup_deadline_but_keeps_being_sent()
    {
        // Home Assistant refuses every off (the relay's link is down): the test ends not confirmed,
        // and after its cleanup deadline it no longer holds every other increase back; the off
        // itself keeps being sent at each boundary.
        var run = new Run("synthetic-plant.json");
        run.Path.Fail = call => call.Service == Switchboard.Application.Configuration.ActuatorService.TurnOff;
        run.Harness.RunUntil(Ready);
        var (test, _) = run.Service.Start(HeaterL1, 1);
        Assert.NotNull(test);

        run.Harness.RunUntil(Ready + TimeSpan.FromMinutes(30));

        Assert.Equal((ActiveTestState.Aborted, (ActiveTestAbortReason?)ActiveTestAbortReason.NotConfirmed), (run.Service.Get(test.Id)!.State, run.Service.Get(test.Id)!.AbortReason));
        Assert.Null(run.Slot.Current);
        Assert.True(run.Path.Calls.Count(c => c.Call.Entity.Value == "switch.grzalka_2" && c.Call.Service == ActuatorService(false)) > 3);
    }

    [Fact]
    public void A_declared_element_on_its_real_phase_is_confirmed_and_left_off()
    {
        var run = new Run("synthetic-plant.json");
        run.Harness.RunUntil(Ready);
        var preconditions = run.Service.Check(DeviceName.Parse("heater_l1"), 1);
        Assert.True(preconditions.Ok, string.Join(", ", preconditions.Refusals.Select(r => $"{r.Code} {r.Phase}")));

        var view = Finish(run);

        Assert.Equal((ActiveTestState.Finished, ActiveTestPhase.Done, (ActiveTestAbortReason?)null), (view.State, view.Phase, view.AbortReason));
        Assert.Equal((PhaseStepVerdict.Confirmed, 2), (view.Judgement!.Summary.Verdict, view.Judgement.Summary.Confirmed));
        Assert.All(view.Judgement.Observations, o => Assert.Equal(ObservationSource.Active, o.Observation.Source));
        Assert.Equal(2, view.Judgement.Observations.Length);
        Assert.Equal(
            [("switch.grzalka_2", ActuatorService(true)), ("switch.grzalka_2", ActuatorService(false))],
            run.Path.Calls.Where(c => c.Call.Entity.Value == "switch.grzalka_2").Select(c => (c.Call.Entity.Value, c.Call.Service)));
        Assert.Contains(run.Harness.Decisions, d => d.Device?.Value == "heater_l1" && d.Origin == Switchboard.Application.Decisions.DecisionOrigin.SelfTest && d.Commanded);
    }

    [Fact]
    public void The_same_element_wired_to_another_phase_is_a_mismatch_naming_that_phase()
    {
        var run = new Run("synthetic-plant-swapped.json");
        run.Harness.RunUntil(Ready);

        var view = Finish(run);

        Assert.Equal(ActiveTestState.Finished, view.State);
        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L3), (view.Judgement!.Summary.Verdict, view.Judgement.Summary.Observed));
    }

    [Fact]
    public void Switching_control_off_mid_test_aborts_it_and_sends_the_tests_own_off()
    {
        var clock = new SignalClock(new SteppableTime(Ready));
        var control = new ControlSwitchState(new GuardInbox(), new KillSwitchState(new GuardInbox(), clock, NullLogger<KillSwitchState>.Instance), clock, NullLogger<ControlSwitchState>.Instance);
        control.Set(true, ControlSwitchSource.Api);
        var run = new Run("synthetic-plant.json", new ModeState(OperatingMode.Active, control));
        run.Harness.RunUntil(Ready);
        var (test, _) = run.Service.Start(DeviceName.Parse("heater_l1"), 1);
        Assert.NotNull(test);
        run.Harness.RunUntil(Ready + TimeSpan.FromSeconds(40));
        Assert.Equal(ActiveTestPhase.On, run.Service.Get(test.Id)!.Phase);

        control.Set(false, ControlSwitchSource.Api);
        run.Harness.RunUntil(Ready + TimeSpan.FromSeconds(60));

        var view = run.Service.Get(test.Id)!;
        Assert.Equal((ActiveTestState.Aborted, (ActiveTestAbortReason?)ActiveTestAbortReason.ControlLeftActive), (view.State, view.AbortReason));
        var last = run.Path.Calls.Last(c => c.Call.Entity.Value == "switch.grzalka_2");
        Assert.Equal(ActuatorService(false), last.Call.Service);
        Assert.True(last.Command.Final);
    }

    [Fact]
    public void Switching_control_off_while_a_stopped_tests_off_waits_for_min_on_still_sends_that_off_once()
    {
        // The user stops the test while the heater is on: its off waits for min_on. Control is then
        // switched off in those minutes: the test's own off is still the last command (once), as for
        // a test that was still running, so no load the test added is left on.
        var clock = new SignalClock(new SteppableTime(Ready));
        var control = new ControlSwitchState(new GuardInbox(), new KillSwitchState(new GuardInbox(), clock, NullLogger<KillSwitchState>.Instance), clock, NullLogger<ControlSwitchState>.Instance);
        control.Set(true, ControlSwitchSource.Api);
        var run = new Run("synthetic-plant.json", new ModeState(OperatingMode.Active, control));
        run.Harness.RunUntil(Ready);
        var (test, _) = run.Service.Start(HeaterL1, 1);
        Assert.NotNull(test);
        run.Harness.RunUntil(Ready + TimeSpan.FromSeconds(40));
        Assert.Equal(ActiveTestPhase.On, run.Service.Get(test.Id)!.Phase);
        Assert.True(run.Service.Cancel(test.Id));
        run.Harness.RunUntil(Ready + TimeSpan.FromSeconds(50));
        Assert.DoesNotContain(run.Path.Calls, c => c.Call.Entity.Value == "switch.grzalka_2" && c.Call.Service == ActuatorService(false));

        control.Set(false, ControlSwitchSource.Api);
        run.Harness.RunUntil(Ready + TimeSpan.FromMinutes(15));

        var off = Assert.Single(run.Path.Calls, c => c.Call.Entity.Value == "switch.grzalka_2" && c.Call.Service == ActuatorService(false));
        Assert.True(off.Command.Final);
        AssertLeftOff(run);
    }

    [Fact]
    public void A_finished_test_has_exactly_the_keys_of_the_spa_sample()
    {
        // GET api/setup/active-test/{id} as the endpoint serializes it (SetupEndpoint.Answers); with
        // SWITCHBOARD_WRITE_SPA_SAMPLES=1 the sample the SPA tests parse is written from it.
        var run = new Run("synthetic-plant.json");
        run.Harness.RunUntil(Ready);
        var view = Finish(run);

        var full = System.Text.Json.JsonSerializer.SerializeToElement(Switchboard.Host.Web.SetupEndpoint.ActiveTest(view), Switchboard.Host.Web.SetupEndpoint.Answers);
        var committed = SpaContract.Sample(RepoPaths.Combine("web", "src", "api", "setup-active-test.sample.json"), full);
        string[] free = ["$.observations[].deltas"];
        Assert.Equal(SpaContract.Describe([full], free), SpaContract.Describe([committed], free));
    }

    private static readonly DeviceName HeaterL1 = DeviceName.Parse("heater_l1");

    private static Switchboard.Application.Configuration.ActuatorService ActuatorService(bool on) =>
        on ? Switchboard.Application.Configuration.ActuatorService.TurnOn : Switchboard.Application.Configuration.ActuatorService.TurnOff;

    /// <summary>The tested heater reports off, the effector does not intend it on, and the slot is free again (dispatch no longer held back).</summary>
    private static void AssertLeftOff(Run run)
    {
        var status = run.Harness.Steps[^1].DispatchStatus!.Devices.Single(d => d.Id == HeaterL1);
        Assert.Equal(Switchboard.Domain.Devices.DeviceLevel.Relay(false), status.Reported);
        Assert.False(status.Intended?.Relays[0] ?? false);
        Assert.Null(run.Slot.Current);
        Assert.Equal(ActuatorService(false), run.Path.Calls.Last(c => c.Call.Entity.Value == "switch.grzalka_2").Call.Service);
    }

    /// <summary>Starts the test on heater_l1 and runs the replay until it ended (at most 15 minutes).</summary>
    private static ActiveTestView Finish(Run run)
    {
        var (test, preconditions) = run.Service.Start(DeviceName.Parse("heater_l1"), 1);
        Assert.True(test is not null, string.Join(", ", preconditions.Refusals.Select(r => $"{r.Code} {r.Phase}")));
        var end = Ready + TimeSpan.FromMinutes(15);
        while (run.Service.Get(test.Id) is { State: ActiveTestState.Running } && run.Harness.Now < end)
        {
            run.Harness.RunUntil(run.Harness.Now + TimeSpan.FromSeconds(30));
        }

        return run.Service.Get(test.Id)!;
    }

    /// <summary>The synthetic morning with the example configuration, the plant, the command path, the recorder and the service.</summary>
    private sealed class Run
    {
        /// <param name="reportDelaySteps">Guard steps the plant takes to follow a command (a relay reporting late).</param>
        public Run(string plantFile, IModeSource? mode = null, int reportDelaySteps = 0)
        {
            var data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-dispatch-day.csv"));
            var configuration = TestConfigurations.Valid(TestConfigurations.Example).Configuration!;
            var plant = new SimulatedPlant(PlantDefinition.Load(RepoPaths.Combine("tests", "fixtures", "replay", plantFile)), data, () => configuration);
            Path = new PlantCommandPath(configuration, plant, reportDelaySteps);
            Harness = new GuardReplayHarness(data, TestConfigurations.Example, mode: mode ?? TestModes.State(OperatingMode.Active, controlAllowed: true), port: Path.Port, selfTest: Slot);
            plant.Attach(Harness.Replay.Source);
            var view = new DiagnosticsRuntimeView(Harness.Replay.Holder);
            Service = new ActivePhaseTestService(Slot, Harness.Inbox, Evidence, view, SetupFixtures.Defaults, new SignalClock(Harness.Replay.Time), NullLogger<ActivePhaseTestService>.Instance);
            Service.Attach(Harness.Runner);
            new PhaseEvidenceRecorder(Evidence, view, SetupFixtures.Defaults, 64, Service).Attach(Harness.Runner);
            Harness.Runner.Stepped += (_, _) => Path.Stepped();
        }

        public SelfTestSlot Slot { get; } = new();

        public PhaseEvidenceStore Evidence { get; } = new(20);

        public PlantCommandPath Path { get; }

        public GuardReplayHarness Harness { get; }

        public ActivePhaseTestService Service { get; }
    }
}
