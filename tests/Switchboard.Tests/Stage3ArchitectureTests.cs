using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// SAFE-05 and the maintainer's kickoff rule as structure: dispatch decides, but nothing in
/// production can command a Home Assistant entity. The command port exists only as an interface;
/// its one implementation is the recording double in the tests, and only the tests can hand it to
/// the effector gate. Even then the gate addresses only devices of the parsed configuration, by
/// logical name. Dispatch reads watts from its own port and amperes only from the guard's envelope.
/// </summary>
public sealed partial class Stage3ArchitectureTests
{
    private static readonly Assembly[] Production =
    [
        typeof(Switchboard.Domain.Signals.SignalSnapshot).Assembly,
        typeof(AppVersion).Assembly,
        typeof(HomeAssistantClient).Assembly,
        typeof(SwitchboardServices).Assembly,
    ];

    [Fact]
    public void Command_port_has_no_implementation_outside_tests()
    {
        var implementations = Production.SelectMany(a => a.GetTypes()).Where(t => typeof(IDeviceCommandPort).IsAssignableFrom(t) && t != typeof(IDeviceCommandPort)).ToList();
        var inTests = typeof(Stage3ArchitectureTests).Assembly.GetTypes().Where(t => typeof(IDeviceCommandPort).IsAssignableFrom(t) && !t.IsInterface).ToList();

        Assert.Empty(implementations);
        Assert.Equal([typeof(RecordingDeviceCommandPort)], inTests);
    }

    [Fact]
    public void Only_tests_can_hand_the_gate_a_port()
    {
        // The public constructors take no port; the internal one is visible to the test assembly
        // only (InternalsVisibleTo), and no production source names the port type at all beyond
        // its own declaration and the gate.
        Assert.All(typeof(EffectorGate).GetConstructors(), c => Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(IDeviceCommandPort)));
        var visibleTo = typeof(AppVersion).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(a => a.AssemblyName).ToList();
        Assert.Equal(["Switchboard.Tests"], visibleTo);

        var mentions = SourceFiles("src")
            .Where(f => File.ReadAllText(f).Contains(nameof(IDeviceCommandPort), StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["EffectorGate.cs", "IDeviceCommandPort.cs"], mentions);
    }

    [Fact]
    public void Port_signatures_carry_no_entity_id()
    {
        // Logical names and levels only: a device name has no '.', so it can never be an entity ID.
        var parameters = typeof(IDeviceCommandPort).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.ParameterType).Distinct().ToList();

        Assert.True(parameters.ToHashSet().SetEquals([typeof(DeviceName), typeof(int), typeof(bool), typeof(Amperes)]), string.Join(", ", parameters.Select(t => t.Name)));
        Assert.DoesNotContain(typeof(string), parameters);
        Assert.DoesNotContain(typeof(EntityId), parameters);
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
        Assert.Equal(OperatingMode.Shadow, new ModeState(OperatingMode.Active, OperatingMode.Active).Current.Effective);
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 1\n", mode: DispatchScenario.Active);
        s.PollUntil(GuardScenario.Start + TimeSpan.FromSeconds(30), 3, 3, 3);

        var start = Assert.Single(s.DecisionsFor("heater_l1"));
        Assert.Equal((false, OperatingMode.Shadow, Switchboard.Domain.Decisions.ReasonCode.ModeShadow), (start.Commanded, start.Mode, start.RefusedBy!.Value));
    }

    [Fact]
    public void Dispatch_reads_dispatch_port_and_guard_envelope_only()
    {
        // SIG-04: dispatch never touches protection inputs or device currents of its own; amperes
        // reach it only through the guard's envelope (and its setup's hysteresis).
        var forbidden = new[] { typeof(ProtectionInputs), typeof(DeviceDrawSample), typeof(IProtectionInputSource), typeof(PhaseProtection), typeof(CurrentReading) };
        foreach (var type in new[] { typeof(DispatchEngine), typeof(Allocator), typeof(ForeignLoadTracker) })
        {
            var signatures = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType)
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType));
            Assert.DoesNotContain(signatures, forbidden.Contains);
        }

        var input = SignalArchitectureTests.TypeGraph(typeof(DispatchInput));
        Assert.DoesNotContain(typeof(ProtectionInputs), input);
        Assert.DoesNotContain(typeof(DeviceDrawSample), input);
    }

    [Fact]
    public void On_change_is_refused_for_every_measurement_role()
    {
        // Only a device state may report on change: a measurement or protection input that stops
        // reporting must go stale (SIG-05).
        foreach (var role in Enum.GetValues<SignalRole>().Where(r => r != SignalRole.DeviceState))
        {
            var definition = new SignalDefinition(SignalName.Parse("x"), EntityId.Parse("sensor.x"), SignalKind.Current, "A", SignConvention.PositiveImport, null, false, TimeSpan.FromSeconds(30), 0.1, role, null, false);
            Assert.Throws<ArgumentException>(() => definition with { Freshness = StateFreshness.OnChange });
        }

        var state = new SignalDefinition(SignalName.Parse("device_x"), EntityId.Parse("switch.x"), SignalKind.State, null, null, null, false, TimeSpan.FromMinutes(10), null, SignalRole.DeviceState, null, false);
        Assert.Equal(StateFreshness.OnChange, (state with { Freshness = StateFreshness.OnChange }).Freshness);
    }

    [Fact]
    public void Allow_list_and_sources_stay_free_of_service_calls()
    {
        // The command port changed nothing outward: the Home Assistant allow-list is unchanged and
        // no literal anywhere names a state-changing service (Stage2ArchitectureTests pins both).
        Assert.Equal(["ping", "subscribe_entities", "unsubscribe_events"], HomeAssistantCommandAllowList.Allowed.Order(StringComparer.Ordinal));
        foreach (var file in SourceFiles("src/Switchboard.Application"))
        {
            Assert.DoesNotMatch(ServiceLiteral(), File.ReadAllText(file));
        }
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(RepoPaths.Combine(root.Split('/')), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex("\"[^\"\\n]*(call_service|api/services|turn_on|turn_off|set_value)[^\"\\n]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceLiteral();
}
