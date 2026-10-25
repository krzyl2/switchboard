using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Domain.Devices;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Effector;

/// <summary>
/// SAFE-05 on a reload that removes a device: the removed device is never commanded again, with one
/// exception. A device Switchboard itself started from off is switched off once more (a final,
/// single-use off of its own actuator), so no load Switchboard added runs on with nobody watching
/// it. A device that was on before Switchboard touched it is left exactly as it is.
/// </summary>
public sealed class RemovedDeviceTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";

    [Fact]
    public void A_device_switchboard_started_gets_one_final_off_when_it_leaves_the_configuration()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1", "heater_l2", "heater_l3"], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);
        Assert.Contains("heater_l1 relay 1 on", port.Commands);
        Assert.NotNull(s.Gate.Ledger.Entry(DeviceName.Parse("heater_l1"))!.SelfStarted);
        var before = port.Submitted.Count;

        s.Reconfigure(TestDevices.Document(Without(DispatchDevices.Heaters, "heater_l1"), OneBoundary));
        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 3, 3, 3);

        var final = Assert.Single(port.Submitted.Skip(before), c => c.Device.Value == "heater_l1");
        Assert.Equal((DeviceCommandKind.Relay, false, true, true), (final.Kind, final.On, final.Retiring, final.Decrease));
    }

    [Fact]
    public void A_device_that_was_on_before_is_left_as_it_is_when_it_leaves_the_configuration()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 3, 3, 3);
        Assert.Null(s.Gate.Ledger.Entry(DeviceName.Parse("heater_l1"))?.SelfStarted);
        var before = port.Submitted.Count;

        s.Reconfigure(TestDevices.Document(Without(DispatchDevices.Heaters, "heater_l1"), OneBoundary));
        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 3, 3, 3);

        Assert.DoesNotContain(port.Submitted.Skip(before), c => c.Device.Value == "heater_l1");
    }

    [Fact]
    public void A_device_restored_to_the_users_own_on_is_left_as_it_is_when_it_leaves_the_configuration()
    {
        // start: headroom, and the user had the heater on. The guard sheds it and dispatch later
        // restores it: switching it back on gives the user's own state back, it is not a start by
        // Switchboard, so removing the device afterwards sends nothing (the final off is "never for a
        // restore to the user's own state").
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 9, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 33, 3, 3);
        Assert.Contains("heater_l1 relay 1 off", port.Commands);
        s.PollUntil(T0 + TimeSpan.FromMinutes(10), 3, 3, 3);
        Assert.Equal("heater_l1 relay 1 on", port.Commands.Last(c => c.StartsWith("heater_l1", StringComparison.Ordinal)));
        Assert.Null(s.Gate.Ledger.Entry(DeviceName.Parse("heater_l1"))!.SelfStarted);
        var before = port.Submitted.Count;

        s.Reconfigure(TestDevices.Document(Without(DispatchDevices.Heaters, "heater_l1"), OneBoundary));
        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 3, 3, 3);

        Assert.DoesNotContain(port.Submitted.Skip(before), c => c.Device.Value == "heater_l1");
    }

    [Fact]
    public void In_shadow_a_removed_device_is_never_commanded()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], OneBoundary, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);

        s.Reconfigure(TestDevices.Document(Without(DispatchDevices.Heaters, "heater_l1"), OneBoundary));
        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 3, 3, 3);

        Assert.Empty(port.Submitted);
        Assert.DoesNotContain(s.Decisions, d => d.Device?.Value == "heater_l1" && d.Commanded);
    }

    /// <summary>The device list without one device (its seven lines).</summary>
    private static string Without(string devices, string id)
    {
        var lines = devices.ReplaceLineEndings("\n").Split('\n').ToList();
        var start = lines.FindIndex(l => l.TrimStart().StartsWith($"- id: {id}", StringComparison.Ordinal));
        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("- id:", StringComparison.Ordinal));
        lines.RemoveRange(start, (end < 0 ? lines.Count : end) - start);
        return string.Join('\n', lines);
    }
}
