using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Effector;

/// <summary>
/// BRK-03 and SAFE-05 for a charger that can only be stopped through a select (0.7.1, maintainer
/// 2026-10-25: the go-e force state, "1" don't charge, "0" neutral). Below its minimum the stop is a
/// select_option with the declared stop option, the declared resume option gives it back, nothing
/// else is ever sent to that select, and the stop goes through the same gate, confirmation, restore
/// baseline and mode rules as a switch stop. The user's own option is never fought: a device the
/// user stopped stays stopped, and an option nobody declared is never sent, only named in the log.
/// </summary>
public sealed class SelectStopTests
{
    [Fact]
    public void Guard_stop_sends_stop_option_recorded_commanded_once_acknowledged()
    {
        // 40 A on L1 (1.6 × In): everything on the phase at once, the charger stopped below its
        // minimum through its select, in the allow-list's exact shape.
        var rig = new SelectStopRig().Begin();

        rig.Poll(30);

        var stop = Assert.Single(rig.Path.Calls, c => c.Call.Service == ActuatorService.SelectOption);
        Assert.Contains("""
            "domain":"select","service":"select_option","service_data":{"option":"1"},"target":{"entity_id":"select.charger_force"}}
            """, stop.Json, StringComparison.Ordinal);
        var shed = Assert.Single(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Origin == DecisionOrigin.Guard);
        Assert.Equal((DecisionAction.SwitchOff, true, (ReasonCode?)null), (shed.Action, shed.Commanded, shed.RefusedBy));
        Assert.False(shed.Target!.Enabled);
    }

    [Fact]
    public void Stop_is_confirmed_by_the_reported_stop_option()
    {
        var rig = new SelectStopRig().Begin();
        rig.Poll(30);

        rig.Hold(30, TimeSpan.FromSeconds(80));

        Assert.Equal("1", rig.Option);
        Assert.Null(rig.Ledger.UnconfirmedSince);
        Assert.DoesNotContain("still reports", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Unconfirmed_select_stop_is_flagged_after_confirm_timeout()
    {
        // The select never takes the option (a go-e that lost its link, an integration that ignores
        // it): after ack + reaction time (20 s) + effector.confirm_timeout (30 s) the stop is
        // unconfirmed, a warning, exactly as for a switch stop.
        var rig = new SelectStopRig().Begin();
        rig.Frozen = true;
        rig.Poll(30);
        var sent = rig.Harness.Now;

        rig.Hold(30, TimeSpan.FromSeconds(40));
        Assert.Null(rig.Ledger.UnconfirmedSince);
        rig.Hold(30, TimeSpan.FromSeconds(20));

        Assert.True(rig.Ledger.UnconfirmedSince is { } since && since >= sent + TimeSpan.FromSeconds(50), $"unconfirmed since {rig.Ledger.UnconfirmedSince:O}");
        Assert.Contains("still reports", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopped_charger_counts_as_off_for_guard_and_dispatch()
    {
        // A select-stopped charger is a disabled level everywhere: dispatch reads it disabled, and the
        // guard, still over its limit, finds nothing more to take from it (no further step, no command).
        var rig = new SelectStopRig().Begin();
        rig.Poll(30);
        rig.Poll(30);
        var calls = rig.Path.Calls.Count;

        rig.Hold(30, TimeSpan.FromSeconds(60));

        var status = rig.Harness.Loop.DispatchStatus.Current.Devices.Single(d => d.Id.Value == "ev_charger");
        Assert.False(status.Reported!.Enabled);
        Assert.Equal(calls, rig.Path.Calls.Count);
        Assert.Single(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff);
    }

    [Fact]
    public void Restore_resumes_with_resume_option()
    {
        // restore_only: the level before the stop (10 A, running) is given back once the overload is
        // gone, through the declared resume option; the stop came from that option, so no warning.
        var rig = new SelectStopRig().Begin();
        rig.Poll(30);
        rig.Hold(30, TimeSpan.FromSeconds(30));

        rig.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Equal(["1", "0"], rig.SelectOptions);
        Assert.Contains(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Action == DecisionAction.SwitchOn && d.Commanded);
        Assert.Equal(("0", 10.0), (rig.Option, rig.Setpoint));
        Assert.Null(rig.Ledger.Baseline);
        Assert.DoesNotContain("undeclared", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_after_restart_uses_the_persisted_baseline()
    {
        // The stop was sent, then the add-on restarted: state.json keeps the baseline with the option
        // before the stop, the charger still reports the stop option, and it is resumed.
        var first = new SelectStopRig().Begin();
        first.Poll(30);
        first.Hold(30, TimeSpan.FromSeconds(20));
        var persisted = first.Harness.Gate.Persisted();
        Assert.Equal(OptionText.Parse("0"), persisted.Baselines[DeviceName.Parse("ev_charger")].OptionBefore);

        using var data = new TempDataDirectory();
        var file = new RuntimeStateFile(data.Path, TestDecisions.Options(), NullLogger<RuntimeStateFile>.Instance);
        file.Save(RuntimeState.Fresh with { StartBaselines = persisted.Baselines, SelfStarted = persisted.SelfStarted });
        var loaded = file.Load().State;

        var second = new SelectStopRig(option: "1", setpoint: 6, at: first.Harness.Now + TimeSpan.FromMinutes(5));
        second.Harness.Gate.Restore(loaded.StartBaselines, loaded.SelfStarted);
        second.Begin();
        second.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Equal(["0"], second.SelectOptions);
        Assert.Contains(second.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Action == DecisionAction.SwitchOn && d.Commanded);
    }

    [Fact]
    public void The_option_before_a_stop_survives_a_restart_whose_first_step_holds_the_charger_off()
    {
        // state.json holds the stop from the undeclared "2". After the restart the first step comes
        // before the charger's states: protection holds the unknown charger off (its stop leaves
        // again), and the option it reports is not known yet. That must not erase the "2" captured
        // before the restart, or the resume could no longer name the option it does not give back.
        var rig = new SelectStopRig(option: "1", setpoint: 6);
        var charger = DeviceName.Parse("ev_charger");
        var baseline = new StartBaseline(DeviceLevel.At(new Amperes(16)), DeviceLevel.At(new Amperes(6), enabled: false), rig.Harness.Now - TimeSpan.FromMinutes(5), true)
        {
            OptionBefore = OptionText.Parse("2"),
        };
        rig.Harness.Gate.Restore(ImmutableDictionary<DeviceName, StartBaseline>.Empty.Add(charger, baseline), ImmutableDictionary<DeviceName, DateTimeOffset>.Empty);

        rig.BeginWithoutCharger();
        Assert.Equal(OptionText.Parse("2"), rig.Harness.Gate.Persisted().Baselines[charger].OptionBefore);
        rig.Harness.Time.Advance(TimeSpan.FromSeconds(10));
        rig.ReportCharger(rig.Harness.Now - TimeSpan.FromMinutes(5));
        rig.Harness.Publish();
        rig.Harness.Runner.Step();
        Assert.Equal(OptionText.Parse("2"), rig.Harness.Gate.Persisted().Baselines[charger].OptionBefore);

        rig.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Equal(["1", "0"], rig.SelectOptions);
        Assert.Contains("Resuming ev_charger with its resume option '0': before Switchboard stopped it, it reported '2'", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_setpoint_changed_while_stopped_is_the_users_new_level_and_the_charger_is_still_resumed(bool switchStop)
    {
        // Switchboard holds the charger stopped (16 A before the stop). The user moves its current to
        // 10 A in the charger's app while it is stopped: a new level, not a choice to stay stopped (the
        // stop itself did not move). The restore baseline takes the new setpoint and stays, so the
        // charger is resumed at 10 A once the overload is gone, for a select stop and a switch stop
        // alike; 0.7.0 dropped the baseline and left the charger stopped by Switchboard for good.
        var rig = new SelectStopRig(setpoint: 16, switchStop: switchStop) { CarWants = 16 }.Begin();
        rig.Poll(30);
        rig.Hold(30, TimeSpan.FromSeconds(30));
        Assert.Equal(rig.Stopped, rig.Option);
        Assert.NotNull(rig.Ledger.Baseline);

        rig.Setpoint = 10;
        rig.Hold(30, TimeSpan.FromSeconds(30));
        Assert.Equal(DeviceLevel.At(new Amperes(10)), rig.Ledger.Baseline?.Level);

        rig.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Equal(["stop", "run"], rig.StopCommands);
        Assert.Equal(10.0, rig.Setpoint);
        Assert.NotEqual(rig.Stopped, rig.Option);
        Assert.Null(rig.Ledger.Baseline);
    }

    [Fact]
    public void A_resume_option_changed_while_stopped_keeps_the_baseline_and_resumes_with_the_new_option()
    {
        // Switchboard holds the charger stopped through "1"; the user then declares "2" as the resume
        // option (the stop entity and the stop option stay, so the reported state means the same).
        // The level to give back is still owed: the charger is resumed with the option declared now.
        // Its "0" from before the stop is no longer declared, so the log names it.
        var rig = new SelectStopRig().Begin();
        rig.Poll(30);
        rig.Hold(30, TimeSpan.FromSeconds(30));
        Assert.Equal("1", rig.Option);

        Reload(rig, TestDevices.Document(SelectStopRig.Charger.Replace("resume_option: \"0\"", "resume_option: \"2\"", StringComparison.Ordinal), defaultStart: "restore_only"));
        rig.Hold(30, TimeSpan.FromSeconds(30));
        Assert.NotNull(rig.Ledger.Baseline);

        rig.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Equal(["1", "2"], rig.SelectOptions);
        Assert.Equal("2", rig.Option);
        Assert.Empty(rig.Path.Refused);
        Assert.Contains("before Switchboard stopped it, it reported '0'", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopped_from_undeclared_option_resumes_with_resume_option_and_warns()
    {
        // The setpoint is first lowered while the select says "0" (the baseline is taken then); the
        // user then sets "2" (force charging); later the charger is stopped (its 6 A no longer fit
        // L1's reserve: dispatch stops it, since 0.7.1 it can). Protection outranks the user's
        // option, the log says it is stopped from '2', and the restore sends only the declared "0",
        // naming the option it could not give back. "2" itself is never sent.
        var rig = new SelectStopRig(setpoint: 16) { CarWants = 16 }.Begin();
        rig.Poll(17);
        Assert.Contains(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Action == DecisionAction.ReduceToMinimum && d.Commanded);
        rig.Poll(17);
        rig.Option = "2";
        rig.Poll(17);
        rig.Hold(17, TimeSpan.FromSeconds(60));
        Assert.Equal(["1"], rig.SelectOptions);
        Assert.Contains("Stopping ev_charger from the undeclared option '2'; it will be resumed with its resume option '0'", rig.Log.AllText, StringComparison.Ordinal);

        rig.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Equal(["1", "0"], rig.SelectOptions);
        Assert.Contains("Resuming ev_charger with its resume option '0': before Switchboard stopped it, it reported '2'", rig.Log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void User_stopped_device_is_never_resumed_by_restore_only()
    {
        // The user stopped the charger (it reports "1" before Switchboard does anything): an overload
        // and its end give nothing back that Switchboard did not take; "0" is never sent.
        var rig = new SelectStopRig(option: "1").Begin();
        rig.Poll(30);
        rig.Hold(30, TimeSpan.FromSeconds(30));

        rig.Hold(2, TimeSpan.FromMinutes(15));

        Assert.DoesNotContain("0", rig.SelectOptions);
        Assert.Equal("1", rig.Option);
        Assert.Null(rig.Ledger.Baseline);
    }

    [Theory]
    [InlineData(OperatingMode.Shadow, true)]
    [InlineData(OperatingMode.Active, false)]
    public void Shadow_or_control_off_sends_no_select_option(OperatingMode ceiling, bool allowed)
    {
        // MODE-02: the stop is decided and recorded, with the reason nothing was sent; the select is
        // never called.
        var rig = new SelectStopRig(ceiling, allowed).Begin();

        rig.Poll(30);
        rig.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Empty(rig.Path.Calls);
        var shed = Assert.Single(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff);
        Assert.Equal((false, ReasonCode.ModeShadow), (shed.Commanded, shed.RefusedBy));
    }

    [Fact]
    public void A_force_off_override_stops_the_charger_through_its_select()
    {
        // Since 0.7.1 a stop-capable charger is taken all the way off by force_off (CHANGELOG), once
        // its min_on (60 s from the start) has run, like any ordinary change.
        var rig = new SelectStopRig().Begin();

        rig.Harness.Loop.Overrides.Set(DeviceName.Parse("ev_charger"), OverrideMode.ForceOff, OverrideSource.Api);
        rig.Hold(2, TimeSpan.FromSeconds(90));

        Assert.Equal(["1"], rig.SelectOptions);
        Assert.Contains(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Origin == DecisionOrigin.Override && d.Action == DecisionAction.SwitchOff && d.Commanded);
    }

    [Fact]
    public void Removed_select_device_gets_final_stop_only_when_switchboard_started_it()
    {
        // start: headroom and the user had it stopped: dispatch starts it ("0"), so Switchboard owes
        // the final stop when the charger leaves the configuration: one "1", through its previous
        // declaration, and nothing more.
        var started = new SelectStopRig(start: "headroom", option: "1").Begin();
        started.Hold(2, TimeSpan.FromMinutes(5));
        Assert.Equal(["0"], started.SelectOptions);
        Assert.NotNull(started.Ledger.SelfStarted);

        Remove(started);
        started.Hold(2, TimeSpan.FromMinutes(2));

        Assert.Equal(["0", "1"], started.SelectOptions);
        Assert.Empty(started.Path.Refused);

        // The user's own running charger (never started by Switchboard) is left exactly as it is
        // (the heater of the new file is held off until its state arrives: that is its own matter).
        var users = new SelectStopRig(start: "headroom").Begin();
        users.Hold(2, TimeSpan.FromMinutes(5));
        var before = users.Path.Calls.Count;

        Remove(users);
        users.Hold(2, TimeSpan.FromMinutes(2));

        Assert.DoesNotContain(users.Path.Calls.Skip(before), c => c.Command.Device.Value == "ev_charger");
        Assert.Empty(users.SelectOptions);
    }

    /// <summary>A valid reload without the charger (a heater stays, so the document keeps a device).</summary>
    private static void Remove(SelectStopRig rig) => Reload(rig, TestDevices.Document(Heater, defaultStart: "headroom"));

    /// <summary>A valid reload of <paramref name="yaml"/>: the store and the command path's actuators follow it.</summary>
    private static void Reload(SelectStopRig rig, string yaml)
    {
        var result = TestConfigurations.Valid(yaml);
        rig.Harness.Store.ApplyConfiguration(result, rig.Harness.Now);
        rig.Path.Directory.Observe(result.Configuration);
    }

    private const string Heater = """
          - id: heater_l2
            type: onoff
            actuator: switch.heater_2
            phase: L2
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;
}
