using System.Reflection;
using Switchboard.Application;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Application.Modes;
using Switchboard.Application.Setup;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.ArchitectureProbe;

namespace Switchboard.Tests;

/// <summary>
/// Call path to the command port (SAFE-05 and the maintainer's kickoff rule as structure): dispatch
/// decides, but nothing in production can command a Home Assistant entity. The command port exists
/// only as an interface; its one production implementation is the gated Home Assistant port, and
/// the tests hand the effector gate only the recording double. The gate addresses only devices of
/// the parsed configuration, by logical name. The presentation (the runtime UI) and the setup code
/// hold no command port, kill switch or override control.
/// </summary>
public sealed class CommandPathArchitectureTests
{
    private static readonly Assembly[] Production =
    [
        typeof(Switchboard.Domain.Signals.SignalSnapshot).Assembly,
        typeof(AppVersion).Assembly,
        typeof(HomeAssistantClient).Assembly,
        typeof(SwitchboardServices).Assembly,
    ];

    private static readonly Type[] Setup = [.. typeof(SetupService).Assembly.GetTypes().Where(t => t.Namespace?.StartsWith("Switchboard.Application.Setup", StringComparison.Ordinal) == true)];

    [Fact]
    public void Command_port_has_exactly_one_production_implementation()
    {
        // Stage C: one gated production path (SAFE-05). Tests have the recording double only.
        var implementations = Production.SelectMany(a => a.GetTypes()).Where(t => typeof(IDeviceCommandPort).IsAssignableFrom(t) && t != typeof(IDeviceCommandPort)).ToList();
        var inTests = typeof(CommandPathArchitectureTests).Assembly.GetTypes().Where(t => typeof(IDeviceCommandPort).IsAssignableFrom(t) && !t.IsInterface).ToList();

        Assert.Equal([typeof(Switchboard.Infrastructure.HomeAssistant.HomeAssistantDeviceCommandPort)], implementations);
        Assert.Equal([typeof(RecordingDeviceCommandPort)], inTests);
    }

    [Fact]
    public void Only_the_gate_and_the_one_implementation_name_the_port()
    {
        // No production source names the port type beyond its declaration, the gate that holds it
        // and its one implementation; the composition hands the gate the concrete port.
        var visibleTo = typeof(AppVersion).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(a => a.AssemblyName).ToList();
        Assert.Equal(["Switchboard.Tests"], visibleTo);

        var mentions = SourceFiles("src")
            .Where(f => File.ReadAllText(f).Contains(nameof(IDeviceCommandPort), StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["EffectorGate.cs", "HomeAssistantDeviceCommandPort.cs", "IDeviceCommandPort.cs"], mentions);
    }

    [Fact]
    public void Port_signatures_carry_no_entity_id()
    {
        // Logical names and levels only: a device name has no '.', so it can never be an entity ID.
        // The entity is looked up at send time, in the configuration layer (ActuatorDirectory).
        var parameters = typeof(IDeviceCommandPort).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.ParameterType).Distinct().ToList();
        Assert.True(parameters.ToHashSet().SetEquals([typeof(DeviceCommand), typeof(CommandResult).MakeByRefType()]), string.Join(", ", parameters.Select(t => t.Name)));

        var command = typeof(DeviceCommand).GetProperties().Select(p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).Distinct().ToList();
        Assert.True(
            command.ToHashSet().SetEquals([typeof(DeviceName), typeof(DeviceCommandKind), typeof(int), typeof(bool), typeof(Amperes), typeof(CommandPriority), typeof(long)]),
            string.Join(", ", command.Select(t => t.Name)));
        Assert.DoesNotContain(typeof(string), command);
        Assert.DoesNotContain(typeof(EntityId), command);
    }

    [Fact]
    public void Gate_addresses_only_devices_of_the_parsed_configuration()
    {
        // Fuzzed: dispatch wishes for names nobody declared are offered to an active gate holding a
        // port; the port is never called for any of them, only for the declared heater.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 1\n", mode: DispatchScenario.Active, port: port);
        s.PollUntil(GuardScenario.Start + TimeSpan.FromSeconds(30), 3, 3, 3);
        var random = new Random(24);
        var intruders = Enumerable.Range(0, 200)
            .Select(_ => "x" + new string([.. Enumerable.Range(0, random.Next(1, 30)).Select(_ => "abcdefghijklmnopqrstuvwxyz0123456789_"[random.Next(37)])]))
            .Select(DeviceName.Parse)
            .Where(n => s.Setup.Device(n) is null)
            .ToList();
        var dispatch = new DispatchEvaluation(
            s.Now,
            [.. intruders.Select(n => new DispatchWish(n, DeviceLevel.Relay(true), Switchboard.Domain.Decisions.ReasonCode.HeadroomAvailable, Switchboard.Application.Decisions.DecisionOrigin.Dispatch, DispatchChange.Increase, null, true, s.Dispatches[^1].Wishes[0].Inputs))],
            [],
            [],
            [],
            []);

        s.Gate.Apply(s.Last, dispatch, OverrideSnapshot.None, s.Mode, new ControlInputs(s.Inputs(), s.DispatchInputs(), s.DispatchSetup), s.Now);

        Assert.All(port.Commands, c => Assert.StartsWith("heater_", c, StringComparison.Ordinal));
        Assert.DoesNotContain(port.Commands, c => intruders.Any(i => c.StartsWith(i.Value + " ", StringComparison.Ordinal)));
        Assert.Equal(["heater_l1 relay 1 on"], port.Commands);
    }

    [Fact]
    public void Invalid_configuration_lets_the_gate_address_last_valid_devices_with_off_only()
    {
        // "Declared" is the guard's last valid setup: during an invalid document the gate may still
        // switch those devices off (safe state), never on.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, [], mode: DispatchScenario.Active, port: port);
        s.PollUntil(GuardScenario.Start + TimeSpan.FromSeconds(20), 3, 3, 3);

        s.ConfigurationValid(false);
        s.Evaluate();

        Assert.Equal(["heater_l1 relay 1 off", "heater_l2 relay 1 off", "heater_l3 relay 1 off"], port.Commands.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(port.Commands, c => c.EndsWith(" on", StringComparison.Ordinal));
    }

    [Fact]
    public void Device_removed_while_targeted_on_is_not_commanded_and_recorded()
    {
        // SAFE-05 read literally: once a reload no longer declares a device it is never addressed
        // again, even to switch it off; a decision says it left control while targeted on.
        var port = new RecordingDeviceCommandPort();
        const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(GuardScenario.Start + TimeSpan.FromSeconds(30), 3, 3, 3);
        Assert.Equal(["heater_l1 relay 1 on"], port.Commands);
        var withoutHeater = DispatchDevices.Heaters[DispatchDevices.Heaters.IndexOf("  - id: heater_l2", StringComparison.Ordinal)..];

        s.Reconfigure(TestDevices.Document(withoutHeater, OneBoundary));
        s.PollUntil(GuardScenario.Start + TimeSpan.FromMinutes(2), 3, 3, 3);

        Assert.Equal(["heater_l1 relay 1 on"], port.Commands);
        var left = s.DecisionsFor("heater_l1").Last();
        Assert.Equal((Switchboard.Application.Decisions.DecisionAction.Release, Switchboard.Domain.Decisions.ReasonCode.SafeState, false), (left.Action, left.Reason, left.Commanded));
        Assert.Null(s.Ledger("heater_l1"));
    }

    [Fact]
    public void Production_mode_is_shadow_and_records_nothing_commanded()
    {
        // ModeStateTests: an active option stays shadow (active_not_enabled). The production gate
        // has no port: even a forced active mode commands nothing and records shadow.
        Assert.Equal(OperatingMode.Shadow, TestModes.State(OperatingMode.Active, controlAllowed: false).Current.Effective);
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 1\n", mode: DispatchScenario.Active);
        s.PollUntil(GuardScenario.Start + TimeSpan.FromSeconds(30), 3, 3, 3);

        var start = Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((false, OperatingMode.Shadow, Switchboard.Domain.Decisions.ReasonCode.ModeShadow), (start.Commanded, start.Mode, start.RefusedBy!.Value));
    }

    [Fact]
    public void Presentation_references_no_command_port_kill_switch_or_override_control()
    {
        var forbidden = new[] { typeof(IDeviceCommandPort), typeof(IKillSwitchControl), typeof(KillSwitchState), typeof(IDeviceOverrideControl), typeof(DeviceOverrides), typeof(EffectorGate) };
        var offenders = Presentation
            .SelectMany(t => Signatures(t).Where(s => forbidden.Any(f => f.IsAssignableFrom(s))).Select(s => $"{t.Name}: {s.Name}"))
            .ToList();

        Assert.NotEmpty(Presentation);
        Assert.Empty(offenders);
    }

    [Fact]
    public void Setup_never_references_command_port_kill_switch_or_override_control()
    {
        var forbidden = new[] { typeof(IDeviceCommandPort), typeof(IKillSwitchControl), typeof(KillSwitchState), typeof(IDeviceOverrideControl), typeof(DeviceOverrides), typeof(EffectorGate), typeof(GuardEngine), typeof(DispatchEngine) };
        var offenders = Setup
            .SelectMany(t => DeclaredSignatures(t).Where(s => forbidden.Any(f => f.IsAssignableFrom(s))).Select(s => $"{t.Name}: {s.Name}"))
            .ToList();

        Assert.NotEmpty(Setup);
        Assert.Empty(offenders);
        foreach (var file in SourceFiles("src/Switchboard.Application/Setup").Append(RepoPaths.Combine("src", "Switchboard.Host", "Web", "SetupEndpoint.cs")))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("IDeviceCommandPort", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IKillSwitchControl", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IDeviceOverrideControl", source, StringComparison.Ordinal);
        }
    }
}
