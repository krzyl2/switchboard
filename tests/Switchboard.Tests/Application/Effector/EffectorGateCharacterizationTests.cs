using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Effector;

/// <summary>
/// Pins what the gate does, step by step, before and after it is split into smaller parts (UPGRADE.md,
/// EffectorGate): every decision (id order, action, reason, origin, target, outcome), every command sent
/// and the ledger at the end, for the paths the split moves code between: arbitration (protection,
/// override, dispatch), settling and verification, restore baselines, foreign transitions, pending
/// command tracking and failures, the shutdown drain and the guard-only step. The traces under Golden/
/// were recorded on the unsplit gate and are never edited to make a refactoring pass; a deliberate
/// change of behaviour re-records them with SWITCHBOARD_CAPTURE=1, in a commit that says so.
/// </summary>
public sealed class EffectorGateCharacterizationTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";
    private static readonly ModeStatus ControlOff = ModeState.Resolve(OperatingMode.Active, controlAllowed: false, configurationValid: true, stateConfirmed: true);
    private static readonly ModeStatus Shadow = ModeState.Resolve(OperatingMode.Shadow, controlAllowed: true, configurationValid: true, stateConfirmed: true);
    private static readonly string[] Heaters = ["heater_l1", "heater_l2", "heater_l3"];

    private static string OnChange(string devices) =>
        devices.ReplaceLineEndings("\n").Replace("    value_per_kwh: 0.35", "    value_per_kwh: 0.35\n    state_reports: on_change", StringComparison.Ordinal);

    private static string RestoreOnly(string devices) =>
        devices.ReplaceLineEndings("\n").Replace("    value_per_kwh:", "    start: restore_only\n    value_per_kwh:", StringComparison.Ordinal);

    [Fact]
    public void Protection_then_dispatch_overload_shed_and_restore()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, Heaters, OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 13.6, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 33, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(12), 3, 3, 3);
        Compare(nameof(Protection_then_dispatch_overload_shed_and_restore), Trace(s, port));
    }

    [Fact]
    public void Override_force_off_and_force_on_outrank_dispatch_and_yield_to_protection()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOff);
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + TimeSpan.FromMinutes(12), 3, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(14), 33, 3, 3);
        s.Override("heater_l1", OverrideMode.Auto);
        s.PollUntil(T0 + TimeSpan.FromMinutes(20), 3, 3, 3);
        Compare(nameof(Override_force_off_and_force_on_outrank_dispatch_and_yield_to_protection), Trace(s, port));
    }

    [Fact]
    public void Failed_and_unsent_commands_are_taken_back_resent_and_counted()
    {
        var failOffs = true;
        var port = new RecordingDeviceCommandPort { Answer = c => c.On ? (CommandOutcome.Failed, null) : failOffs ? (CommandOutcome.TimedOut, null) : (CommandOutcome.Acknowledged, null) };
        var s = DispatchScenario.Create(OnChange(DispatchDevices.Heaters), [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 13.6, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 15.6, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 33, 3, 3);
        port.LinkGeneration++;
        failOffs = false;
        s.PollUntil(T0 + TimeSpan.FromMinutes(10), 3, 3, 3);
        port.Answer = _ => (CommandOutcome.NotSent, CommandNotSentReason.LinkDown);
        s.PollUntil(T0 + TimeSpan.FromMinutes(16), 33, 3, 3);
        Compare(nameof(Failed_and_unsent_commands_are_taken_back_resent_and_counted), Trace(s, port));
    }

    [Fact]
    public void An_acknowledged_command_the_device_never_shows_is_unconfirmed_and_marks_it_unresponsive()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(OnChange(DispatchDevices.Heaters), Heaters, OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(20), 3, 3, 3);
        Compare(nameof(An_acknowledged_command_the_device_never_shows_is_unconfirmed_and_marks_it_unresponsive), Trace(s, port));
    }

    [Fact]
    public void Settling_windows_verify_a_setpoint_charger_against_its_draw()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndCharger, ["heater_l1"], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.Charger(6, 6);
        double Now() => s.Ledger("ev_charger")?.Target?.Setpoint()?.Value ?? 6;
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 3, 3, 3, () => s.Charger(Now(), Now()));
        s.PollUntil(T0 + TimeSpan.FromMinutes(10), 14, 3, 3, () => s.Charger(Now(), Now()));
        s.PollUntil(T0 + TimeSpan.FromMinutes(14), 22, 3, 3, () => s.Charger(Now(), Now() / 2));
        s.PollUntil(T0 + TimeSpan.FromMinutes(20), 3, 3, 3, () => s.Charger(Now(), Now()));
        Compare(nameof(Settling_windows_verify_a_setpoint_charger_against_its_draw), Trace(s, port));
    }

    [Fact]
    public void A_stepped_device_moves_stage_by_stage_and_sheds_stage_by_stage()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Boiler, ["boiler"], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 3, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(10), 28, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(16), 3, 3, 3);
        Compare(nameof(A_stepped_device_moves_stage_by_stage_and_sheds_stage_by_stage), Trace(s, port));
    }

    [Fact]
    public void A_foreign_switch_is_followed_in_active_mode_and_ignored_by_the_target_in_shadow_mode()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, Heaters, OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3);
        s.SetState("heater_l1", new RelayApplied(false));
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 3, 3, 3);
        s.ChangeMode(Shadow);
        s.PollUntil(T0 + TimeSpan.FromMinutes(8), 3, 3, 3);
        s.SetState("heater_l2", new RelayApplied(true));
        s.PollUntil(T0 + TimeSpan.FromMinutes(12), 3, 3, 3);
        s.ChangeMode(DispatchScenario.Active);
        s.PollUntil(T0 + TimeSpan.FromMinutes(16), 3, 3, 3);
        Compare(nameof(A_foreign_switch_is_followed_in_active_mode_and_ignored_by_the_target_in_shadow_mode), Trace(s, port));
    }

    [Fact]
    public void Restore_baselines_give_back_only_what_Switchboard_shed_across_control_off_and_a_restart()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.HeaterAndCharger), ["heater_l1"], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.Charger(16, 16);
        double Now() => s.Ledger("ev_charger")?.Target?.Setpoint()?.Value ?? 16;
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3, () => s.Charger(Now(), Now()));
        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 30, 3, 3, () => s.Charger(Now(), Now()));
        s.ChangeMode(ControlOff);
        s.PollUntil(T0 + TimeSpan.FromMinutes(8), 3, 3, 3, () => s.Charger(Now(), Now()));
        var first = s.Gate.Persisted();
        s.ChangeMode(DispatchScenario.Active);
        s.PollUntil(T0 + TimeSpan.FromMinutes(16), 3, 3, 3, () => s.Charger(Now(), Now()));
        var second = s.Gate.Persisted();

        var restartPort = new RecordingDeviceCommandPort();
        var later = T0 + TimeSpan.FromHours(1);
        var r = DispatchScenario.Create(RestoreOnly(DispatchDevices.Heaters), ["heater_l1"], OneBoundary, mode: DispatchScenario.Active, port: restartPort, settledLockouts: false, start: later);
        r.Follow(restartPort);
        r.Gate.Restore(
            ImmutableDictionary<DeviceName, StartBaseline>.Empty
                .Add(DeviceName.Parse("heater_l1"), new StartBaseline(DeviceLevel.Relay(true), DeviceLevel.Relay(false), T0, true))
                .Add(DeviceName.Parse("heater_l2"), new StartBaseline(DeviceLevel.Relay(true), DeviceLevel.Relay(false), T0, true)),
            ImmutableDictionary<DeviceName, DateTimeOffset>.Empty.Add(DeviceName.Parse("heater_l3"), T0));
        r.SetState("heater_l2", new RelayApplied(true));
        r.ChangeMode(DispatchScenario.Active, start: true);
        r.PollUntil(later + TimeSpan.FromMinutes(10), 3, 3, 3);

        var text = new StringBuilder();
        text.AppendLine("-- first run, control off");
        AppendPersisted(text, first);
        text.AppendLine("-- first run, control on again");
        AppendPersisted(text, second);
        text.Append(Trace(s, port));
        text.AppendLine("== restart");
        AppendPersisted(text, r.Gate.Persisted());
        text.Append(Trace(r, restartPort));
        Compare(nameof(Restore_baselines_give_back_only_what_Switchboard_shed_across_control_off_and_a_restart), text.ToString());
    }

    [Fact]
    public void Shutdown_drain_records_a_decision_still_waiting_as_not_answered()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, Heaters, OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3);
        port.Hold = true;
        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 33, 3, 3);
        var pending = s.Gate.PendingDecisions;
        var drained = s.Gate.Drain(s.Now);
        var text = new StringBuilder(Trace(s, port));
        text.AppendLine(CultureInfo.InvariantCulture, $"pending before drain: {pending}, after: {s.Gate.PendingDecisions}");
        foreach (var d in drained)
        {
            text.AppendLine("drained " + Line(d));
        }

        Compare(nameof(Shutdown_drain_records_a_decision_still_waiting_as_not_answered), text.ToString());
    }

    [Fact]
    public void The_guard_alone_overload_the_dispatch_fault_and_the_mode_decision_build_their_decisions()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, Heaters, OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3);

        var fault = s.Gate.DispatchFaultDecision(s.Mode, s.Now);
        s.Gate.BeginStep(null, s.Now);
        var alone = s.Gate.Apply(s.Last, s.Mode, s.Now);
        var mode = s.Gate.ModeDecision(Shadow, null, s.Now);
        var text = new StringBuilder(Trace(s, port));
        text.AppendLine("fault " + Line(fault));
        text.AppendLine(CultureInfo.InvariantCulture, $"guard alone: {alone.Length}");
        foreach (var d in alone)
        {
            text.AppendLine("alone " + Line(d));
        }

        text.AppendLine("mode " + Line(mode));
        Compare(nameof(The_guard_alone_overload_the_dispatch_fault_and_the_mode_decision_build_their_decisions), text.ToString());
    }

    private static string Trace(GuardScenario s, RecordingDeviceCommandPort port)
    {
        var text = new StringBuilder();
        foreach (var d in s.Decisions)
        {
            text.AppendLine(Line(d));
        }

        text.AppendLine("-- commands");
        foreach (var c in port.Commands)
        {
            text.AppendLine(c);
        }

        text.AppendLine("-- ledger");
        foreach (var device in s.Setup.Devices.OrderBy(d => d.Id))
        {
            if (s.Ledger(device.Id.Value) is not { } e)
            {
                text.AppendLine(device.Id.Value + ": none");
                continue;
            }

            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{device.Id}: target={e.Target} origin={e.Origin} reason={e.Reason} deferred={Fmt(e.Deferred)} window={e.Window?.Kind}/{e.Window?.Applied}/{e.Window?.Outcome} timedOut={e.SettlingTimedOut} mismatches={e.Mismatches} unresponsive={e.UnresponsiveSince:O} retry={e.RetryAt:O} commanded={e.TargetCommanded} refuted={e.CommandRefuted} baseline={Fmt(e.Baseline)} selfStarted={e.SelfStarted:O} unconfirmed={e.UnconfirmedSince:O}"));
            foreach (var h in e.History)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  history {h.At:O} {h.Kind} exp={h.Expected:0.###} meas={h.Measured:0.###} {h.Unit} {h.Verdict} from={h.From} to={h.To}"));
            }
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"pending decisions: {s.Gate.PendingDecisions}"));
        return text.ToString();
    }

    private static string Line(Decision d) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{d.At:O} {d.Id} {d.Scope} dev={d.Device} stage={d.Stage} phase={d.Phase} {d.Action} {d.Reason} over={d.Overridden} mode={d.Mode} cmd={d.Commanded} refused={d.RefusedBy} origin={d.Origin} group={d.Group} target={d.Target}");

    private static string Fmt(Deferral? d) => d is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{d.Target}/{d.Reason}/{d.Origin}/{d.RefusedBy}/{d.Since:O}/{d.DueAt:O}");

    private static string Fmt(StartBaseline? b) => b is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{b.Level}/{b.Shed}/{b.Since:O}/{b.Commanded}/{b.OptionBefore}");

    private static void AppendPersisted(StringBuilder text, (ImmutableDictionary<DeviceName, StartBaseline> Baselines, ImmutableDictionary<DeviceName, DateTimeOffset> SelfStarted) p)
    {
        foreach (var (device, b) in p.Baselines.OrderBy(x => x.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"persisted baseline {device}: {Fmt(b)}");
        }

        foreach (var (device, at) in p.SelfStarted.OrderBy(x => x.Key))
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"persisted selfStarted {device}: {at:O}"));
        }
    }

    private static void Compare(string name, string actual)
    {
        var path = RepoPaths.Combine("tests", "Switchboard.Tests", "Application", "Effector", "Golden", name + ".txt");
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("SWITCHBOARD_CAPTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
