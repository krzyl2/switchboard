using System.Collections.Immutable;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Effector;

/// <summary>
/// start: restore_only gives back what Switchboard took away (maintainer survey 2026-10-25), and that
/// promise has to survive the two things that interrupt it: the control switch going off and on, and
/// a restart. A reduction that was really sent is remembered until it was really undone, never
/// forgotten by a "would" restore in shadow; across a restart (state.json) it stands while the device
/// still shows the level it was shed to, and a device the user changed meanwhile is left at the
/// user's level, never raised above it.
/// </summary>
public sealed class RestoreBaselineTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly DateTimeOffset Later = T0 + TimeSpan.FromHours(1);
    private static readonly DeviceName HeaterL1 = DeviceName.Parse("heater_l1");
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";

    /// <summary>Option active, control switched off: the posture in which nothing leaves.</summary>
    private static readonly ModeStatus ControlOff = ModeState.Resolve(OperatingMode.Active, controlAllowed: false, configurationValid: true, stateConfirmed: true);

    [Fact]
    public void A_shed_left_in_place_by_switching_control_off_is_restored_once_control_is_back()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.Heaters), [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 9, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 33, 3, 3);
        Assert.Contains("heater_l1 relay 1 off", port.Commands);

        // Control off: nothing leaves and the heater stays off where the guard put it. The overload
        // ends and dispatch "would" restore it in shadow: that restore never happened, so the
        // heater is still Switchboard's to give back (and stays in state.json).
        s.ChangeMode(ControlOff);
        var sent = port.Submitted.Count;
        s.PollUntil(T0 + TimeSpan.FromMinutes(10), 3, 3, 3);
        Assert.Equal(sent, port.Submitted.Count);
        Assert.Equal(DeviceLevel.Relay(true), s.Ledger("heater_l1")!.Baseline?.Level);
        Assert.True(s.Gate.Persisted().Baselines.ContainsKey(HeaterL1));

        s.ChangeMode(DispatchScenario.Active);
        s.PollUntil(T0 + TimeSpan.FromMinutes(20), 3, 3, 3);

        Assert.Equal("heater_l1 relay 1 on", port.Commands.Last(c => c.StartsWith("heater_l1", StringComparison.Ordinal)));
        Assert.Null(s.Ledger("heater_l1")!.Baseline);
    }

    [Fact]
    public void After_a_restart_a_device_still_at_its_shed_level_is_restored()
    {
        var persisted = ShedInAFirstRun();
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.Heaters), ["heater_l1"], OneBoundary, mode: DispatchScenario.Active, port: port, settledLockouts: false, start: Later);
        s.Follow(port);
        s.Gate.Restore(persisted.Baselines, persisted.SelfStarted);
        s.ChangeMode(DispatchScenario.Active, start: true);

        s.PollUntil(Later + TimeSpan.FromMinutes(10), 3, 3, 3);

        Assert.Equal(["heater_l1 relay 1 on"], port.Commands.Where(c => c.StartsWith("heater_l1", StringComparison.Ordinal)));
        Assert.Contains(s.DecisionsFor("heater_l1"), d => d.Action == Switchboard.Application.Decisions.DecisionAction.SwitchOn && d.Commanded);
        Assert.Null(s.Ledger("heater_l1")!.Baseline);
    }

    [Fact]
    public void After_a_restart_a_setpoint_the_user_changed_meanwhile_is_never_raised_above_the_users_level()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.HeaterAndCharger), ["heater_l1"], OneBoundary, mode: DispatchScenario.Active, port: port, settledLockouts: false, start: Later);
        s.Follow(port);

        // state.json: Switchboard had reduced the charger from 16 A to its 6 A minimum; while the
        // add-on was down the user set 10 A.
        s.Gate.Restore(
            ImmutableDictionary<DeviceName, StartBaseline>.Empty.Add(DeviceName.Parse("ev_charger"), new StartBaseline(DeviceLevel.At(new Amperes(16)), DeviceLevel.At(new Amperes(6)), T0, true)),
            ImmutableDictionary<DeviceName, DateTimeOffset>.Empty);
        s.Charger(10, 10);
        s.ChangeMode(DispatchScenario.Active, start: true);

        s.PollUntil(Later + TimeSpan.FromMinutes(15), 3, 3, 3, () => s.Charger(s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 10, s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 10));

        Assert.All(
            port.Submitted.Where(c => c.Device.Value == "ev_charger" && c.Setpoint is not null),
            c => Assert.True(c.Setpoint!.Value.Value <= 10, $"the charger was raised to {c.Setpoint} A, above the 10 A the user set"));
        Assert.Null(s.Ledger("ev_charger")!.Baseline);
    }

    [Fact]
    public void A_shutdown_off_that_never_landed_is_given_back_after_the_startup_safe_state()
    {
        // At stop the heater was shed (baseline on), but the shutdown's off did not reach it. At
        // start the configuration is read before the Home Assistant link: nothing is known yet, the
        // safe state holds every device off (its offs cannot leave). Once the link's states arrive
        // the heater shows on, and the off is sent then: that is a Switchboard reduction of the
        // user's "on" like any other, and restore_only gives it back, exactly as for the devices
        // that had no baseline in state.json.
        var persisted = ShedInAFirstRun();
        var port = new RecordingDeviceCommandPort { Answer = _ => (CommandOutcome.NotSent, CommandNotSentReason.LinkDown) };
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.Heaters), [], OneBoundary, mode: DispatchScenario.Active, port: port, settledLockouts: false, start: Later);
        s.Gate.Restore(persisted.Baselines, persisted.SelfStarted);
        foreach (var device in new[] { "heater_l1", "heater_l2", "heater_l3" })
        {
            s.SetState(DeviceName.Parse(device), 1, null);
        }

        foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
        {
            s.Invalidate(phase, ProtectionInvalidReason.CurrentAwaiting, evaluate: false);
        }

        s.ChangeMode(DispatchScenario.Active, start: true);
        s.Evaluate();
        Assert.All(new[] { "heater_l1", "heater_l2", "heater_l3" }, d => Assert.Equal(DeviceLevel.Relay(false), s.Ledger(d)!.Target));

        // The link is up: every heater shows on, heater_l1 too (the shutdown's off never landed).
        port.Answer = _ => (CommandOutcome.Acknowledged, null);
        port.LinkGeneration++;
        foreach (var device in new[] { "heater_l1", "heater_l2", "heater_l3" })
        {
            s.SetState(DeviceName.Parse(device), 1, new RelayApplied(true));
        }

        s.Follow(port);
        s.PollUntil(Later + TimeSpan.FromMinutes(15), 3, 3, 3);

        foreach (var device in new[] { "heater_l1", "heater_l2", "heater_l3" })
        {
            var commands = port.Commands.Where(c => c.StartsWith(device, StringComparison.Ordinal)).ToList();
            Assert.Contains($"{device} relay 1 off", commands);
            Assert.Equal($"{device} relay 1 on", commands[^1]);
        }
    }

    /// <summary>An active run in which the guard sheds heater_l1 (the user had it on): what state.json keeps at the stop.</summary>
    private static (ImmutableDictionary<DeviceName, StartBaseline> Baselines, ImmutableDictionary<DeviceName, DateTimeOffset> SelfStarted) ShedInAFirstRun()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.Heaters), [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 9, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 33, 3, 3);
        Assert.Contains("heater_l1 relay 1 off", port.Commands);
        var persisted = s.Gate.Persisted();
        Assert.Equal((DeviceLevel.Relay(true), DeviceLevel.Relay(false), true), (persisted.Baselines[HeaterL1].Level, persisted.Baselines[HeaterL1].Shed, persisted.Baselines[HeaterL1].Commanded));
        return persisted;
    }

    private static string RestoreOnly(string devices) =>
        devices.ReplaceLineEndings("\n").Replace("    value_per_kwh:", "    start: restore_only\n    value_per_kwh:", StringComparison.Ordinal);
}
