using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application.Goals;
using Switchboard.Domain.Goals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// The manual goal (PLAN-19) only stores data in the add-on's own state and states a request to the planner. Why it matters: "want now" sits next to a
/// car charger, and a person clicking it must never be able to make the add-on command anything or name an entity: the store holds no command port, effector,
/// override or kill switch and knows no entity ID, the endpoint calls nothing that writes to Home Assistant, and nothing under the guard or dispatch can reach
/// a manual goal (the plan reaches dispatch only behind the planner.dispatch flag, slice 5).
/// </summary>
public sealed partial class ManualGoalArchitectureTests
{
    private static readonly string[] Files =
    [
        RepoPaths.Combine("src", "Switchboard.Domain", "Goals", "ManualGoal.cs"),
        RepoPaths.Combine("src", "Switchboard.Application", "Goals", "ManualGoalStore.cs"),
        RepoPaths.Combine("src", "Switchboard.Host", "Web", "GoalsEndpoint.cs"),
    ];

    [Fact]
    public void The_manual_goal_code_commands_nothing_and_names_no_entity()
    {
        // Why: never command an entity the user has not declared, and the core never knows an entity ID: the store, the value and the endpoint mention
        // neither the command path nor an entity literal, nor Home Assistant's service call.
        var offenders = Files.Where(f => Commanding().IsMatch(File.ReadAllText(f)) || EntityLiteral().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.Equal(3, Files.Count(File.Exists));
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_manual_goal_store_is_built_only_from_the_goal_inputs_the_clock_and_a_logger()
    {
        // Why: the structural form of the same promise: its constructor takes no port, gate, switch or override, so there is nothing for it to call. The one optional
        // extra is a read-only door to a module's SoC estimate (the EV device's synthetic goal).
        var constructor = typeof(ManualGoalStore).GetConstructors().Single();

        Assert.Equal(
            [typeof(Switchboard.Application.Signals.IGoalInputSource), typeof(Switchboard.Application.Signals.SignalClock), typeof(Microsoft.Extensions.Logging.ILogger<ManualGoalStore>), typeof(IModuleSocSource)],
            constructor.GetParameters().Select(p => p.ParameterType));
        var offenders = typeof(ManualGoalStore).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(m => m is FieldInfo f ? [f.FieldType] : m is PropertyInfo p ? [p.PropertyType] : Array.Empty<Type>())
            .Where(t => t.Namespace is { } ns && (ns.StartsWith("Switchboard.Application.Effector", StringComparison.Ordinal) || ns.StartsWith("Switchboard.Application.Guard", StringComparison.Ordinal) || ns.StartsWith("Switchboard.Application.Dispatch", StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_type_the_guard_or_dispatch_is_built_from_reaches_a_manual_goal()
    {
        var roots = new[]
        {
            typeof(Switchboard.Domain.Signals.ProtectionInputs),
            typeof(Switchboard.Domain.Signals.DispatchInputs),
            typeof(Switchboard.Domain.Devices.DeviceSpec),
            typeof(Switchboard.Domain.Guard.ProtectionSetup),
            typeof(Switchboard.Domain.Dispatch.DispatchSetup),
            typeof(Switchboard.Application.Guard.GuardInput),
            typeof(Switchboard.Application.Dispatch.DispatchInput),
            typeof(Switchboard.Application.Dispatch.DispatchStatus),
        };

        var offenders = roots.SelectMany(r => SignalArchitectureTests.TypeGraph(r).Where(t => t == typeof(ManualGoal) || t == typeof(ManualGoalStore) || t == typeof(ManualGoalSnapshot)).Select(t => $"{r.Name} -> {t.Name}")).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scan_does_catch_what_it_looks_for()
    {
        Assert.Matches(Commanding(), "private readonly IDeviceCommandPort _port;");
        Assert.Matches(Commanding(), "channel.CallService(\"switch\", \"turn_on\")");
        Assert.Matches(EntityLiteral(), "\"number.charger_current\"");
        Assert.DoesNotMatch(Commanding(), "var goals = new ManualGoalStore(inputs, clock, logger);");
    }

    [GeneratedRegex(@"IDeviceCommandPort|ICallServiceChannel|EffectorGate|DeviceOverrides|IDeviceOverrideControl|KillSwitch|IControlSwitch|ControlSwitchState|DeviceTargets|HomeAssistantDeviceCommandPort|GuardEngine|DispatchEngine|call_service|CallService", RegexOptions.CultureInvariant)]
    private static partial Regex Commanding();

    [GeneratedRegex(@"\b(sensor|switch|number|input_number|input_boolean|input_datetime|binary_sensor|select|input_select|climate|sun|weather)\.[a-z0-9_]+")]
    private static partial Regex EntityLiteral();
}
