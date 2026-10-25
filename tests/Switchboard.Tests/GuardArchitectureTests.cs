using System.Reflection;
using Switchboard.Application;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;

namespace Switchboard.Tests;

/// <summary>
/// Stage 2 is shadow only, structurally: nothing the guard or the effector gate can reach is able
/// to send anything to Home Assistant or MQTT, and a device name can never be an entity ID, so
/// protection cannot learn one (SIG-01, SAFE-05, MODE-01).
/// </summary>
public sealed class GuardArchitectureTests
{
    private static readonly Assembly Application = typeof(AppVersion).Assembly;

    [Fact]
    public void Gate_has_no_path_to_a_command_sender()
    {
        // Everything the gate, the engines and the runner hold or receive comes from Application,
        // Domain, the BCL or logging: no transport, no client, nothing named like a sender of
        // commands. The one exception is the gate's command port (an Application interface, Stage C),
        // and the gate is the only one of them that holds it: the runner, the engines and the kill
        // switch can never send anything themselves (ControlArchitectureTests pins who calls it).
        var port = typeof(IDeviceCommandPort);
        var types = new[] { typeof(EffectorGate), typeof(GuardRunner), typeof(GuardEngine), typeof(DispatchEngine), typeof(KillSwitchState), typeof(LockoutClocks), typeof(DeviceOverrides) };
        var offenders = types
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Where(f => f.FieldType != port).Select(f => f.FieldType)
                .Concat(t.GetConstructors().SelectMany(c => c.GetParameters()).Where(p => p.ParameterType != port).Select(p => p.ParameterType)))
            .SelectMany(Unwrap)
            .Where(type => !Allowed(type) || type.Name.Contains("Command", StringComparison.Ordinal) || type.Name.Contains("Mqtt", StringComparison.Ordinal) || type.Name.Contains("HomeAssistant", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
        Assert.Equal(
            [typeof(EffectorGate)],
            types.Where(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Any(f => f.FieldType == port)
                || t.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SelectMany(c => c.GetParameters()).Any(p => p.ParameterType == port)));
        Assert.Equal(typeof(AppVersion).Assembly, port.Assembly);
    }

    [Fact]
    public void Guard_runs_without_any_mqtt_or_home_assistant_type()
    {
        // Fail-safe row "MQTT disconnected": the guard does not depend on MQTT or Home Assistant at
        // all, so a broker outage cannot change a single guard decision.
        var references = Application.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("MQTTnet", StringComparison.Ordinal) || r.StartsWith("Switchboard.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Application.GetTypes().Where(t => t.Namespace is "Switchboard.Application.Guard" or "Switchboard.Application.Effector"),
            t => t.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => p.ParameterType.Namespace?.Contains("Health", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Protection_device_names_cannot_be_entity_ids()
    {
        // A device or group name has no '.', an entity ID always has one: no string is both.
        foreach (var candidate in new[] { "switch.heater", "heater", "number.goe_amp", "ev_charger", "a", "sensor.x_y", "carport" })
        {
            var isName = DeviceName.TryParse(candidate, out _) || Switchboard.Domain.Connection.GroupName.TryParse(candidate, out _);
            var isEntity = EntityId.TryParse(candidate, out _);
            Assert.False(isName && isEntity, candidate);
        }

        Assert.False(DeviceName.TryParse("switch.heater_1", out _));
        Assert.False(DeviceName.TryParse(new string('a', 41), out _));
    }

    [Fact]
    public void The_guard_reads_the_control_port_only()
    {
        // SIG-04: the runner's one input port is IControlInputSource (protection and dispatch views
        // of one store cycle); it never depends on the diagnostics view or the snapshot holder, and
        // the control inputs expose no full snapshot.
        var parameters = typeof(GuardRunner).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(IControlInputSource), parameters);
        Assert.DoesNotContain(typeof(IProtectionInputSource), parameters);
        Assert.DoesNotContain(typeof(ISignalDiagnosticsSource), parameters);
        Assert.DoesNotContain(typeof(SignalSnapshotHolder), parameters);
        Assert.DoesNotContain(typeof(Switchboard.Domain.Signals.SignalSnapshot), SignalArchitectureTests.TypeGraph(typeof(ControlInputs)));
    }

    private static bool Allowed(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        return assembly is "Switchboard.Application" or "Switchboard.Domain"
            || type.Namespace?.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !type.Namespace.StartsWith("System.Net", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsArray || type.IsByRef ? type.GetElementType()! : type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }
}
