using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// VER-02 and DISP-05 … DISP-07 on the fake clock. Active semantics are driven through the
/// recording command port (the only implementation there is, in the tests): a command opens a
/// settling window that closes once every balance input the change can move has reported after
/// command time + reaction time (never the device's own state), or at the hard timeout; the change
/// counts in flight only while the window is open; at its close the measured change is verified.
/// Shadow windows open at virtual command instants, close by the same rule and verify nothing.
/// </summary>
public sealed class SettlingScenarioTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);

    // Fast scenarios: one confirming boundary, no relay lockouts, a short unresponsive retry.
    private const string Fast = "  dispatch:\n    persistence_up_cycles: 1\n    relay_min_on: 0s\n    relay_min_off: 0s\n  settling:\n    unresponsive_retry_after: 2m\n";

    [Fact]
    public void Delayed_sample_closes_the_window_when_it_arrives()
    {
        // The heater is commanded on at 12:00:30; the meter's next report comes only at 12:01:00.
        // Until then the window stays open (and the 6.6 A count in flight); the delayed report closes
        // it, and the measured 1.5 kW step matches the rated power.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle, 3, 3, 3);
        Assert.Equal(["heater_l1 relay 1 on"], port.Commands);
        s.SetState("heater_l1", new RelayApplied(true));

        s.RunUntil(T0 + (2 * Cycle) - TimeSpan.FromTicks(1));
        Assert.NotNull(s.Ledger("heater_l1")!.Window);
        Assert.Equal(6.6, s.Envelope(Phase.L1).InFlight.Value, 9);

        s.RunUntil(T0 + (2 * Cycle));
        s.SampleAll(3 + (1500 / GuardScenario.Volts), 3, 3);

        var ledger = s.Ledger("heater_l1")!;
        Assert.Null(ledger.Window);
        var record = Assert.Single(ledger.History);
        Assert.Equal((HistoryKind.Command, Verdict.Match, 1500.0), (record.Kind, record.Verdict, Math.Round(record.Measured)));
        Assert.Equal(0, s.Envelope(Phase.L1).InFlight.Value);
    }

    [Fact]
    public void Lost_sample_times_out_and_counts_no_mismatch()
    {
        // The meter never reports after the command: at command + 90 s the window closes anyway,
        // one SettlingTimedOut is recorded (settling_window_open), no mismatch is counted, and the
        // in-flight change ends (measurement rules).
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle, 3, 3, 3);

        s.RunUntil(T0 + Cycle + TimeSpan.FromSeconds(90));

        var timedOut = Assert.Single(s.Decisions, d => d.Action == DecisionAction.SettlingTimedOut);
        Assert.Equal((T0 + Cycle + TimeSpan.FromSeconds(90), ReasonCode.SettlingWindowOpen, "heater_l1"), (timedOut.At, timedOut.Reason, timedOut.Device!.Value.Value));
        Assert.Equal(0, s.Ledger("heater_l1")!.Mismatches);
        Assert.Equal(Verdict.TimedOut, s.Ledger("heater_l1")!.History.Single().Verdict);
        s.RunFor(TimeSpan.FromMinutes(3));
        Assert.Single(s.Decisions, d => d.Action == DecisionAction.SettlingTimedOut);
    }

    [Fact]
    public void Shadow_windows_close_by_time_and_never_verify()
    {
        // Shadow mode: nothing is applied, so the window of a virtual command closes by the same
        // rule (the next meter report) and its verdict is "not applied"; no mismatch can be counted.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], Fast);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);

        var ledger = s.Ledger("heater_l1")!;
        Assert.Null(ledger.Window);
        Assert.Equal(Verdict.NotApplied, ledger.History.Single().Verdict);
        Assert.Equal(0, ledger.Mismatches);
        Assert.True(ledger.Target!.Relays[0]);
    }

    [Fact]
    public void Active_in_flight_delta_ends_at_window_close_and_measurement_rules()
    {
        // In active mode the commanded 6.6 A count only while the window is open. The relay did not
        // switch (state still off, no load): once the meter reports, the estimate is the measured
        // current again and the missing step is a mismatch.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle, 3, 3, 3);
        Assert.Equal(3 + 6.6, s.Envelope(Phase.L1).EstimatedCurrent!.Value.Value, 9);

        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);

        Assert.Equal(3, s.Envelope(Phase.L1).EstimatedCurrent!.Value.Value, 9);
        Assert.Equal((Verdict.Mismatch, 1), (s.Ledger("heater_l1")!.History.Single().Verdict, s.Ledger("heater_l1")!.Mismatches));
    }

    [Fact]
    public void Estimate_keeps_the_relay_delta_until_the_meter_reports_after_required()
    {
        // The relay reports "on" at once (on change) but the meter has not seen the load yet: the
        // estimate keeps the commanded 6.6 A (against the contribution captured at the command)
        // until the meter reports after command + reaction time.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle, 3, 3, 3);
        s.RunFor(TimeSpan.FromSeconds(2));
        s.SetState("heater_l1", new RelayApplied(true));
        s.Evaluate();

        Assert.Equal(3 + 6.6, s.Envelope(Phase.L1).EstimatedCurrent!.Value.Value, 9);
    }

    [Fact]
    public void Unresponsive_on_change_relay_is_marked_after_mismatch_limit_excluded_and_retried()
    {
        // A relay that reports only on change ignores every command: it never reports, and the
        // meter shows no step. Its windows close on the meter's reports (its own state is not in the
        // closing set), each a mismatch; the third marks it unresponsive (device_unresponsive), its
        // target goes off and it is excluded from increases until the probe after
        // unresponsive_retry_after, which matches once the relay works again.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"], devices: DispatchDevices.Heaters.ReplaceLineEndings("\n").Replace("value_per_kwh: 0.35", "value_per_kwh: 0.35\n    state_reports: on_change", StringComparison.Ordinal));
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);
        Assert.Equal(1, s.Ledger("heater_l1")!.Mismatches);

        s.Override("heater_l1", OverrideMode.ForceOff);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(30), 3, 3, 3);
        Assert.Equal(2, s.Ledger("heater_l1")!.Mismatches);

        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + (3 * Cycle) + TimeSpan.FromSeconds(10), 3, 3, 3);
        var marked = Assert.Single(s.Decisions, d => d.Action == DecisionAction.MarkedUnresponsive);
        Assert.Equal(ReasonCode.DeviceUnresponsive, marked.Reason);

        s.Override("heater_l1", OverrideMode.Auto);
        s.PollUntil(T0 + (5 * Cycle), 3, 3, 3);
        Assert.False(s.Ledger("heater_l1")!.Target!.Relays[0]);
        Assert.Contains(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.DeviceUnresponsive);
        var commands = port.Commands.Count;
        var retry = marked.At + TimeSpan.FromMinutes(2);
        s.PollUntil(retry - TimeSpan.FromSeconds(1), 3, 3, 3);
        Assert.Equal(commands, port.Commands.Count);

        // The probe: commanded on at the first boundary after the retry delay; the relay works now.
        var probe = new DateTimeOffset((retry.UtcTicks + Cycle.Ticks - 1) / Cycle.Ticks * Cycle.Ticks, TimeSpan.Zero);
        s.PollUntil(probe, 3, 3, 3);
        Assert.Equal("heater_l1 relay 1 on", port.Commands[^1]);
        s.SetState("heater_l1", new RelayApplied(true));
        s.PollUntil(s.Now + TimeSpan.FromSeconds(10), 3 + (1500 / GuardScenario.Volts), 3, 3);
        Assert.Single(s.Decisions, d => d.Action == DecisionAction.UnresponsiveCleared);
        Assert.False(s.Ledger("heater_l1")!.Unresponsive);
    }

    [Fact]
    public void Unresponsive_flag_clears_when_the_device_declaration_changes()
    {
        // Fail-safe row "effector unresponsive": the flag is memory only (never persisted, so a
        // restart clears it) and a changed declaration of the device (a new relay, a new rating)
        // starts it again from scratch.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOff);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(30), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + (3 * Cycle) + TimeSpan.FromSeconds(10), 3, 3, 3);
        Assert.True(s.Ledger("heater_l1")!.Unresponsive);

        var heaters = DispatchDevices.Heaters;
        var first = heaters.IndexOf("rated_current_a: 6.6", StringComparison.Ordinal);
        s.Reconfigure(TestDevices.Document(heaters[..first] + "rated_current_a: 7.0" + heaters[(first + "rated_current_a: 6.6".Length)..], Fast));
        s.PollUntil(s.Now + TimeSpan.FromSeconds(10), 3, 3, 3);

        Assert.False(s.Ledger("heater_l1")!.Unresponsive);
        Assert.Equal(0, s.Ledger("heater_l1")!.Mismatches);
        Assert.Empty(new DeviceTargets().Devices);
    }

    [Fact]
    public void Match_resets_the_mismatch_count()
    {
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);
        Assert.Equal(1, s.Ledger("heater_l1")!.Mismatches);

        s.Override("heater_l1", OverrideMode.ForceOff);
        s.SetState("heater_l1", new RelayApplied(false));
        s.PollUntil(s.Now + TimeSpan.FromSeconds(20), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOn);
        s.PollUntil(T0 + (3 * Cycle), 3, 3, 3);
        s.SetState("heater_l1", new RelayApplied(true));
        s.PollUntil(T0 + (3 * Cycle) + TimeSpan.FromSeconds(10), 3 + (1500 / GuardScenario.Volts), 3, 3);

        Assert.Equal(Verdict.Match, s.Ledger("heater_l1")!.History[^1].Verdict);
        Assert.Equal(0, s.Ledger("heater_l1")!.Mismatches);
    }

    [Fact]
    public void Foreign_load_arriving_mid_window_is_recorded_and_not_blamed_on_the_device()
    {
        // A kettle (2 kW) starts while the heater's window is open: the excess over the heater's
        // 1.5 kW is in the commanded direction and a foreign step was detected, so the verdict is
        // inconclusive, not a mismatch; the foreign step has its own record.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle, 3, 3, 3);
        s.SetState("heater_l1", new RelayApplied(true));

        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3 + (3500 / GuardScenario.Volts), 3, 3);

        Assert.Single(s.Decisions, d => d.Action == DecisionAction.ForeignLoadDetected && d.Phase == Phase.L1);
        Assert.Equal((Verdict.Inconclusive, 0), (s.Ledger("heater_l1")!.History.Single().Verdict, s.Ledger("heater_l1")!.Mismatches));
    }

    [Fact]
    public void Foreign_load_arriving_mid_switch_off_window_is_not_blamed_on_the_device()
    {
        // VER-02, switch-off direction: the heater (no draw signal, verified against L1's house
        // load) is commanded off, expected −1.5 kW; a 2 kW kettle starts inside the window, so L1
        // rises by 0.5 kW. The relay did switch off: the shortfall is the kettle's, the verdict is
        // inconclusive and no mismatch counts towards marking a working relay unresponsive.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: []);
        var l1 = 3 + (1500 / GuardScenario.Volts);
        s.PollUntil(T0 + TimeSpan.FromSeconds(40), l1, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOff);
        s.Evaluate(GuardTrigger.Override);
        Assert.Equal("heater_l1 relay 1 off", port.Commands[^1]);
        s.SetState("heater_l1", new RelayApplied(false));

        s.PollUntil(s.Now + TimeSpan.FromSeconds(30), l1 + (500 / GuardScenario.Volts), 3, 3);

        Assert.Single(s.Decisions, d => d.Action == DecisionAction.ForeignLoadDetected && d.Phase == Phase.L1);
        var record = s.Ledger("heater_l1")!.History.Last(h => h.Kind == HistoryKind.Command);
        Assert.Equal((Verdict.Inconclusive, -1500.0), (record.Verdict, record.Expected));
        Assert.Equal(0, s.Ledger("heater_l1")!.Mismatches);
    }

    [Fact]
    public void External_heater_switching_records_measured_step_without_counting()
    {
        // Another controller switches the heater on: the target lockouts restart (the contacts
        // moved), an observation window records the measured step for the history (evidence for the
        // rated power and the phase), and nothing is counted or recorded as a decision.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 100\n");
        s.PollUntil(T0 + TimeSpan.FromSeconds(20), 3, 3, 3);
        s.SetState("heater_l1", new RelayApplied(true));
        s.Evaluate();
        s.PollUntil(T0 + TimeSpan.FromSeconds(40), 3 + (1460 / GuardScenario.Volts), 3, 3);

        var record = Assert.Single(s.Ledger("heater_l1")!.History);
        Assert.Equal((HistoryKind.Observed, Verdict.Match, 1460.0), (record.Kind, record.Verdict, Math.Round(record.Measured)));
        Assert.Equal(0, s.Ledger("heater_l1")!.Mismatches);
        Assert.Empty(s.DecisionsFor("heater_l1"));
        Assert.True(s.Lockouts.MinOnRemaining(DeviceName.Parse("heater_l1"), 1, s.Now, LockoutTimeline.Target) > TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Night_window_closes_with_the_inverter_asleep()
    {
        // Generation is not in the closing set: with the inverter silent all night the window still
        // closes on the meter's power and current reports.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], Fast);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);

        Assert.Null(s.Ledger("heater_l1")!.Window);
        Assert.DoesNotContain(s.Decisions, d => d.Action == DecisionAction.SettlingTimedOut);
    }

    [Fact]
    public void Own_commanded_change_is_never_foreign()
    {
        // The meter shows the heater's 1.5 kW before its relay reports on: in active mode a commanded
        // change counts as ours until the reported state shows it, so it is no foreign load.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle, 3, 3, 3);

        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(20), 3 + (1500 / GuardScenario.Volts), 3, 3);

        Assert.DoesNotContain(s.Decisions, d => d.Action == DecisionAction.ForeignLoadDetected);
    }

    [Fact]
    public void A_relay_that_did_not_follow_its_command_hides_no_kettle()
    {
        // Active semantics: the heater is commanded on but never switches (no step on the meter,
        // its state stays off), so its window closes with a mismatch. From then on it is not counted
        // as ours: a 2 kW kettle a minute later is a foreign load event (behind a phantom 1.5 kW it
        // would read as +0.5 kW and go unseen), and the phantom ending is no event either.
        var port = new RecordingDeviceCommandPort();
        var s = Active(port, off: ["heater_l1"]);
        s.PollUntil(T0 + Cycle + TimeSpan.FromSeconds(10), 3, 3, 3);
        Assert.Equal((Verdict.Mismatch, 1), (s.Ledger("heater_l1")!.History.Single().Verdict, s.Ledger("heater_l1")!.Mismatches));

        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3);
        Assert.DoesNotContain(s.Decisions, d => d.Action == DecisionAction.ForeignLoadDetected);
        s.PollUntil(T0 + TimeSpan.FromMinutes(3), 3 + (2000 / GuardScenario.Volts), 3, 3);

        // One event, at the kettle's first sample: not later, when the heater's next command (the
        // stop the kettle causes) would end the phantom.
        var kettle = Assert.Single(s.Decisions, d => d.Action == DecisionAction.ForeignLoadDetected);
        Assert.Equal((Phase.L1, T0 + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10)), (kettle.Phase!.Value, kettle.At));
    }

    [Fact]
    public void Foreign_step_records_one_event_per_step()
    {
        // 04 §4: a kettle is one event however long it boils; once the residual is back below
        // rearm_fraction of the threshold the next kettle is the next event. Each carries the v3
        // code foreign_load: the log tells "the house did this" from "the reserve is too small".
        var s = DispatchScenario.Create(DispatchDevices.Heaters, [], "  dispatch:\n    persistence_up_cycles: 100\n");
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 9.6, 9.6, 9.6);
        s.PollUntil(T0 + TimeSpan.FromMinutes(3), 9.6 + (2000 / GuardScenario.Volts), 9.6, 9.6);
        Assert.Single(s.Decisions, d => d.Action == DecisionAction.ForeignLoadDetected);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 9.6, 9.6, 9.6);
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 9.6 + (2000 / GuardScenario.Volts), 9.6, 9.6);

        var events = s.Decisions.Where(d => d.Action == DecisionAction.ForeignLoadDetected).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal((DecisionScope.Phase, Phase.L1, ReasonCode.ForeignLoad), (e.Scope, e.Phase!.Value, e.Reason)));
    }

    private static GuardScenario Active(RecordingDeviceCommandPort port, string[] off, string? devices = null) =>
        DispatchScenario.Create(devices ?? DispatchDevices.Heaters, off, Fast, mode: DispatchScenario.Active, port: port);
}
