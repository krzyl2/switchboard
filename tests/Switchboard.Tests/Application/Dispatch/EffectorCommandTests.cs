using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The effector gate's command side, driven through the recording port (the only implementation,
/// in the tests) with active semantics: what reaches the port, when, and how its result is judged
/// (DISP-07, DISP-09, VER-02). A setpoint is a ceiling, not a draw: a car that takes less than it
/// may is the car's choice, never an unresponsive charger; a charger that keeps drawing above a
/// lowered ceiling is. In shadow mode a port, even when present, is never called.
/// </summary>
public sealed class EffectorCommandTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);
    private const string Fast = "  dispatch:\n    persistence_up_cycles: 1\n    relay_min_on: 0s\n    relay_min_off: 0s\n  settling:\n    unresponsive_retry_after: 2m\n";

    [Fact]
    public void Car_limited_draw_after_setpoint_increases_never_marks_the_charger_unresponsive()
    {
        // The charger at its 6 A floor, the car drawing 6 A and never more: dispatch raises the
        // setpoint cycle after cycle (the charger accepts it), the draw stays at 6 A. Every window
        // closes inconclusive; no mismatch is counted, the highest-value device is never excluded.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndCharger, ["heater_l1", "ev_charger"], Fast, mode: DispatchScenario.Active, port: port);
        s.Charger(6, 6);

        for (var cycle = 1; cycle <= 6; cycle++)
        {
            s.PollUntil(T0 + (cycle * Cycle), 9, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 6));
            if (s.Ledger("ev_charger")?.Target?.Setpoint is { } target)
            {
                s.SetState("ev_charger", new SetpointApplied(target));
            }
        }

        Assert.Contains(port.Commands, c => c.StartsWith("ev_charger setpoint ", StringComparison.Ordinal) && c != "ev_charger setpoint 6");
        var verdicts = s.Ledger("ev_charger")!.History.Where(h => h.Kind == Switchboard.Application.Effector.HistoryKind.Command).Select(h => h.Verdict).ToList();
        Assert.NotEmpty(verdicts);
        Assert.All(verdicts, v => Assert.Equal(Verdict.Inconclusive, v));
        Assert.Equal(0, s.Ledger("ev_charger")!.Mismatches);
        Assert.DoesNotContain(s.Decisions, d => d.Action == DecisionAction.MarkedUnresponsive);
    }

    [Fact]
    public void Charger_that_keeps_drawing_above_a_lowered_setpoint_is_a_mismatch()
    {
        // A decrease is verified strictly: forced off (to its floor), the charger keeps drawing 16 A;
        // the window closes on the first reports after command + its 20 s reaction time.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndCharger, ["heater_l1"], Fast, mode: DispatchScenario.Active, port: port);
        s.Charger(16, 16);
        s.PollUntil(T0 + TimeSpan.FromSeconds(20), 19, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 16));

        s.Override("ev_charger", OverrideMode.ForceOff);
        s.PollUntil(T0 + TimeSpan.FromSeconds(70), 19, 3, 3, refresh: () => s.SetDraw("ev_charger", Phase.L1, 16));

        Assert.Equal("ev_charger setpoint 6", port.Commands[^1]);
        var record = s.Ledger("ev_charger")!.History.Last(h => h.Kind == Switchboard.Application.Effector.HistoryKind.Command);
        Assert.Equal((Verdict.Mismatch, 6.0, 16.0, MeasureUnit.Amperes), (record.Verdict, record.Expected, record.Measured, record.Unit));
        Assert.Equal(1, s.Ledger("ev_charger")!.Mismatches);
    }

    [Fact]
    public void A_port_in_shadow_mode_is_never_called()
    {
        // Even with a port handed in, shadow mode commands nothing: every start is recorded as
        // refused by mode_shadow, commanded false, and the port stays silent.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1", "heater_l2"], Fast, port: port);

        s.PollUntil(T0 + TimeSpan.FromMinutes(3), 3, 3, 3);

        Assert.Contains(s.Decisions, d => d.Action == DecisionAction.SwitchOn);
        Assert.All(s.Decisions.Where(d => d.Scope == DecisionScope.Device), d => Assert.False(d.Commanded));
        Assert.Empty(port.Commands);
    }

    [Fact]
    public void Deferred_start_reaches_the_port_at_the_first_boundary_after_min_off_expires()
    {
        // DISP-09 in active semantics: after a restart min_off runs until 12:01:45; dispatch wants the
        // heater from the 12:00:30 boundary. Nothing reaches the port before the expiry, nor at it
        // (between boundaries an increase is never made on an old wish); at the 12:02:00 boundary,
        // with every gate re-checked, the deferred start is commanded, once.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 1\n", mode: DispatchScenario.Active, port: port, settledLockouts: false, start: T0 + TimeSpan.FromSeconds(5));

        s.PollUntil(T0 + TimeSpan.FromSeconds(119), 3, 3, 3);
        Assert.Empty(port.Commands);
        Assert.Equal(ReasonCode.LockoutMinOff, Assert.Single(s.DecisionsFor("heater_l1")).RefusedBy);

        s.PollUntil(T0 + (4 * Cycle), 3, 3, 3);

        Assert.Equal(["heater_l1 relay 1 on"], port.Commands);
        var start = s.DecisionsFor("heater_l1").Last();
        Assert.Equal((DecisionAction.SwitchOn, T0 + (4 * Cycle), true, (ReasonCode?)null), (start.Action, start.At, start.Commanded, start.RefusedBy));
    }

    [Fact]
    public void Re_asserting_a_hold_after_an_external_re_enable_is_one_recorded_command()
    {
        // Every command carries a decision with a reason. The kill switch holds the heater off (one
        // recorded, commanded switch-off). Another controller switches it back on: in active mode
        // the target follows reality, the guard's hold is still in force but makes no new intent,
        // so the gate re-asserts it: the second "off" reaching the port must have its own record.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, [], Fast, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromSeconds(20), 9.6, 9.6, 9.6);
        s.KillSwitch = new Switchboard.Application.Guard.KillSwitchStatus(true, s.Now, Switchboard.Application.Guard.KillSwitchSource.Api);
        s.Evaluate();
        s.SetState("heater_l1", new RelayApplied(false));
        s.PollUntil(T0 + TimeSpan.FromSeconds(40), 3, 3, 3);
        Assert.Single(port.Commands, c => c == "heater_l1 relay 1 off");

        s.SetState("heater_l1", new RelayApplied(true));
        s.Evaluate();

        Assert.Equal(2, port.Commands.Count(c => c == "heater_l1 relay 1 off"));
        var offs = s.DecisionsFor("heater_l1").Where(d => d.Action == DecisionAction.SwitchOff && d.Commanded).ToList();
        Assert.Equal(2, offs.Count);
        Assert.Equal((s.Now, DecisionOrigin.Guard, ReasonCode.KillSwitch, (ReasonCode?)null), (offs[1].At, offs[1].Origin!.Value, offs[1].Reason, offs[1].RefusedBy));
    }

    [Fact]
    public void With_active_semantics_a_change_that_needs_no_command_is_not_refused_by_shadow_mode()
    {
        // mode_shadow says shadow mode stopped a change. With active semantics the kill switch's
        // hold is commanded, and its later release needs no command at all (the guard never starts
        // anything): the release is recorded as active, not commanded, and refused by nothing.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l2", "heater_l3"], Fast, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromSeconds(20), 9.6, 3, 3);
        s.KillSwitch = new Switchboard.Application.Guard.KillSwitchStatus(true, s.Now, Switchboard.Application.Guard.KillSwitchSource.Api);
        s.Evaluate();
        s.SetState("heater_l1", new RelayApplied(false));
        s.KillSwitch = new Switchboard.Application.Guard.KillSwitchStatus(false, s.Now, Switchboard.Application.Guard.KillSwitchSource.Api);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);

        var hold = Assert.Single(s.DecisionsFor("heater_l1"), d => d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff);
        Assert.Equal((true, (ReasonCode?)null), (hold.Commanded, hold.RefusedBy));
        var releases = s.Decisions.Where(d => d.Action == DecisionAction.Release).ToList();
        Assert.NotEmpty(releases);
        Assert.All(releases, d => Assert.Equal((Switchboard.Application.Modes.OperatingMode.Active, false, (ReasonCode?)null), (d.Mode, d.Commanded, d.RefusedBy)));
    }

    [Fact]
    public void Failed_probe_re_marks_at_once_without_counting_to_the_limit_again()
    {
        // A relay that ignores every command is marked unresponsive after mismatch_limit (3)
        // mismatches. Its probe after unresponsive_retry_after fails too: it is marked again at once,
        // with the next probe one retry delay later; it does not get three more tries.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], Fast, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOff);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(30), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + (3 * Cycle) + TimeSpan.FromSeconds(10), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.Auto);
        var first = Assert.Single(s.Decisions, d => d.Action == DecisionAction.MarkedUnresponsive);
        var retry = s.Ledger("heater_l1")!.RetryAt!.Value;

        var probe = new DateTimeOffset((retry.UtcTicks + Cycle.Ticks - 1) / Cycle.Ticks * Cycle.Ticks, TimeSpan.Zero);
        s.PollUntil(probe + TimeSpan.FromSeconds(10), 3, 3, 3);

        Assert.Equal("heater_l1 relay 1 on", port.Commands[^1]);
        var marks = s.Decisions.Where(d => d.Action == DecisionAction.MarkedUnresponsive).ToList();
        Assert.Equal(2, marks.Count);
        Assert.Equal(probe + TimeSpan.FromSeconds(10), marks[1].At);
        Assert.Equal(first.At, s.Ledger("heater_l1")!.UnresponsiveSince);
        Assert.Equal(marks[1].At + TimeSpan.FromMinutes(2), s.Ledger("heater_l1")!.RetryAt);
    }
}
