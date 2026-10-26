using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Host;

/// <summary>
/// SAFE-01 at an add-on stop with control in force: the shutdown safe state is not only recorded, it
/// is sent before the link closes. GuardService.StopAsync runs the shutdown step, waits (at most
/// HomeAssistant:ShutdownCommandWait) for the command path to go idle, and only then records what
/// came back: an off Home Assistant acknowledged is commanded, one it did not answer in time is
/// recorded as not answered (command_failed), never as commanded. Here the same order over the
/// real command port, with a channel the test answers; the port's timers run on a fake clock.
/// </summary>
public sealed class ShutdownCommandTests
{
    [Fact]
    public async Task The_shutdown_safe_state_is_sent_before_the_link_closes_and_recorded_with_what_came_back()
    {
        // The port's own view of the mode (option active, control allowed), as the harness's control
        // switch is set below; its outcomes wake its own inbox, which the test reads.
        var channel = new AnsweringChannel();
        var time = new ObservableTimeProvider();
        var directory = new ActuatorDirectoryHolder();
        var outcomes = new GuardInbox();
        var options = TestOptions.HomeAssistant();
        var port = new HomeAssistantDeviceCommandPort(channel, directory, TestModes.State(OperatingMode.Active, controlAllowed: true), outcomes, MsOptions.Create(options), time, NullLogger<HomeAssistantDeviceCommandPort>.Instance);
        var harness = new GuardRunnerHarness(TestDevices.Document(), OperatingMode.Active, port: port);
        directory.Observe(harness.Configuration.Configuration);
        using var stop = new CancellationTokenSource();
        var sender = port.RunAsync(stop.Token);
        harness.Control.Set(true, ControlSwitchSource.Api);
        harness.PushReference();
        harness.Runner.Step();
        Assert.Equal(0, channel.Count);

        // GuardService.StopAsync: the shutdown step, the bounded wait for the command path, the drain.
        await harness.Runner.ShutdownAsync(TestContext.Current.CancellationToken);
        var calls = new List<AnsweringChannel.Call>();
        for (var i = 0; i < 4; i++)
        {
            calls.Add(await channel.NextAsync());
        }

        Assert.Equal(
            ["number.charger_current set_value 6", "switch.heater_1 turn_off", "switch.heater_2 turn_off", "switch.heater_3 turn_off"],
            calls.Select(c => $"{c.Sent.Entity.Value} {HomeAssistantCommandAllowList.ServiceName(c.Sent.Service)}{(c.Sent.Value is { } v ? $" {v}" : string.Empty)}").Order(StringComparer.Ordinal));
        foreach (var call in calls.Where(c => c.Sent.Entity.Value != "switch.heater_3"))
        {
            call.Answer(CommandOutcome.Acknowledged);
        }

        // The three answers have reached the port (each outcome wakes the guard) before the wait runs out.
        for (var woken = 0; woken < 3;)
        {
            await outcomes.Reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().Soon();
            while (outcomes.Reader.TryRead(out var trigger))
            {
                woken += trigger == GuardTrigger.Command ? 1 : 0;
            }
        }

        var idle = port.WhenIdleAsync(options.ShutdownCommandWait, TestContext.Current.CancellationToken);
        await time.WaitForTimerAsync(options.ShutdownCommandWait);
        Assert.False(idle.IsCompleted, "the wait ended before the last off was answered or its time was up");
        time.Advance(options.ShutdownCommandWait);
        await idle.Soon();
        harness.Runner.Drain();

        var shutdown = harness.Recorder.Decisions.Where(d => d.Reason == ReasonCode.SafeState && d.Origin == DecisionOrigin.Guard && d.Device is not null).ToList();
        Assert.Equal(
            [("ev_charger", true, (ReasonCode?)null), ("heater_l1", true, null), ("heater_l2", true, null), ("heater_l3", false, ReasonCode.CommandFailed)],
            shutdown.Select(d => (d.Device!.Value.Value, d.Commanded, d.RefusedBy)).OrderBy(d => d.Value, StringComparer.Ordinal));
        Assert.All(shutdown, d => Assert.Equal(OperatingMode.Active, d.Mode));

        await stop.CancelAsync();
        await sender.Soon();
    }

    [Fact]
    public async Task Shutdown_sends_the_select_stop_before_the_link_closes()
    {
        // SAFE-01 at a stop, with a charger stopped through its select (0.7.1): the shutdown step
        // sends its floor setpoint and its stop option over the real command port, and the decision
        // is commanded once Home Assistant acknowledged both.
        var channel = new AnsweringChannel();
        var time = new ObservableTimeProvider();
        var directory = new ActuatorDirectoryHolder();
        var outcomes = new GuardInbox();
        var options = TestOptions.HomeAssistant();
        var port = new HomeAssistantDeviceCommandPort(channel, directory, TestModes.State(OperatingMode.Active, controlAllowed: true), outcomes, MsOptions.Create(options), time, NullLogger<HomeAssistantDeviceCommandPort>.Instance);
        var harness = new GuardRunnerHarness(TestDevices.Document(SelectStopRig.Charger), OperatingMode.Active, port: port);
        directory.Observe(harness.Configuration.Configuration);
        using var stop = new CancellationTokenSource();
        var sender = port.RunAsync(stop.Token);
        harness.Control.Set(true, ControlSwitchSource.Api);
        harness.Push(("number.charger_current", "10", "A"), ("select.charger_force", "0", null), ("sensor.meter_current_a", "12", "A"), ("sensor.meter_current_b", "5", "A"), ("sensor.meter_current_c", "5", "A"));
        harness.Runner.Step();
        Assert.Equal(0, channel.Count);

        await harness.Runner.ShutdownAsync(TestContext.Current.CancellationToken);
        var calls = new List<AnsweringChannel.Call> { await channel.NextAsync(), await channel.NextAsync() };

        Assert.Equal(
            ["number.charger_current set_value 6", "select.charger_force select_option 1"],
            calls.Select(c => $"{c.Sent.Entity.Value} {HomeAssistantCommandAllowList.ServiceName(c.Sent.Service)} {c.Sent.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? c.Sent.Option}").Order(StringComparer.Ordinal));
        foreach (var call in calls)
        {
            call.Answer(CommandOutcome.Acknowledged);
        }

        await port.WhenIdleAsync(options.ShutdownCommandWait, TestContext.Current.CancellationToken).Soon();
        harness.Runner.Drain();

        var shutdown = Assert.Single(harness.Recorder.Decisions, d => d.Reason == ReasonCode.SafeState && d.Origin == DecisionOrigin.Guard && d.Device?.Value == "ev_charger");
        Assert.Equal((DecisionAction.SwitchOff, true), (shutdown.Action, shutdown.Commanded));

        await stop.CancelAsync();
        await sender.Soon();
    }

    /// <summary>A connected link whose calls the test answers; the same allow-list check as the client.</summary>
    private sealed class AnsweringChannel : ICallServiceChannel
    {
        private readonly Channel<Call> _sent = System.Threading.Channels.Channel.CreateUnbounded<Call>();
        private int _count;

        public bool Ready => true;

        public long Generation => 1;

        public int Count => Volatile.Read(ref _count);

        public Task<CallOutcome> SendAsync(ActuatorCall call, ActuatorDeclaredCheck isDeclared, TimeSpan timeout, CancellationToken cancellationToken)
        {
            HomeAssistantCommandAllowList.EnsureCallServiceAllowed(CallServicePayload.Build(1, call).Span, isDeclared);
            Interlocked.Increment(ref _count);
            var answer = new TaskCompletionSource<CallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sent.Writer.TryWrite(new Call(call, answer));
            return answer.Task;
        }

        public async Task<Call> NextAsync() => await _sent.Reader.ReadAsync().AsTask().Soon();

        public sealed record Call(ActuatorCall Sent, TaskCompletionSource<CallOutcome> Completion)
        {
            public void Answer(CommandOutcome outcome) => Completion.TrySetResult(new CallOutcome(outcome, outcome == CommandOutcome.Acknowledged ? null : "test"));
        }
    }
}
