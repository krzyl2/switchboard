using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Effector;

/// <summary>
/// The effector gate (03, MODE-01, SAFE-07): in shadow mode every decision is recorded and none is
/// commanded; lockouts restart at process start and on every observed transition; protective
/// intents outrank lockouts and change limits and say which lockout they outranked; ordinary ones
/// do not; an unchanged decision is never logged twice.
/// </summary>
public sealed class EffectorGateTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;

    [Fact]
    public void Shadow_records_every_intent_commands_nothing()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.Sample(Phase.L1, 37.5);
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Api);
        s.Evaluate(GuardTrigger.KillSwitch);

        var device = s.Decisions.Where(d => d.Scope == DecisionScope.Device).ToList();
        Assert.Equal(s.Intents.Count, device.Count);
        Assert.All(device, d => Assert.Equal((false, ReasonCode.ModeShadow, OperatingMode.Shadow), (d.Commanded, d.RefusedBy, d.Mode)));
        Assert.All(s.Decisions.Where(d => d.Scope != DecisionScope.Device), d => Assert.Null(d.RefusedBy));
        Assert.DoesNotContain(s.Decisions, d => d.Commanded);
    }

    [Fact]
    public void Unchanged_target_and_reason_not_logged_again()
    {
        // MODE-03: state changes and refusals, not every unchanged cycle.
        var s = new GuardScenario(TestDevices.Document());
        var intent = Intent("heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded);

        var first = s.Gate.Apply(Evaluation(s, intent), s.Mode, s.Now);
        var second = s.Gate.Apply(Evaluation(s, intent), s.Mode, s.Now);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void Shadow_refusals_logged_once_per_change()
    {
        // Every shadow decision is a refusal (mode_shadow); refusals are deduplicated by what they
        // say, so a held state writes one line, and a new reason writes the next.
        var s = new GuardScenario(TestDevices.Document());

        var lines = new[]
        {
            Intent("heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded),
            Intent("heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded),
            Intent("heater_l1", GuardTarget.Off, ReasonCode.GuardStaleSignal),
            Intent("heater_l1", GuardTarget.Off, ReasonCode.GuardStaleSignal),
        }.SelectMany(i => s.Gate.Apply(Evaluation(s, i), s.Mode, s.Now)).ToList();

        Assert.Equal([ReasonCode.GuardTierExceeded, ReasonCode.GuardStaleSignal], lines.Select(d => d.Reason));
    }

    [Fact]
    public void Protective_shed_overrides_min_on_and_records_it()
    {
        // Lockouts never outrank protection: the heater, 6 min min_on running since start, is shed
        // at once, and the record says which lockout was overridden.
        var s = new GuardScenario(TestDevices.Document(), settledLockouts: false);
        s.SampleAll(8, 8, 8);
        s.Sample(Phase.L1, 37.5);

        var heater = s.Decisions.Single(d => d.Device?.Value == "heater_l1");
        Assert.Equal((DecisionAction.SwitchOff, ReasonCode.LockoutMinOn, ReasonCode.ModeShadow), (heater.Action, heater.Overridden, heater.RefusedBy));
        Assert.True(heater.Inputs.Device!.MinOnRemaining > TimeSpan.Zero);
    }

    [Fact]
    public void Ordinary_off_during_min_on_refused()
    {
        var s = new GuardScenario(TestDevices.Document(), settledLockouts: false);
        s.SampleAll(8, 8, 8);

        var decision = Assert.Single(s.Gate.Apply(Evaluation(s, Intent("heater_l1", GuardTarget.Off, ReasonCode.ValueRankBelowCutoff, IntentUrgency.Ordinary)), s.Mode, s.Now));

        Assert.Equal((ReasonCode.LockoutMinOn, false), (decision.RefusedBy, decision.Commanded));
    }

    [Fact]
    public void Observed_state_change_restarts_lockout()
    {
        // Lockouts count from the actual transition (03): the heater observed off at 12:10 starts
        // its 90 s min_off then; observed on again at 12:11, its 6 min min_on starts then.
        var s = new GuardScenario(TestDevices.Document(), settledLockouts: false);
        var heater = DeviceName.Parse("heater_l1");
        s.SampleAll(8, 8, 8);
        s.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.Zero, s.Lockouts.MinOnRemaining(heater, 1, s.Now));

        s.SetState("heater_l1", new RelayApplied(false));
        s.Evaluate();
        Assert.Equal(TimeSpan.FromSeconds(90), s.Lockouts.MinOffRemaining(heater, 1, s.Now));
        Assert.Equal(TimeSpan.Zero, s.Lockouts.MinOnRemaining(heater, 1, s.Now));

        s.Time.Advance(TimeSpan.FromMinutes(1));
        s.SetState("heater_l1", new RelayApplied(true));
        s.Evaluate();
        Assert.Equal(TimeSpan.FromMinutes(6), s.Lockouts.MinOnRemaining(heater, 1, s.Now));
    }

    [Fact]
    public void Lockouts_restart_on_a_backward_clock_step()
    {
        var s = new GuardScenario(TestDevices.Document());
        var heater = DeviceName.Parse("heater_l1");
        Assert.Equal(TimeSpan.Zero, s.Lockouts.MinOnRemaining(heater, 1, s.Now));

        s.Lockouts.RestartAll(s.Now);

        Assert.Equal(TimeSpan.FromMinutes(6), s.Lockouts.MinOnRemaining(heater, 1, s.Now));
    }

    [Fact]
    public void Ordinary_changes_beyond_step_limit_wait()
    {
        // max_step_change_stages = 1 (07-PITFALLS, shutdown avalanche): the second stage of the
        // same device waits for the next cycle; dispatch proposes it again then.
        var s = new GuardScenario(TestDevices.Document(Boiler));
        s.SampleAll(8, 8, 8);
        var stage1 = Intent("boiler", GuardTarget.Off, ReasonCode.ValueRankBelowCutoff, IntentUrgency.Ordinary, stage: 1);
        var stage2 = Intent("boiler", GuardTarget.Off, ReasonCode.ValueRankBelowCutoff, IntentUrgency.Ordinary, stage: 2);

        var now = s.Gate.Apply(Evaluation(s, stage1, stage2), s.Mode, s.Now);
        Assert.Equal([ReasonCode.ModeShadow, ReasonCode.SettlingWindowOpen], now.Select(d => d.RefusedBy!.Value));

        var next = s.Gate.Apply(Evaluation(s, cycleOffset: 1, stage2), s.Mode, s.Now + TimeSpan.FromSeconds(30));
        Assert.Equal(ReasonCode.ModeShadow, Assert.Single(next).RefusedBy);
    }

    [Fact]
    public void Protective_shed_all_not_step_limited()
    {
        var s = new GuardScenario(TestDevices.Document(Boiler));
        s.SampleAll(8, 8, 8);

        var evaluation = s.Sample(Phase.L1, 37.5);

        Assert.Equal(2, evaluation.Intents.Length);
        var boiler = s.Decisions.Where(d => d.Device?.Value == "boiler").ToList();
        Assert.Equal([1, 2], boiler.Select(d => d.Stage!.Value).Order());
        Assert.All(boiler, d => Assert.Equal(ReasonCode.ModeShadow, d.RefusedBy));
    }

    [Fact]
    public void Active_mode_never_commands_in_this_version()
    {
        // Even a forced active effective mode records shadow: there is no command path to use.
        var s = new GuardScenario(TestDevices.Document());
        var forced = new ModeStatus(OperatingMode.Active, OperatingMode.Active, OperatingMode.Active, ModeNote.None);

        var decision = Assert.Single(s.Gate.Apply(Evaluation(s, Intent("heater_l1", GuardTarget.Off, ReasonCode.GuardTierExceeded)), forced, s.Now));

        Assert.Equal((false, OperatingMode.Shadow, ReasonCode.ModeShadow), (decision.Commanded, decision.Mode, decision.RefusedBy));
    }

    private const string Boiler = """
          - id: boiler
            type: stepped
            value_per_kwh: 0.3
            stages:
              - { actuator: switch.boiler_1, phase: L1, rated_current_a: 6.6, rated_power_w: 1500 }
              - { actuator: switch.boiler_2, phase: L1, rated_current_a: 6.6, rated_power_w: 1500 }
        """;

    private static GuardIntent Intent(string device, GuardTarget target, ReasonCode reason, IntentUrgency urgency = IntentUrgency.Protective, int? stage = null) =>
        new(DeviceName.Parse(device), stage, target, reason, Phase.L1, urgency, null, DecisionInputs.None);

    private static GuardEvaluation Evaluation(GuardScenario s, params GuardIntent[] intents) => Evaluation(s, 0, intents);

    private static GuardEvaluation Evaluation(GuardScenario s, int cycleOffset, params GuardIntent[] intents) =>
        new(s.Now, GuardStatus.Initial(s.Now, s.Mode), [.. intents], ImmutableArray<GuardEvent>.Empty, s.Setup.Timing, ((s.Now - DateTimeOffset.UnixEpoch).Ticks / s.Setup.Timing.Cycle.Ticks) + cycleOffset);
}
