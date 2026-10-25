using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The only command port (SAFE-05). Its rules exist for protection: a stale "on" may never follow a
/// protective "off" for the same relay, an unresponsive entity may never delay another device's off,
/// nothing leaves once control is switched off (queued commands included), and a command is resolved
/// through the configuration loaded at send time, never redirected to a re-pointed entity.
/// </summary>
public sealed class HomeAssistantDeviceCommandPortTests : IAsyncDisposable
{
    private const string Heater1 = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35

        """;

    private const string Devices = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: heater_l2
            type: onoff
            actuator: switch.heater_2
            phase: L2
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
        """;

    private readonly SteppableTime _time = new(GuardScenario.Start);
    private readonly GuardInbox _inbox = new();
    private readonly FakeChannel _channel = new();
    private readonly ActuatorDirectoryHolder _directory = new();
    private readonly SwitchboardConfiguration _configuration = TestConfigurations.Valid(TestDevices.Document(Devices)).Configuration!;
    private readonly ControlSwitchState _control;
    private readonly HomeAssistantDeviceCommandPort _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private ModeStatus? _forced;

    public HomeAssistantDeviceCommandPortTests()
    {
        var clock = new SignalClock(_time);
        var killSwitch = new KillSwitchState(_inbox, clock, NullLogger<KillSwitchState>.Instance);
        _control = new ControlSwitchState(_inbox, killSwitch, clock, NullLogger<ControlSwitchState>.Instance);
        _control.Set(true, ControlSwitchSource.Api);
        var mode = new ModeState(OperatingMode.Active, _control);
        _directory.Observe(_configuration);
        _port = new HomeAssistantDeviceCommandPort(
            _channel,
            _directory,
            new ForcedMode(mode, () => _forced),
            _inbox,
            MsOptions.Create(TestOptions.HomeAssistant()),
            _time,
            NullLogger<HomeAssistantDeviceCommandPort>.Instance,
            _control);
        _run = _port.RunAsync(_stop.Token);
    }

    [Fact]
    public async Task A_newer_command_for_the_same_relay_replaces_one_not_sent_yet_so_a_stale_on_never_follows_an_off()
    {
        var on = _port.Submit(Relay("heater_l1", on: true));
        var off = _port.Submit(Relay("heater_l1", on: false));
        _port.Flush();

        var sent = await _channel.NextAsync();
        Assert.Equal(("switch.heater_1", ActuatorService.TurnOff), (sent.Call.Entity.Value, sent.Call.Service));
        sent.Answer(CommandOutcome.Acknowledged);
        await UntilAsync(() => Results().Count == 2);

        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.Superseded), Outcome(on));
        Assert.Equal((CommandOutcome.Acknowledged, (CommandNotSentReason?)null), Outcome(off));
        Assert.Equal(1, _channel.Sent);
    }

    [Fact]
    public async Task Different_actuators_are_pipelined_so_an_unanswered_one_never_delays_another_off()
    {
        _port.Submit(Relay("heater_l1", on: false));
        var second = _port.Submit(Relay("heater_l2", on: false));
        _port.Flush();

        var first = await _channel.NextAsync();
        var other = await _channel.NextAsync();
        Assert.Equal(["switch.heater_1", "switch.heater_2"], new[] { first.Call.Entity.Value, other.Call.Entity.Value }.Order(StringComparer.Ordinal));
        var heater2 = first.Call.Entity.Value == "switch.heater_2" ? first : other;
        heater2.Answer(CommandOutcome.Acknowledged);

        await UntilAsync(() => Results().Any(r => r.Ticket == second));
        Assert.Equal((CommandOutcome.Acknowledged, (CommandNotSentReason?)null), Outcome(second));
    }

    [Fact]
    public async Task One_actuator_has_at_most_one_command_in_flight()
    {
        _port.Submit(Relay("heater_l1", on: false));
        _port.Flush();
        var first = await _channel.NextAsync();

        var next = _port.Submit(Relay("heater_l1", on: true));
        await SendPassAsync();
        Assert.Equal(1, _channel.Sent);

        first.Answer(CommandOutcome.Acknowledged);
        var second = await _channel.NextAsync();
        Assert.Equal(ActuatorService.TurnOn, second.Call.Service);
        second.Answer(CommandOutcome.Acknowledged);
        await UntilAsync(() => Results().Any(r => r.Ticket == next));
    }

    [Fact]
    public async Task Switching_control_off_drops_every_queued_command_before_it_leaves()
    {
        var queued = _port.Submit(Relay("heater_l1", on: true));

        _control.Set(false, ControlSwitchSource.Api);
        await SendPassAsync();
        await UntilAsync(() => Results().Count == 1);

        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.ControlOff), Outcome(queued));
        Assert.Equal(0, _channel.Sent);
        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.ControlOff), Outcome(_port.Submit(Relay("heater_l1", on: false))));
    }

    [Fact]
    public async Task During_an_invalid_configuration_only_decreases_leave()
    {
        _forced = ModeState.Resolve(OperatingMode.Active, controlAllowed: true, configurationValid: false, stateConfirmed: true);

        var increase = _port.Submit(Relay("heater_l1", on: true));
        var decrease = _port.Submit(Relay("heater_l2", on: false));
        _port.Flush();
        var sent = await _channel.NextAsync();
        sent.Answer(CommandOutcome.Acknowledged);
        await UntilAsync(() => Results().Count == 2);

        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.ControlOff), Outcome(increase));
        Assert.Equal(("switch.heater_2", ActuatorService.TurnOff), (sent.Call.Entity.Value, sent.Call.Service));
        Assert.Equal(CommandOutcome.Acknowledged, Outcome(decrease).Outcome);
    }

    [Fact]
    public async Task In_shadow_nothing_leaves()
    {
        _forced = ModeState.Resolve(OperatingMode.Shadow, controlAllowed: true, configurationValid: true, stateConfirmed: true);

        var ticket = _port.Submit(Relay("heater_l1", on: false));
        _port.Flush();
        await UntilAsync(() => Results().Count == 1);

        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.ControlOff), Outcome(ticket));
        Assert.Equal(0, _channel.Sent);
    }

    [Fact]
    public async Task Without_a_link_a_command_is_not_sent_and_says_so()
    {
        _channel.Ready = false;

        var ticket = _port.Submit(Relay("heater_l1", on: false));
        _port.Flush();
        await UntilAsync(() => Results().Count == 1);

        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.LinkDown), Outcome(ticket));
    }

    [Fact]
    public async Task A_removed_devices_final_off_waits_for_the_link_instead_of_being_lost()
    {
        // A reload removed heater_l1, which Switchboard had started: its one final off may leave
        // once. The gate has already let go of the device, so nothing would ever send it again: with
        // Home Assistant reconnecting it waits for the link, neither answered nor used up.
        _directory.Observe(TestConfigurations.Valid(TestDevices.Document(Devices.Replace(Heater1, string.Empty, StringComparison.Ordinal))).Configuration!);
        _channel.Ready = false;
        var final = _port.Submit(Relay("heater_l1", on: false) with { Retiring = true });
        await SendPassAsync();
        Assert.Empty(Results());
        Assert.Equal(0, _channel.Sent);

        _channel.Ready = true;
        _port.Flush();
        var sent = await _channel.NextAsync();
        Assert.Equal(("switch.heater_1", ActuatorService.TurnOff), (sent.Call.Entity.Value, sent.Call.Service));
        sent.Answer(CommandOutcome.Acknowledged);
        await UntilAsync(() => Results().Any(r => r.Ticket == final));
        Assert.Equal(CommandOutcome.Acknowledged, Outcome(final).Outcome);
    }

    [Fact]
    public async Task A_device_re_pointed_since_the_decision_is_not_redirected_to_the_new_entity()
    {
        var ticket = _port.Submit(Relay("heater_l1", on: false));
        _directory.Observe(TestConfigurations.Valid(TestDevices.Document(Devices.Replace("switch.heater_1", "switch.heater_9", StringComparison.Ordinal))).Configuration!);
        _port.Flush();
        await UntilAsync(() => Results().Count == 1);

        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.Superseded), Outcome(ticket));
        Assert.Equal(0, _channel.Sent);
    }

    [Fact]
    public async Task A_setpoint_leaves_on_the_grid_of_its_actuator()
    {
        var ticket = _port.Submit(new DeviceCommand(DeviceName.Parse("ev_charger"), DeviceCommandKind.Setpoint, 1, true, new Amperes(13.6), CommandPriority.Ordinary) { Revision = Revision("ev_charger") });
        _port.Flush();

        var sent = await _channel.NextAsync();
        Assert.Equal(("number.charger_current", ActuatorService.SetValue, (double?)13), (sent.Call.Entity.Value, sent.Call.Service, sent.Call.Value));
        sent.Answer(CommandOutcome.Acknowledged);
        await UntilAsync(() => Results().Any(r => r.Ticket == ticket));
    }

    [Fact]
    public async Task A_call_the_allow_list_refuses_at_send_time_is_not_sent()
    {
        // The second check runs on the serialized bytes against the configuration loaded now.
        _channel.Refuse = true;

        var ticket = _port.Submit(Relay("heater_l1", on: false));
        _port.Flush();
        await UntilAsync(() => Results().Count == 1);

        Assert.Equal((CommandOutcome.NotSent, CommandNotSentReason.NotDeclared), Outcome(ticket));
    }

    [Fact]
    public async Task The_shutdown_waits_until_the_last_offs_are_answered()
    {
        _port.Submit(Relay("heater_l1", on: false));
        _port.Flush();
        var sent = await _channel.NextAsync();

        var idle = _port.WhenIdleAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(idle.IsCompleted);
        sent.Answer(CommandOutcome.Acknowledged);

        await idle.Soon();
        Assert.Equal(0, _port.Status.Pending);
        Assert.NotNull(_port.Status.LastAcknowledgedAt);
    }

    [Fact]
    public async Task A_failure_is_kept_as_the_last_failure_and_wakes_the_guard()
    {
        _port.Submit(Relay("heater_l1", on: false));
        _port.Flush();
        (await _channel.NextAsync()).Answer(CommandOutcome.Failed);

        // Waits for the wake itself (the outcome is queued a moment before the guard is posted), so
        // a guard that is never woken hangs here instead of passing by timing.
        await UntilAsync(() => Results().Count == 1 && Drained().Contains(GuardTrigger.Command));

        Assert.Equal((CommandOutcome.Failed, "heater_l1"), (_port.Status.LastFailure!.Outcome, _port.Status.LastFailure.Device.Value));
        Assert.Contains(GuardTrigger.Command, _triggers);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _run.Soon();
        _stop.Dispose();
    }

    private readonly List<CommandResult> _results = [];
    private readonly List<GuardTrigger> _triggers = [];

    private List<CommandResult> Results()
    {
        while (_port.TryTakeResult(out var result))
        {
            _results.Add(result);
        }

        return _results;
    }

    private (CommandOutcome Outcome, CommandNotSentReason? NotSent) Outcome(CommandTicket ticket)
    {
        var result = Results().Single(r => r.Ticket == ticket);
        return (result.Outcome, result.NotSent);
    }

    /// <summary>The guard triggers posted so far (the inbox drained into <see cref="_triggers"/>).</summary>
    private List<GuardTrigger> Drained()
    {
        while (_inbox.Reader.TryRead(out var trigger))
        {
            _triggers.Add(trigger);
        }

        return _triggers;
    }

    /// <summary>Every outcome wakes the guard: waits on the inbox until the condition holds (hang-guarded).</summary>
    private async Task UntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await _inbox.Reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().Soon();
            while (_inbox.Reader.TryRead(out var trigger))
            {
                _triggers.Add(trigger);
            }
        }
    }

    private long Revision(string device) => _configuration.Devices.Single(d => d.Id.Value == device).ActuatorRevision;

    /// <summary>Wakes the sender and waits for its pass to hand out what it sends: the end of "nothing more left", on no clock.</summary>
    private async Task SendPassAsync()
    {
        var pass = _port.NextSendPassAsync();
        _port.Flush();
        await pass.Soon();
    }

    private DeviceCommand Relay(string device, bool on) =>
        new(DeviceName.Parse(device), DeviceCommandKind.Relay, 1, on, null, on ? CommandPriority.Ordinary : CommandPriority.Protective) { Revision = Revision(device), Decrease = !on };

    /// <summary>The mode from the control switch, unless a test forces another.</summary>
    private sealed class ForcedMode(ModeState real, Func<ModeStatus?> forced) : IModeSource
    {
        public ModeStatus Current => forced() ?? real.Current;

        public ModeStatus For(bool configurationValid) => forced() ?? real.For(configurationValid);
    }

    /// <summary>A channel whose calls the test answers; it runs the same allow-list check as the client.</summary>
    private sealed class FakeChannel : ICallServiceChannel
    {
        private readonly Channel<SentCall> _sent = System.Threading.Channels.Channel.CreateUnbounded<SentCall>();
        private int _count;

        public bool Ready { get; set; } = true;

        public bool Refuse { get; set; }

        public long Generation => 1;

        public int Sent => Volatile.Read(ref _count);

        public Task<CallOutcome> SendAsync(ActuatorCall call, Func<EntityId, string, ActuatorService, double?, bool> isDeclared, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var payload = CallServicePayload.Build(1, call);
            HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload.Span, Refuse ? (_, _, _, _) => false : isDeclared);
            Interlocked.Increment(ref _count);
            var answer = new TaskCompletionSource<CallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sent.Writer.TryWrite(new SentCall(call, answer));
            return answer.Task;
        }

        public async Task<SentCall> NextAsync() => await _sent.Reader.ReadAsync().AsTask().Soon();
    }

    private sealed record SentCall(ActuatorCall Call, TaskCompletionSource<CallOutcome> Completion)
    {
        public void Answer(CommandOutcome outcome) => Completion.TrySetResult(new CallOutcome(outcome, outcome == CommandOutcome.Acknowledged ? null : "test"));
    }
}
