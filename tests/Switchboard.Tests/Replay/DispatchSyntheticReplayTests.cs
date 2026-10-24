using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// Guard, dispatch and effector over the committed synthetic morning
/// (tests/fixtures/replay/make-synthetic-dispatch.py) with the maintainer's example configuration
/// plus a 16 A carport breaker for the charger: on_change heaters reported once, an idle charger at
/// a 20 A setpoint, kettles, someone else switching the elements, a lost meter sample. One pass in
/// shadow mode, as the live add-on runs, and one with active semantics through the recording
/// command port (the only implementation there is), where the heater relays never follow because
/// the recording cannot: an unresponsive effector.
/// </summary>
public sealed class DispatchSyntheticReplayRun
{
    public const string File = "synthetic-dispatch-day.csv";

    public DispatchSyntheticReplayRun()
    {
        Data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", File));
        var configuration = TestConfigurations.Valid(Configuration).Configuration!;
        Setup = DispatchSetupFactory.Build(configuration);
        Protection = ProtectionSetupFactory.Build(configuration);

        Shadow = new GuardReplayHarness(Data, Configuration, dispatch: true);
        Shadow.RunUntil(Data.End);
        ShadowAnalyzer = new DispatchAnalyzer(Shadow.Steps, Shadow.Decisions, Setup, Protection);

        Active = new GuardReplayHarness(Data, Configuration, dispatch: true, mode: new ActiveSemantics(), port: Port);
        Active.RunUntil(Data.End);
        ActiveAnalyzer = new DispatchAnalyzer(Active.Steps, Active.Decisions, Setup, Protection);
    }

    /// <summary>The example with the charger behind a 16 A carport breaker (no sub-meter: the worst case of its members).</summary>
    internal static string Configuration
    {
        get
        {
            var example = TestConfigurations.Example.ReplaceLineEndings("\n");
            var grouped = example
                .Replace("      max_age: 60s\n", "      max_age: 60s\n    circuit_group: carport\n", StringComparison.Ordinal)
                .Replace("\n# control: overrides", "\ncircuit_groups:\n  - id: carport\n    breaker_rating_a: 16\n    characteristic: B\n    phases: [L1]\n\n# control: overrides", StringComparison.Ordinal);
            Assert.Equal(example.Split('\n').Length + 7, grouped.Split('\n').Length);
            return grouped;
        }
    }

    internal ReplayData Data { get; }

    internal DispatchSetup Setup { get; }

    internal ProtectionSetup Protection { get; }

    internal GuardReplayHarness Shadow { get; }

    internal DispatchAnalyzer ShadowAnalyzer { get; }

    internal RecordingDeviceCommandPort Port { get; } = new();

    internal GuardReplayHarness Active { get; }

    internal DispatchAnalyzer ActiveAnalyzer { get; }

    /// <summary>A moment of the synthetic morning (2026-10-22, UTC).</summary>
    public static DateTimeOffset At(int hours, int minutes, double seconds = 0) =>
        new DateTimeOffset(2026, 10, 22, hours, minutes, 0, TimeSpan.Zero).AddSeconds(seconds);

    internal static IEnumerable<Decision> Of(GuardReplayHarness harness, string device, DecisionAction action, DecisionOrigin origin = DecisionOrigin.Dispatch) =>
        harness.Decisions.Where(d => d.Device?.Value == device && d.Action == action && d.Origin == origin);

    /// <summary>Active semantics for tests only: option and UI choice active, and a command path (the recording port).</summary>
    private sealed class ActiveSemantics : IModeSource
    {
        public ModeStatus Current { get; } = DispatchScenario.Active;
    }
}

public sealed class DispatchSyntheticReplayTests(DispatchSyntheticReplayRun run, ITestOutputHelper output) : IClassFixture<DispatchSyntheticReplayRun>
{
    // VER-04: the same independent checks as on the live recording, everywhere.
    [Fact]
    public void Shadow_dispatch_causes_misses_repeats_and_commands_nothing()
    {
        var violations = new ViolationAnalyzer(run.Protection, run.Shadow.Observations, run.Shadow.Decisions, run.Shadow.Steps);
        foreach (var line in run.ShadowAnalyzer.Report())
        {
            output.WriteLine(line);
        }

        Assert.Empty(violations.MissedViolations());
        Assert.Empty(violations.SilentActions());
        Assert.Empty(run.ShadowAnalyzer.IncreasesIntoTooLittleHeadroom());
        Assert.Empty(run.ShadowAnalyzer.IncreasesAgainstTheGuardOrAWindow());
        Assert.Empty(run.ShadowAnalyzer.RepeatedIntents());
        Assert.Empty(run.ShadowAnalyzer.Chatter());
        Assert.Empty(run.ShadowAnalyzer.CausedViolations());
        Assert.All(run.Shadow.Decisions, d => Assert.False(d.Commanded));
        Assert.True(run.ShadowAnalyzer.Changes().Count(c => c.Increase) >= 5, "Too few would-starts; the checks would pass vacuously.");
    }

    // The checks must be able to fail: the same run with one decision doctored at a time.
    [Fact]
    public void The_dispatch_analyzer_catches_repeats_chatter_overbooking_increases_past_the_guard_and_caused_violations()
    {
        var steps = run.Shadow.Steps.ToList();
        var start = DispatchSyntheticReplayRun.Of(run.Shadow, "heater_l2", DecisionAction.SwitchOn).First();
        var at = steps.FindIndex(s => s.Decisions.Contains(start));
        DispatchAnalyzer With(params (int Step, Decision Decision)[] added)
        {
            var doctored = steps.ToList();
            foreach (var (step, decision) in added)
            {
                doctored[step] = doctored[step] with { Decisions = doctored[step].Decisions.Add(decision) };
            }

            return new DispatchAnalyzer(doctored, [.. run.Shadow.Decisions, .. added.Select(a => a.Decision)], run.Setup, run.Protection);
        }

        // The same start once more, nothing in between: a repeated intent.
        Assert.NotEmpty(With((at + 1, Copy(start, "repeat", steps[at + 1].Evaluation.At))).RepeatedIntents());

        // Off and on again within two steps, the measured headroom unchanged: chatter.
        var stop = Copy(start, "stop", steps[at + 1].Evaluation.At, DecisionAction.SwitchOff, DeviceLevel.Relay(false));
        var again = Copy(start, "again", steps[at + 2].Evaluation.At);
        Assert.Contains(With((at + 1, stop), (at + 2, again)).Chatter(), c => c.StartsWith("heater_l2 chattered", StringComparison.Ordinal));

        // A start with only its own ceiling left (no start hysteresis): overbooked.
        var tight = start.Inputs.Dispatch! with { Headroom = [new ConstraintHeadroom(ConstraintKey.Main(Phase.L2), new Amperes(7), new Amperes(7))] };
        Assert.NotEmpty(With((at, Copy(start, "tight", start.At, inputs: tight))).IncreasesIntoTooLittleHeadroom());

        // A start where the guard allowed no increase (the kettle on L3 at 06:07).
        var refused = steps.FindIndex(s => s.Evaluation.Envelope.For(ConstraintKey.Main(Phase.L3)) is { IncreasesAllowed: false } && s.Evaluation.At > DispatchSyntheticReplayRun.At(6, 6));
        var l3 = DispatchSyntheticReplayRun.Of(run.Shadow, "heater_l3", DecisionAction.SwitchOn).First();
        Assert.Contains(With((refused, Copy(l3, "refused", steps[refused].Evaluation.At))).IncreasesAgainstTheGuardOrAWindow(), p => p.Contains("where the guard allowed none", StringComparison.Ordinal));

        // A would-start of 30 A on L1 counted for 10 minutes with nothing reduced: a caused violation.
        var phantom = steps.Select(s => s.Evaluation.At >= DispatchSyntheticReplayRun.At(7, 30) && s.Evaluation.At < DispatchSyntheticReplayRun.At(7, 40)
            ? s with { Evaluation = s.Evaluation with { Envelope = s.Evaluation.Envelope with { Devices = s.Evaluation.Envelope.Devices.Add(new DeviceLoad(
                DeviceName.Parse("heater_l1"), null, ImmutableDictionary<Phase, Amperes>.Empty, ImmutableDictionary<ConstraintKey, Amperes>.Empty,
                ImmutableDictionary<ConstraintKey, Amperes>.Empty.Add(ConstraintKey.Main(Phase.L1), new Amperes(30)), ImmutableDictionary<int, StageLimit>.Empty, true, ImmutableDictionary<Phase, DateTimeOffset>.Empty)) } } }
            : s).ToList();
        Assert.Contains(new DispatchAnalyzer(phantom, run.Shadow.Decisions, run.Setup, run.Protection).CausedViolations(), v => v.StartsWith("L1 band", StringComparison.Ordinal));
    }

    private static Decision Copy(Decision d, string id, DateTimeOffset at, DecisionAction? action = null, DeviceLevel? target = null, DispatchDecisionInputs? inputs = null) =>
        new(id, at, d.Scope, d.Device, d.Stage, d.Phase, action ?? d.Action, d.Reason, d.Overridden, d.Mode, d.Commanded, d.RefusedBy, inputs is null ? d.Inputs : d.Inputs with { Dispatch = inputs }, d.Origin, d.Group, target ?? d.Target);

    // DISP-10 and DISP-08: the elements are free to rotate, so the one on the phase with the most
    // headroom (L2, the lowest base load) goes first, and only one increase is in flight at a time:
    // the charger's restore from the startup hold (06:03:30) takes the next window, so the L3
    // element starts only at the first boundary after that window closed.
    [Fact]
    public void Rotation_starts_the_element_with_the_most_headroom_first_one_window_at_a_time()
    {
        var starts = run.Shadow.Decisions.Where(d => d.Origin == DecisionOrigin.Dispatch && d.Action == DecisionAction.SwitchOn).Take(2).ToList();
        var restore = DispatchSyntheticReplayRun.Of(run.Shadow, "ev_charger", DecisionAction.SetSetpoint).First();

        Assert.Equal(
            [("heater_l2", DispatchSyntheticReplayRun.At(6, 3)), ("heater_l3", DispatchSyntheticReplayRun.At(6, 4, 30))],
            starts.Select(d => (d.Device!.Value.Value, d.At)));
        Assert.All(starts, d => Assert.Equal(ReasonCode.HeadroomAvailable, d.Reason));
        Assert.Equal(DispatchSyntheticReplayRun.At(6, 3, 30), restore.At);
        Assert.DoesNotContain(run.Shadow.Decisions, d => d.Origin == DecisionOrigin.Dispatch && d.Action is DecisionAction.SwitchOn or DecisionAction.SetSetpoint && d.At > restore.At && d.At < starts[1].At);
    }

    // DISP-03 / 04 §3: the charger is worth more per kWh, so its setpoint takes L1's headroom first;
    // the L1 element (7 A plus the start hysteresis) does not fit next to even the charger's 6 A
    // floor, and starts only once protection has the charger at its minimum.
    [Fact]
    public void The_charger_worth_more_takes_the_L1_headroom_and_the_element_waits()
    {
        var firstL1Start = DispatchSyntheticReplayRun.Of(run.Shadow, "heater_l1", DecisionAction.SwitchOn).First();
        var chargerFloor = DispatchSyntheticReplayRun.Of(run.Shadow, "ev_charger", DecisionAction.ReduceToMinimum, DecisionOrigin.Guard).First();
        var at0700 = run.Shadow.Steps.Last(s => s.Evaluation.At <= DispatchSyntheticReplayRun.At(7, 0)).DispatchStatus!;
        var charger = at0700.Devices.Single(d => d.Id.Value == "ev_charger");
        var element = at0700.Devices.Single(d => d.Id.Value == "heater_l1");

        Assert.True(firstL1Start.At > chargerFloor.At, $"heater_l1 started at {firstL1Start.At:O}, before the charger was lowered at {chargerFloor.At:O}.");
        Assert.True(charger.Intended!.Setpoint!.Value.Value > 6, "The charger should hold more than its floor of L1's headroom.");
        Assert.True(charger.StartRank < element.StartRank);
        Assert.Equal(ReasonCode.HeadroomInsufficient, element.Reason);
    }

    // The live 0.3.0 finding: a setpoint is a ceiling, not a draw. The idle charger at 20 A relieves
    // nothing, so the guard's estimate on L1 is the measured current; and its ceiling is budgeted:
    // held at its 6 A floor by the startup safe state, the intended setpoint is restored (a recorded
    // would-increase from the held level, DISP-03) only to what fits under the reserve threshold,
    // never to the 20 A the idle car would "allow" if its draw were budgeted instead.
    [Fact]
    public void Idle_charger_at_a_20A_setpoint_relieves_nothing_and_is_budgeted_at_its_ceiling()
    {
        var l1 = run.Shadow.StatusAt(DispatchSyntheticReplayRun.At(7, 0)).Phases.Single(p => p.Phase == Phase.L1);
        var held = DispatchSyntheticReplayRun.Of(run.Shadow, "ev_charger", DecisionAction.SwitchOff, DecisionOrigin.Guard).First();
        var restored = DispatchSyntheticReplayRun.Of(run.Shadow, "ev_charger", DecisionAction.SetSetpoint).First();

        Assert.Equal(l1.Current!.Value.Value, l1.EffectiveCurrent!.Value.Value, 3);
        Assert.Equal(6, held.Target!.Setpoint!.Value.Value);
        Assert.Equal((DispatchSyntheticReplayRun.At(6, 3, 30), ReasonCode.HeadroomAvailable), (restored.At, restored.Reason));
        Assert.InRange(restored.Target!.Setpoint!.Value.Value, 6.5, 19.5);
    }

    // DISP-09: a reduction wanted inside min_on is recorded once as deferred and applied when the
    // lockout expires (the L3 element started 06:04:30, the kettle came at 06:06).
    [Fact]
    public void A_reduction_inside_min_on_is_deferred_once_and_applied_at_expiry()
    {
        var deferred = Assert.Single(run.Shadow.Decisions, d => d.Device?.Value == "heater_l3" && d.Action == DecisionAction.Deferred && d.At < DispatchSyntheticReplayRun.At(6, 20));
        var start = DispatchSyntheticReplayRun.Of(run.Shadow, "heater_l3", DecisionAction.SwitchOn).First();
        var stop = DispatchSyntheticReplayRun.Of(run.Shadow, "heater_l3", DecisionAction.SwitchOff).First();

        Assert.Equal((ReasonCode.ReserveThreshold, ReasonCode.LockoutMinOn), (deferred.Reason, deferred.RefusedBy));
        Assert.Equal(DispatchSyntheticReplayRun.At(6, 4, 30), start.At);
        Assert.Equal(start.At + run.Protection.Device(DeviceName.Parse("heater_l3"))!.MinOn, stop.At);
        Assert.Equal(deferred.At, stop.Inputs.Dispatch!.DeferredSince);
    }

    // 04 §3 persistence and hysteresis: the kettle (07:10-07:13) stops the L3 element after
    // persistence_down boundaries, and it starts again only after persistence_up calm boundaries.
    [Fact]
    public void A_kettle_stops_and_restarts_the_element_only_as_its_load_comes_and_goes()
    {
        var around = run.Shadow.Decisions.Where(d => d.Device?.Value == "heater_l3" && d.Origin == DecisionOrigin.Dispatch && d.At >= DispatchSyntheticReplayRun.At(7, 9) && d.At <= DispatchSyntheticReplayRun.At(7, 30)).ToList();

        Assert.Equal(
            [(DecisionAction.SwitchOff, DispatchSyntheticReplayRun.At(7, 11)), (DecisionAction.SwitchOn, DispatchSyntheticReplayRun.At(7, 16))],
            around.Select(d => (d.Action, d.At)));
    }

    // VER-02 lost sample: the L3 meter power is silent 10:30-10:33; the window opened by the
    // would-stop at 10:31 times out after settling.hard_timeout, once, and counts no mismatch.
    [Fact]
    public void A_lost_sample_times_out_the_settling_window_once_and_counts_no_mismatch()
    {
        var stop = DispatchSyntheticReplayRun.Of(run.Shadow, "heater_l3", DecisionAction.SwitchOff).Single(d => d.At >= DispatchSyntheticReplayRun.At(10, 30));
        var timedOut = Assert.Single(DispatchSyntheticReplayRun.Of(run.Shadow, "heater_l3", DecisionAction.SettlingTimedOut));
        var heater = run.Shadow.Steps[^1].DispatchStatus!.Devices.Single(d => d.Id.Value == "heater_l3");

        Assert.Equal(stop.At + run.Setup.Settling.HardTimeout, timedOut.At);
        Assert.Equal(ReasonCode.SettlingWindowOpen, timedOut.Reason);
        Assert.Equal(Verdict.TimedOut, timedOut.Inputs.Dispatch!.Verification!.Verdict);
        Assert.Contains(heater.History, h => h.Kind == HistoryKind.Command && h.Verdict == Verdict.TimedOut);
        Assert.Equal(0, heater.Mismatches);
        Assert.Null(heater.Unresponsive);
    }

    // Shadow mode applies nothing, so its windows close by the same rule but verify nothing.
    [Fact]
    public void Shadow_windows_close_by_rule_and_never_verify()
    {
        var commands = run.Shadow.Steps[^1].DispatchStatus!.Devices.SelectMany(d => d.History.Where(h => h.Kind == HistoryKind.Command)).ToList();

        Assert.NotEmpty(commands);
        Assert.All(commands, h => Assert.Contains(h.Verdict, new[] { Verdict.NotApplied, Verdict.TimedOut }));
        Assert.DoesNotContain(run.Shadow.Decisions, d => d.Action is DecisionAction.MarkedUnresponsive or DecisionAction.UnresponsiveCleared);
    }

    // 04 §4 / VER-02: every kettle step is a foreign load event on its own phase; the element someone
    // else switched on L2 is our own device (its reported state), never foreign.
    [Fact]
    public void Foreign_loads_are_recorded_on_their_phase_and_our_own_element_is_not_one()
    {
        var events = run.Shadow.Decisions.Where(d => d.Action == DecisionAction.ForeignLoadDetected).Select(d => (d.Phase!.Value, new DateTimeOffset(d.At.Year, d.At.Month, d.At.Day, d.At.Hour, d.At.Minute, d.At.Second, TimeSpan.Zero))).ToList();

        Assert.Equal(
            [
                (Phase.L3, DispatchSyntheticReplayRun.At(6, 6)),
                (Phase.L3, DispatchSyntheticReplayRun.At(7, 10)),
                (Phase.L2, DispatchSyntheticReplayRun.At(8, 30)),
                (Phase.L1, DispatchSyntheticReplayRun.At(9, 20)),
                (Phase.L3, DispatchSyntheticReplayRun.At(10, 30)),
            ],
            events);
        Assert.All(run.Shadow.Decisions.Where(d => d.Action == DecisionAction.ForeignLoadDetected), d => Assert.Equal(DecisionOrigin.Dispatch, d.Origin));
    }

    // DISP-07: another controller's switching fills the history with the measured step (a real
    // 1.7 kW element; an element whose thermostat is open draws nothing) and is never counted.
    [Fact]
    public void Someone_else_switching_an_element_is_observed_and_never_counted()
    {
        var devices = run.Shadow.Steps[^1].DispatchStatus!.Devices;
        var l2 = devices.Single(d => d.Id.Value == "heater_l2");
        var l3 = devices.Single(d => d.Id.Value == "heater_l3");

        var real = Assert.Single(l2.History, h => h.Kind == HistoryKind.Observed);
        Assert.Equal((Verdict.Match, MeasureUnit.Watts), (real.Verdict, real.Unit));
        Assert.Equal(-1700, real.Measured, 0.1 * 1700);
        Assert.Contains(l3.History, h => h.Kind == HistoryKind.Observed && h.Verdict == Verdict.Mismatch && Math.Abs(h.Measured) < 100);
        Assert.All([l2, l3], d => Assert.Equal((0, (UnresponsiveStatus?)null), (d.Mismatches, d.Unresponsive)));
    }

    // CONN-03: the carport breaker is enforced alongside the main limit, on its member only, and
    // every decision it causes names the group.
    [Fact]
    public void The_carport_group_is_enforced_on_its_member_and_named()
    {
        var group = GroupName.Parse("carport");

        Assert.Contains(run.Shadow.Decisions, d => d.Scope == DecisionScope.Group && d.Group == group);
        Assert.Contains(run.Shadow.Decisions, d => d.Device?.Value == "ev_charger" && d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.ReduceToMinimum && d.Group == group);
        Assert.DoesNotContain(run.Shadow.Decisions, d => d.Device is { } name && name.Value.StartsWith("heater_", StringComparison.Ordinal) && d.Group is not null);
    }

    // VER-02 unresponsive effector and DISP-07, with active semantics through the recording port:
    // the relays never follow (the recording cannot), so after mismatch_limit (3) consecutive
    // mismatches the element is marked, switched back off after min_on, excluded, and probed once
    // per unresponsive_retry_after; a failed probe marks it again at once.
    [Fact]
    public void An_element_that_never_follows_is_marked_unresponsive_after_three_mismatches_and_probed_after_the_retry_delay()
    {
        var settling = run.Setup.Settling;
        var marks = DispatchSyntheticReplayRun.Of(run.Active, "heater_l3", DecisionAction.MarkedUnresponsive).ToList();
        var commands = DispatchSyntheticReplayRun.Of(run.Active, "heater_l3", DecisionAction.SwitchOn).Concat(DispatchSyntheticReplayRun.Of(run.Active, "heater_l3", DecisionAction.SwitchOff)).Where(d => d.Commanded).OrderBy(d => d.At).ToList();
        var verified = commands.TakeWhile(d => d.At < marks[0].At).ToList();

        Assert.Equal(settling.MismatchLimit, verified.Count);
        Assert.Equal(Verdict.Mismatch, marks[0].Inputs.Dispatch!.Verification!.Verdict);
        var off = commands.First(d => d.At > marks[0].At);
        Assert.Equal((DecisionAction.SwitchOff, ReasonCode.DeviceUnresponsive), (off.Action, off.Reason));
        var probe = commands.First(d => d.At > off.At);
        Assert.Equal(DecisionAction.SwitchOn, probe.Action);
        Assert.True(probe.At >= marks[0].At + settling.UnresponsiveRetryAfter, $"Probed at {probe.At:O}, before the retry delay after {marks[0].At:O}.");
        Assert.Contains(marks, m => m.At > probe.At && m.At <= probe.At + settling.HardTimeout);
        Assert.Contains("heater_l3 relay 1 on", run.Port.Commands);
    }

    // DISP-07 for a setpoint: a car limits its own current, so an increase it does not draw is
    // inconclusive, never a mismatch; a decrease is verified strictly (still drawing above the new
    // ceiling is a mismatch).
    [Fact]
    public void A_charger_increase_it_does_not_draw_is_inconclusive_and_a_decrease_is_verified_strictly()
    {
        var history = run.Active.Steps[^1].DispatchStatus!.Devices.Single(d => d.Id.Value == "ev_charger").History;

        Assert.Contains(history, h => h.Kind == HistoryKind.Command && h.Unit == MeasureUnit.Amperes && h.Verdict == Verdict.Inconclusive && h.Measured < h.Expected);
        Assert.Contains(history, h => h.Kind == HistoryKind.Command && h.Unit == MeasureUnit.Amperes && h.Verdict == Verdict.Mismatch && h.Measured > h.Expected);
        Assert.DoesNotContain(run.Active.Decisions, d => d.Device?.Value == "ev_charger" && d.Action == DecisionAction.MarkedUnresponsive);
    }

    // SAFE-05 in the active run: every command the gate sent addresses a declared device.
    [Fact]
    public void Active_commands_address_only_declared_devices_and_keep_the_dispatch_rules()
    {
        var declared = run.Setup.Devices.Select(d => d.Id.Value).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(run.Port.Commands);
        Assert.All(run.Port.Commands, c => Assert.Contains(c.Split(' ')[0], declared));
        Assert.Empty(run.ActiveAnalyzer.IncreasesIntoTooLittleHeadroom());
        Assert.Empty(run.ActiveAnalyzer.IncreasesAgainstTheGuardOrAWindow());
        foreach (var line in run.ActiveAnalyzer.RepeatedIntents())
        {
            output.WriteLine(line);
        }

        Assert.Empty(run.ActiveAnalyzer.RepeatedIntents());
        Assert.Empty(run.ActiveAnalyzer.CausedViolations());
        Assert.Empty(new ViolationAnalyzer(run.Protection, run.Active.Observations, run.Active.Decisions, run.Active.Steps).MissedViolations());
    }
}
