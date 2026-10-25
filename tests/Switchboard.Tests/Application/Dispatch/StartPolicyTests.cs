using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The start: key (maintainer survey 2026-10-25). restore_only, the default, gives back only what
/// Switchboard took away: a device the user left off is never started, and a device that was shed
/// comes back to the level it had, never higher. headroom lets dispatch fill free capacity as in
/// Stage 3. Protection is the same for both.
/// </summary>
public sealed class StartPolicyTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";

    [Fact]
    public void A_restore_only_device_the_user_left_off_is_never_started()
    {
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.Heaters), ["heater_l1"], OneBoundary);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);

        Assert.DoesNotContain(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);
        Assert.False(s.Ledger("heater_l1")?.Target?.Relays[0] ?? false);
    }

    [Fact]
    public void A_headroom_device_is_started_with_free_capacity()
    {
        // The same house with start: headroom: dispatch fills the headroom (the Stage 3 behaviour).
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], OneBoundary);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);

        Assert.Contains(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);
    }

    [Fact]
    public void A_shed_restore_only_device_comes_back_to_its_level_once_the_overload_is_gone()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.Heaters), [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 9, 3, 3);

        // A kettle on L1 above the breaker: the guard sheds the heater, the baseline is "on".
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 33, 3, 3);
        Assert.Contains("heater_l1 relay 1 off", port.Commands);
        Assert.Equal(DeviceLevel.Relay(true), s.Gate.Ledger.Entry(DeviceName.Parse("heater_l1"))!.Baseline?.Level);

        // The kettle is done: the heater comes back to on, and nothing holds it any more.
        s.PollUntil(T0 + TimeSpan.FromMinutes(10), 3, 3, 3);
        Assert.Equal("heater_l1 relay 1 on", port.Commands.Last(c => c.StartsWith("heater_l1", StringComparison.Ordinal)));
        Assert.Contains(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn && d.Commanded);
        Assert.Null(s.Gate.Ledger.Entry(DeviceName.Parse("heater_l1"))!.Baseline);
    }

    [Fact]
    public void A_restore_only_charger_is_raised_back_to_its_setpoint_and_never_above_it()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(RestoreOnly(DispatchDevices.HeaterAndCharger), ["heater_l1"], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.Charger(12, 12);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 15, 3, 3, () => s.Charger(s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 12, s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 12));

        // Overload: the charger is reduced.
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 33, 3, 3, () => s.Charger(s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 12, s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 12));
        var lowest = port.Submitted.Where(c => c.Device.Value == "ev_charger" && c.Setpoint is not null).Min(c => c.Setpoint!.Value.Value);
        Assert.True(lowest < 12, $"the charger was not reduced (lowest {lowest} A)");

        // Plenty of headroom afterwards: back up to 12 A, the level it had, never to its 20 A maximum.
        s.PollUntil(T0 + TimeSpan.FromMinutes(15), 3, 3, 3, () => s.Charger(s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 12, s.Ledger("ev_charger")?.Target?.Setpoint?.Value ?? 12));
        var setpoints = port.Submitted.Where(c => c.Device.Value == "ev_charger" && c.Setpoint is not null).Select(c => c.Setpoint!.Value.Value).ToList();
        Assert.Equal(12, setpoints[^1]);
        Assert.All(setpoints, v => Assert.True(v <= 12, $"the charger was raised to {v} A, above the 12 A it had"));
        Assert.DoesNotContain(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOn);
    }

    [Fact]
    public void The_live_example_parses_with_every_device_restore_only()
    {
        // deploy/examples/switchboard.yaml declares no start: key; the shipped default applies.
        var configuration = TestConfigurations.Valid(TestConfigurations.Example).Configuration!;

        Assert.Equal(4, configuration.Devices.Length);
        Assert.All(configuration.Devices, d => Assert.Equal(StartPolicy.RestoreOnly, d.Start));
    }

    [Fact]
    public void A_device_key_overrides_the_default_and_an_unknown_policy_is_refused()
    {
        // TestDevices.Document defaults every device to headroom (the Stage 3 scenarios).
        var mixed = TestConfigurations.Valid(TestDevices.Document(WithStart(DispatchDevices.Heaters, "heater_2", "restore_only"))).Configuration!;
        Assert.Equal(
            [StartPolicy.Headroom, StartPolicy.RestoreOnly, StartPolicy.Headroom],
            mixed.Devices.OrderBy(d => d.Id.Value, StringComparer.Ordinal).Select(d => d.Start));

        var refused = TestConfigurations.Parse(TestDevices.Document(WithStart(DispatchDevices.Heaters, "heater_2", "eager")));
        Assert.Null(refused.Configuration);
        Assert.Contains(refused.Errors, e => e.Path.Contains("start", StringComparison.Ordinal));
    }

    private static string WithStart(string devices, string actuator, string start) =>
        devices.ReplaceLineEndings("\n").Replace($"    actuator: switch.{actuator}", $"    actuator: switch.{actuator}\n    start: {start}", StringComparison.Ordinal);

    private static string RestoreOnly(string devices) =>
        devices.ReplaceLineEndings("\n").Replace("    value_per_kwh:", "    start: restore_only\n    value_per_kwh:", StringComparison.Ordinal);
}
