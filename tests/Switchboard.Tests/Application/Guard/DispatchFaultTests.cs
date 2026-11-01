using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// UPGRADE.md P0 item 1: dispatch (economics, plan, EV, the allocation itself) runs in the guard's step, after the guard. An error there used to leave the step before
/// the effector gate applied the guard's evaluation, stop the guard loop and with it the add-on, and come back in the shutdown step, so the shutdown offs never
/// left. Protection outranks everything: a dispatch failure must cost dispatch, never a hold, a shed or a safe-state off.
/// </summary>
public sealed class DispatchFaultTests
{
    private static readonly DeviceName HeaterL1 = DeviceName.Parse("heater_l1");
    private static readonly DeviceName HeaterL2 = DeviceName.Parse("heater_l2");

    [Fact]
    public async Task A_dispatch_that_throws_at_every_step_never_stops_protection_or_the_loop()
    {
        // Why: with control on, the overload on L1 and then on L2 must be shed by commands that leave, step after step, although dispatch never once evaluates.
        var plan = new BrokenPlanView();
        var port = new RecordingDeviceCommandPort();
        var h = new GuardRunnerHarness(TestDevices.Document(), OperatingMode.Active, port: port, plan: plan);
        h.Control.Set(true, ControlSwitchSource.Api);
        h.PushReference(l1: 37.5);

        h.Runner.Step();

        Assert.True(plan.Observed > 0, "dispatch was not asked, so nothing failed");
        Assert.Equal(DeviceGuardState.Shed, h.Status.Current.Devices.Single(d => d.Id == HeaterL1).GuardState);
        Assert.Contains(port.Submitted, c => c.Device == HeaterL1 && !c.On);
        Assert.Contains(h.Recorder.Decisions, d => d.Device == HeaterL1 && d.Action == DecisionAction.SwitchOff && d.Origin == DecisionOrigin.Guard && d.Commanded);

        // The loop itself: a step that throws faults RunAsync, so a second shed only happens while the loop is alive.
        var stepped = new SemaphoreSlim(0);
        h.Runner.Stepped += (_, _) => stepped.Release();
        using var stop = new CancellationTokenSource();
        var run = h.Runner.RunAsync(stop.Token);
        await stepped.WaitAsync(TestContext.Current.CancellationToken).Soon();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Push(("sensor.meter_current_b", "40", "A"));
        while (h.Status.Current.Devices.Single(d => d.Id == HeaterL2).GuardState != DeviceGuardState.Shed)
        {
            await stepped.WaitAsync(TestContext.Current.CancellationToken).Soon();
        }

        Assert.False(run.IsCompleted, "the guard loop stopped");
        await stop.CancelAsync();
        await run.Soon();
        Assert.Contains(port.Submitted, c => c.Device == HeaterL2 && !c.On);
        Assert.Contains(h.Recorder.Decisions, d => d.Device == HeaterL2 && d.Action == DecisionAction.SwitchOff && d.Origin == DecisionOrigin.Guard && d.Commanded);
    }

    [Fact]
    public async Task The_shutdown_safe_state_is_sent_although_dispatch_fails_and_dispatch_is_not_asked_at_shutdown()
    {
        // Why: SAFE-01. The shutdown step used to run the whole step again, dispatch included; the same exception came back before the gate, so not one off left.
        var plan = new BrokenPlanView { Fail = false };
        var port = new RecordingDeviceCommandPort();
        var h = new GuardRunnerHarness(TestDevices.Document(), OperatingMode.Active, port: port, plan: plan);
        h.Control.Set(true, ControlSwitchSource.Api);
        h.PushReference();
        h.Runner.Step();
        Assert.Empty(port.Submitted);
        GuardStep? last = null;
        h.Runner.Stepped += (_, step) => last = step;
        plan.Fail = true;
        var asked = plan.Observed;
        Assert.True(asked > 0, "dispatch was not asked before the stop");

        await h.Runner.ShutdownAsync(TestContext.Current.CancellationToken);

        var offs = h.Recorder.Decisions.Where(d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.SafeState).ToList();
        Assert.Equal(["ev_charger", "heater_l1", "heater_l2", "heater_l3"], offs.Select(d => d.Device!.Value.Value).Order(StringComparer.Ordinal));
        Assert.All(offs, d => Assert.True(d.Commanded));
        Assert.Equal(["ev_charger", "heater_l1", "heater_l2", "heater_l3"], port.Submitted.Select(c => c.Device.Value).Distinct().Order(StringComparer.Ordinal));
        Assert.True(last!.Memory.CleanShutdown);

        // The shutdown step is the guard's alone: dispatch is not evaluated, whether it fails or not.
        Assert.Equal(asked, plan.Observed);
        Assert.Null(last.Dispatch);
    }

    [Fact]
    public async Task The_shutdown_step_keeps_the_overrides_in_force_so_it_records_none_cleared()
    {
        // Why: the shutdown step leaves dispatch out, not the user's overrides: the off of a forced-on heater says protection outranked the override, and the log
        // must not claim the override was cleared at the stop (it is still in state.json and in force after the restart).
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.PushReference();
        h.Loop.Overrides.Set(HeaterL1, OverrideMode.ForceOn, OverrideSource.Api);
        h.Runner.Step();
        Assert.Single(h.Recorder.Decisions, d => d.Action == DecisionAction.OverrideSet);

        await h.Runner.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.Single(h.Recorder.Decisions, d => d.Action == DecisionAction.OverrideSet);
        var off = Assert.Single(h.Recorder.Decisions, d => d.Device == HeaterL1 && d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.SafeState);
        Assert.Equal(ReasonCode.ManualOverride, off.Overridden);
    }

    [Fact]
    public void A_fault_is_one_decision_and_one_error_per_entry_and_its_recovery_is_logged()
    {
        // Why: a dispatch that fails at every store cycle would otherwise flood the decision log and the add-on log; the user needs to see that dispatch stopped
        // (dispatch_fault, with the error) and that it came back, not a line every few seconds.
        var plan = new BrokenPlanView();
        var logs = new LogCapture();
        var h = new GuardRunnerHarness(TestDevices.Document(), plan: plan, runnerLogger: logs.For<GuardRunner>());
        GuardStep? last = null;
        h.Runner.Stepped += (_, step) => last = step;
        h.PushReference();

        for (var i = 0; i < 3; i++)
        {
            h.Runner.Step();
            h.Time.Advance(TimeSpan.FromSeconds(10));
            h.PushReference();
        }

        var fault = Assert.Single(h.Recorder.Decisions, d => d.Reason == ReasonCode.DispatchFault);
        Assert.Equal((DecisionScope.System, DecisionAction.StopIncreases, DecisionOrigin.Dispatch, OperatingMode.Shadow), (fault.Scope, fault.Action, fault.Origin, fault.Mode));
        Assert.Equal(((DeviceName?)null, false, (ReasonCode?)null), (fault.Device, fault.Commanded, fault.RefusedBy));
        Assert.Equal(DecisionKind.Dispatch, DecisionKinds.Of(fault));
        var error = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("the plan view broke", error.Text, StringComparison.Ordinal);

        // During the fault the step carries no dispatch evaluation, and the dispatch status still follows the ledger.
        Assert.Null(last!.Dispatch);
        Assert.NotNull(last.DispatchStatus);
        Assert.Equal(h.Now - TimeSpan.FromSeconds(10), h.Loop.DispatchStatus.Current.At);

        // Dispatch is retried at every step: the first one that evaluates ends the fault, with one line and no decision.
        plan.Fail = false;
        h.Runner.Step();
        Assert.NotNull(last.Dispatch);
        Assert.Single(h.Recorder.Decisions, d => d.Reason == ReasonCode.DispatchFault);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Information && e.Text.Contains("Dispatch evaluates again", StringComparison.Ordinal));
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.PushReference();
        h.Runner.Step();
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Information && e.Text.Contains("Dispatch evaluates again", StringComparison.Ordinal));

        // A new fault is a new entry: one more decision, one more error.
        plan.Fail = true;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.PushReference();
        h.Runner.Step();
        Assert.Equal(2, h.Recorder.Decisions.Count(d => d.Reason == ReasonCode.DispatchFault));
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Error));
    }

    [Fact]
    public void A_dispatch_status_that_cannot_be_built_never_stops_protection_and_the_last_status_stays()
    {
        // Why: the dispatch status is built in the guard's step, after the gate applied protection. An error there used to leave the step, stop the loop and
        // the add-on; it must cost the status only: the overload is still shed by a command that leaves, the page keeps the last status it had, the step
        // says it has none (so no reader takes "none" for "no override, no lockout"), and the error is logged once per streak, not at every step.
        var logs = new LogCapture();
        var h = new GuardRunnerHarness(TestDevices.Document());
        var inputs = new StatusBreakingInputs(h.Holder);
        var port = new RecordingDeviceCommandPort();
        var loop = ControlLoop.WithControl(inputs, new SignalClock(h.Time), OperatingMode.Active, "boot1", port, runnerLogger: logs.For<GuardRunner>());
        loop.Control!.Set(true, ControlSwitchSource.Api);
        GuardStep? last = null;
        loop.Runner.Stepped += (_, step) => last = step;
        h.PushReference();
        loop.Runner.Step();
        var kept = loop.DispatchStatus.Current;
        Assert.Same(kept, last!.DispatchStatus);

        inputs.Break = true;
        for (var i = 0; i < 2; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(10));
            h.PushReference(l1: 37.5);
            loop.Runner.Step();
        }

        Assert.Equal(DeviceGuardState.Shed, loop.Status.Current.Devices.Single(d => d.Id == HeaterL1).GuardState);
        Assert.Contains(port.Submitted, c => c.Device == HeaterL1 && !c.On);
        Assert.Contains(loop.Recorder.Decisions, d => d.Device == HeaterL1 && d.Action == DecisionAction.SwitchOff && d.Origin == DecisionOrigin.Guard && d.Commanded);
        Assert.Same(kept, loop.DispatchStatus.Current);
        Assert.Null(last.DispatchStatus);
        Assert.Equal(1, StatusErrors());

        // The first status that builds again is published; a new failure is a new streak with its own error.
        inputs.Break = false;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.PushReference(l1: 37.5);
        loop.Runner.Step();
        Assert.Equal(h.Now, loop.DispatchStatus.Current.At);
        Assert.Same(loop.DispatchStatus.Current, last.DispatchStatus);
        inputs.Break = true;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.PushReference(l1: 37.5);
        loop.Runner.Step();
        Assert.Equal(2, StatusErrors());

        int StatusErrors() => logs.Entries.Count(e => e.Level == LogLevel.Error && e.Text.Contains("Building the dispatch status failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The harness's inputs, with a declared device the dispatch status cannot be built for (no stages) added to the dispatch setup while <see cref="Break"/>
    /// is set. The guard reads only the protection inputs and the gate only the devices protection declares, so protection does not see it.
    /// </summary>
    private sealed class StatusBreakingInputs(IControlInputSource inner) : IControlInputSource
    {
        public bool Break { get; set; }

        public ControlInputs Current => Break && inner.Current is { DispatchSetup: { } setup } current
            ? current with { DispatchSetup = setup with { Devices = setup.Devices.Add(setup.Devices[0] with { Spec = setup.Devices[0].Spec with { Id = DeviceName.Parse("ghost"), Stages = [] } }) } }
            : inner.Current;

        public event EventHandler<ControlInputs>? Published
        {
            add => inner.Published += value;
            remove => inner.Published -= value;
        }
    }

    /// <summary>A plan view whose Observe throws while <see cref="Fail"/> is set: dispatch calls it once per evaluation with a valid configuration.</summary>
    private sealed class BrokenPlanView : IPlanView
    {
        public bool Fail { get; set; } = true;

        public int Observed { get; private set; }

        public PlannerDispatchMode Mode => PlannerDispatchMode.Off;

        public PlanEntryView? For(DeviceName device, DateTimeOffset now) => null;

        public void Observe(DateTimeOffset now)
        {
            Observed++;
            if (Fail)
            {
                throw new InvalidOperationException("the plan view broke");
            }
        }
    }
}
