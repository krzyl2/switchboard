using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// SAFE-03: the global kill switch holds every controlled device off within one cycle, whoever set
/// it and whatever the disk does; releasing it lets go only after persistence_up_cycles good
/// boundaries, and never starts anything.
/// </summary>
public sealed class GuardKillSwitchTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;

    [Fact]
    public void Holds_every_device_off_within_one_cycle()
    {
        // Set from Home Assistant: the guard is woken at once and evaluates at the same instant,
        // without waiting for a meter sample or a cycle boundary.
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.PushReference();
        h.Runner.Step();
        var before = h.Recorder.Decisions.Count;

        h.KillSwitch.Set(true, KillSwitchSource.Mqtt);
        h.Runner.Step();

        var decisions = h.Recorder.Decisions.Skip(before).ToList();
        Assert.Contains(decisions, d => d.Action == DecisionAction.KillSwitchEngaged && d.Inputs.KillSwitch == KillSwitchSource.Mqtt);
        var offs = decisions.Where(d => d.Action == DecisionAction.SwitchOff).ToList();
        Assert.Equal(["ev_charger", "heater_l1", "heater_l2", "heater_l3"], offs.Select(d => d.Device!.Value.Value).Order());
        Assert.All(offs, d => Assert.Equal((ReasonCode.KillSwitch, false, ReasonCode.ModeShadow), (d.Reason, d.Commanded, d.RefusedBy)));
        Assert.All(decisions, d => Assert.Equal(h.Now, d.At));
        Assert.True(h.Status.Current.KillSwitch.Engaged);
    }

    [Fact]
    public void Kill_switch_engages_when_state_file_unwritable()
    {
        // The switch changes memory and wakes the guard before anything is persisted: a full disk
        // (state.json unwritable) can neither delay nor undo it.
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.PushReference();
        h.Runner.Step();
        h.KillSwitch.Changed += (_, _) => throw new IOException("No space left on device.");

        h.KillSwitch.Set(true, KillSwitchSource.Api);
        h.Runner.Step();

        Assert.True(h.KillSwitch.Status.Engaged);
        Assert.Equal(4, h.Recorder.Decisions.Count(d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.KillSwitch));
    }

    [Fact]
    public void Kill_switch_release_waits_for_persistence_up()
    {
        // Releasing the switch ends its cause, not the holds: a flapping switch (or automation)
        // cannot chatter relays. Six good boundaries later each device is let go.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Api);
        s.Evaluate(GuardTrigger.KillSwitch);
        Assert.Equal(4, s.Intents.Count(i => i.Reason == ReasonCode.KillSwitch));

        s.RunUntil(T0 + TimeSpan.FromSeconds(15));
        s.KillSwitch = new KillSwitchStatus(false, s.Now, KillSwitchSource.Api);
        var released = s.Evaluate(GuardTrigger.KillSwitch);

        Assert.Contains(released.Events, e => e.Action == DecisionAction.KillSwitchDisengaged);
        Assert.Empty(released.Intents);
        Assert.All(s.Last.Status.Devices, d => Assert.Equal(DeviceGuardState.Shed, d.GuardState));
        s.Hold(Phase.L1, 8, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(180) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);
        s.RunUntil(T0 + TimeSpan.FromSeconds(180));
        Assert.Equal(4, s.Intents.Count(i => i.Target == GuardTarget.Released));
    }

    [Fact]
    public void Kill_switch_flicked_between_boundaries_waits_for_persistence_up()
    {
        // After 5 quiet minutes the release streak is long; a switch engaged at 12:05:15 and
        // released at 12:05:20 (an automation toggling it) must still wait six good boundaries
        // after it, 12:08:00, not let everything go at the next boundary, 12:05:30.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(315));
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Mqtt);
        Assert.Equal(4, s.Evaluate(GuardTrigger.KillSwitch).Intents.Length);
        s.RunUntil(T0 + TimeSpan.FromSeconds(320));
        s.KillSwitch = new KillSwitchStatus(false, s.Now, KillSwitchSource.Mqtt);
        s.Evaluate(GuardTrigger.KillSwitch);

        s.Hold(Phase.L1, 8, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(480) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Intents, i => i.Target == GuardTarget.Released);
        s.RunUntil(T0 + TimeSpan.FromSeconds(480));
        Assert.Equal(4, s.Intents.Count(i => i.Target == GuardTarget.Released));
    }

    [Fact]
    public void Release_returns_allowed_without_a_start()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Mqtt);
        s.Evaluate(GuardTrigger.KillSwitch);
        s.KillSwitch = KillSwitchStatus.Off;
        s.Evaluate(GuardTrigger.KillSwitch);
        s.Hold(Phase.L1, 8, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(5));

        Assert.All(s.Last.Status.Devices, d => Assert.Equal(DeviceGuardState.Allowed, d.GuardState));
        Assert.All(s.Intents, i => Assert.Contains(i.Target, new[] { GuardTarget.Off, GuardTarget.Released }));
        Assert.All(s.Decisions.Where(d => d.Action == DecisionAction.Release), d => Assert.Equal((false, ReasonCode.HeadroomAvailable), (d.Commanded, d.Reason)));
    }

    [Fact]
    public void Kill_switch_outranks_safe_state()
    {
        // Precedence: kill switch, then safe state. The log says who stopped everything.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Api);
        s.Invalidate(Phase.L1, ProtectionInvalidReason.CurrentStale, evaluate: false);

        var evaluation = s.Evaluate(GuardTrigger.KillSwitch);

        Assert.All(evaluation.Intents, i => Assert.Equal(ReasonCode.KillSwitch, i.Reason));
        Assert.True(evaluation.Status.KillSwitch.Engaged);
    }

    [Fact]
    public void Restored_kill_switch_holds_from_the_first_evaluation()
    {
        // Persisted in state.json: after a restart the first evaluation already holds everything off.
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.PushReference();
        h.KillSwitch.Restore(true, GuardScenario.Start - TimeSpan.FromHours(1));

        h.Runner.Step();

        Assert.Equal(KillSwitchSource.Restored, h.KillSwitch.Status.Source);
        Assert.Equal(4, h.Recorder.Decisions.Count(d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.KillSwitch));
    }
}
