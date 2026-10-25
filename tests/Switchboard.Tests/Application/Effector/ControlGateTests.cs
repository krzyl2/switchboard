using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Effector;

/// <summary>
/// MODE-02 and SAFE-01/03 with the real command path: commands leave only while the add-on option
/// is active, the control switch is on, the configuration is valid and the state is confirmed. With
/// anything missing every decision is still computed and logged, with the reason nothing was sent;
/// the exceptions are protective: an invalid configuration under an active, allowed control still
/// sends the offs of the safe state to the devices of the last valid file, and nothing else.
/// </summary>
public sealed class ControlGateTests
{
    [Fact]
    public void With_the_option_in_shadow_nothing_is_sent_even_with_control_allowed()
    {
        var (harness, port) = Overloaded(OperatingMode.Shadow, allowed: true);

        Assert.Empty(port.Submitted);
        var sheds = Sheds(harness);
        Assert.NotEmpty(sheds);
        Assert.All(sheds, d => Assert.Equal((false, ReasonCode.ModeShadow), (d.Commanded, d.RefusedBy)));
    }

    [Fact]
    public void With_the_option_active_but_control_off_nothing_is_sent()
    {
        var (harness, port) = Overloaded(OperatingMode.Active, allowed: false);

        Assert.Empty(port.Submitted);
        Assert.All(Sheds(harness), d => Assert.Equal((false, ReasonCode.ModeShadow), (d.Commanded, d.RefusedBy)));
        var mode = harness.Recorder.Decisions.Last(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal([ModeCondition.ControlOff], mode.Inputs.Mode!.Missing);
    }

    [Fact]
    public void Active_with_control_allowed_sends_the_protective_off_and_records_it_commanded_once_acknowledged()
    {
        var (harness, port) = Overloaded(OperatingMode.Active, allowed: true);

        Assert.NotEmpty(port.Submitted);
        Assert.All(port.Submitted, c => Assert.Equal(CommandPriority.Protective, c.Priority));
        var sheds = Sheds(harness);
        Assert.NotEmpty(sheds);
        Assert.All(sheds, d => Assert.Equal((true, (ReasonCode?)null), (d.Commanded, d.RefusedBy)));
        Assert.All(sheds, d => Assert.Contains(port.Submitted, c => c.Device == d.Device));
    }

    [Fact]
    public void A_failed_protective_command_is_not_commanded_and_names_the_device_unresponsive()
    {
        var port = new RecordingDeviceCommandPort { Answer = _ => (CommandOutcome.Failed, null) };
        var (harness, _) = Overloaded(OperatingMode.Active, allowed: true, port);

        var sheds = Sheds(harness);
        Assert.NotEmpty(sheds);
        Assert.All(sheds, d => Assert.Equal((false, ReasonCode.DeviceUnresponsive), (d.Commanded, d.RefusedBy)));
    }

    [Fact]
    public void A_command_the_link_could_not_carry_is_not_commanded()
    {
        var port = new RecordingDeviceCommandPort { Answer = _ => (CommandOutcome.NotSent, CommandNotSentReason.LinkDown) };
        var (harness, _) = Overloaded(OperatingMode.Active, allowed: true, port);

        Assert.All(Sheds(harness), d => Assert.False(d.Commanded));
    }

    [Fact]
    public void The_kill_switch_sends_every_off_once()
    {
        // SAFE-03 with commands: every controlled device off (the charger, without a stop actuator,
        // to its minimum), each command exactly once.
        var port = new RecordingDeviceCommandPort();
        var harness = Running(port, OperatingMode.Active, allowed: true);

        harness.KillSwitch.Set(true, KillSwitchSource.Api);
        harness.Runner.Step();
        var sent = port.Commands.ToList();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        harness.PushReference();
        harness.Runner.Step();

        Assert.Equal(["ev_charger setpoint 6", "heater_l1 relay 1 off", "heater_l2 relay 1 off", "heater_l3 relay 1 off"], sent.Order(StringComparer.Ordinal));
        Assert.Equal(sent, port.Commands);
    }

    [Fact]
    public void Switching_control_off_sends_nothing_and_leaves_every_device_where_it_is()
    {
        var port = new RecordingDeviceCommandPort();
        var harness = Running(port, OperatingMode.Active, allowed: true);
        var before = port.Commands.Count;

        harness.Control.Set(false, ControlSwitchSource.Api);
        harness.Runner.Step();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        harness.PushReference(l1: 33);
        harness.Runner.Step();

        Assert.Equal(before, port.Commands.Count);
        var mode = harness.Recorder.Decisions.Last(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal((OperatingMode.Shadow, false), (mode.Mode, mode.Inputs.Mode!.ControlAllowed));
        Assert.Equal(ControlSwitchSource.Api, mode.Inputs.Mode.Source);
        Assert.All(Sheds(harness).Where(d => d.At > mode.At), d => Assert.Equal(ReasonCode.ModeShadow, d.RefusedBy));
    }

    [Fact]
    public void An_invalid_configuration_sends_only_the_offs_of_the_safe_state_to_the_last_valid_devices()
    {
        // 04 §8: safe state on an invalid configuration means every controlled device off. Under an
        // active, allowed control the offs leave (decreases only); nothing is ever raised.
        var port = new RecordingDeviceCommandPort();
        var harness = Running(port, OperatingMode.Active, allowed: true);
        var before = port.Commands.Count;

        harness.Store.ApplyConfiguration(TestConfigurations.Parse(TestDevices.Document().Replace("rating_a:", "ratting_a:", StringComparison.Ordinal)), harness.Now);
        harness.Publish();
        harness.Runner.Step();

        var sent = port.Submitted.Skip(before).ToList();
        Assert.NotEmpty(sent);
        Assert.All(sent, c => Assert.True(c.Decrease, $"{c.Device} {c.Kind} {c.On} {c.Setpoint} is not a decrease"));
        Assert.Contains(sent, c => c.Device.Value == "heater_l1" && !c.On);
        var mode = harness.Recorder.Decisions.Last(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal([ModeCondition.ConfigurationInvalid], mode.Inputs.Mode!.Missing);
        Assert.Equal(ReasonCode.SafeState, mode.Reason);
    }

    [Fact]
    public void A_locked_state_keeps_control_off_and_the_kill_switch_on_until_confirmed()
    {
        var port = new RecordingDeviceCommandPort();
        var harness = new GuardRunnerHarness(TestDevices.Document(), OperatingMode.Active, port: port);
        harness.Control.Lock(ControlLockReason.StateFileCorrupt);
        harness.PushReference();
        harness.Runner.Step();

        Assert.Equal(ControlSetResult.RefusedLocked, harness.Control.Set(true, ControlSwitchSource.Api));
        Assert.True(harness.KillSwitch.Status.Engaged);
        Assert.Empty(port.Submitted);
        var mode = harness.Recorder.Decisions.Last(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal([ModeCondition.ControlOff, ModeCondition.StateUnconfirmed], mode.Inputs.Mode!.Missing);

        Assert.True(harness.Control.Confirm());
        harness.Runner.Step();
        Assert.False(harness.KillSwitch.Status.Engaged);
        Assert.False(harness.Control.Status.Allowed);
        Assert.Empty(port.Submitted);
    }

    [Fact]
    public void Every_change_of_the_mode_is_a_mode_decision_with_what_is_missing()
    {
        var port = new RecordingDeviceCommandPort();
        var harness = new GuardRunnerHarness(TestDevices.Document(), OperatingMode.Active, port: port);
        harness.PushReference();
        harness.Runner.Step();
        harness.Control.Set(true, ControlSwitchSource.Mqtt);
        harness.Runner.Step();
        harness.Runner.Step();

        var modes = harness.Recorder.Decisions.Where(d => d.Action == DecisionAction.ModeResolved).ToList();
        Assert.Equal([OperatingMode.Shadow, OperatingMode.Active], modes.Select(d => d.Mode));
        Assert.Equal(ControlSwitchSource.Mqtt, modes[1].Inputs.Mode!.Source);
        Assert.Empty(modes[1].Inputs.Mode!.Missing);
    }

    private static List<Decision> Sheds(GuardRunnerHarness harness) =>
        [.. harness.Recorder.Decisions.Where(d => d.Origin == DecisionOrigin.Guard && d.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum)];

    /// <summary>The reference running quietly for one step, then L1 well above its breaker.</summary>
    private static (GuardRunnerHarness Harness, RecordingDeviceCommandPort Port) Overloaded(OperatingMode ceiling, bool allowed, RecordingDeviceCommandPort? port = null)
    {
        port ??= new RecordingDeviceCommandPort();
        var harness = Running(port, ceiling, allowed);
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        harness.PushReference(l1: 33);
        harness.Runner.Step();
        return (harness, port);
    }

    private static GuardRunnerHarness Running(RecordingDeviceCommandPort port, OperatingMode ceiling, bool allowed)
    {
        var harness = new GuardRunnerHarness(TestDevices.Document(), ceiling, port: port);
        if (allowed)
        {
            harness.Control.Set(true, ControlSwitchSource.Api);
        }

        harness.PushReference();
        harness.Runner.Step();
        return harness;
    }
}
