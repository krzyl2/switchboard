using System.Text.RegularExpressions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Ev;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// EV-06 by structure (docs/EV.md S5, CLAUDE.md "Protection outranks everything"): the EV module proposes goals and nothing else. It has no path to the effector,
/// the command port, the device overrides or the guard; the guard's step reads it through one narrow door that does no work; the guard, dispatch and planner sources
/// do not know it exists. The pattern is that of <see cref="EvSignalArchitectureTests"/> and <see cref="GoalArchitectureTests"/>.
/// </summary>
public sealed partial class EvGoalArchitectureTests
{
    private static readonly string[] ModuleFiles =
    [
        RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvSnapshot.cs"),
        RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvEngine.cs"),
        RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvSnapshotKeeper.cs"),
        RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvGoalSource.cs"),
        RepoPaths.Combine("src", "Switchboard.Domain", "Ev", "EvComposer.cs"),
        RepoPaths.Combine("src", "Switchboard.Host", "Hosting", "EvSnapshotHostedService.cs"),
    ];

    [Fact]
    public void The_module_names_no_effector_command_port_override_guard_or_dispatch_type()
    {
        // Why: the module proposes, it never commands. If none of its files can even name the types that send a command, switch a device off, hold an override or run
        // the guard, it has no path to any of them.
        var offenders = ModuleFiles.Where(f => Commanding().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.Equal(6, ModuleFiles.Length);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_manual_controls_reach_only_the_add_ons_own_state_never_an_effector_command_port_override_or_guard()
    {
        // Why: EV-09. The mode and "charge now" come from a person (web API, MQTT). They may change ev.json and a manual goal and nothing else: these files may name
        // the manual goal store's interface (that is where a charge-now lives) but none of the types that send a command, hold an override or run the guard.
        var files = new[]
        {
            RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvControl.cs"),
            RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvManualState.cs"),
            RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvModuleSocSource.cs"),
            RepoPaths.Combine("src", "Switchboard.Infrastructure", "Mqtt", "EvEntities.cs"),
        };

        var offenders = files.Where(f => Commanding().IsMatch(StripComments(File.ReadAllText(f)).Replace("IManualGoalControl", string.Empty, StringComparison.Ordinal))).Select(Path.GetFileName).ToList();
        var entities = files.Where(f => EntityLiteral().IsMatch(StripComments(File.ReadAllText(f)))).Select(Path.GetFileName).ToList();

        Assert.Equal(4, files.Count(File.Exists));
        Assert.Empty(offenders);
        Assert.Empty(entities);
    }

    [Fact]
    public void The_module_reads_no_wall_clock_and_knows_no_entity()
    {
        // Why: time is an argument (injected clock, virtual in tests; 23- and 25-hour days), and the core never knows an entity ID.
        var clocks = ModuleFiles.Where(f => Regex.IsMatch(File.ReadAllText(f), @"DateTime(Offset)?\.(Now|UtcNow)|TimeProvider\.System|TimeZoneInfo\.Local")).Select(Path.GetFileName).ToList();
        var entities = ModuleFiles.Where(f => EntityLiteral().IsMatch(StripComments(File.ReadAllText(f)))).Select(Path.GetFileName).ToList();

        Assert.Empty(clocks);
        Assert.Empty(entities);
    }

    [Fact]
    public void The_goal_source_only_reads_the_snapshot_so_the_guards_step_does_no_work_in_it()
    {
        // Why: B1. The source runs inside the guard's step. It must not be able to touch a file, a signal, the presentation, the engine or a task: it filters an
        // immutable snapshot by the clock, and that is all.
        var source = StripComments(File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Application", "Ev", "EvGoalSource.cs")));

        Assert.DoesNotMatch(@"IPresentation|IEvInputSource|EvEngine|EvStateFile|EvInputs|PlanHolder|\bFile\.|\bDirectory\.|\bTask\b|\basync\b|\bawait\b|\block\b|Thread", source);
        Assert.Contains("IEvSnapshotSource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void No_guard_dispatch_effector_or_planner_source_names_the_module()
    {
        // Why: the plan reaches dispatch only through IPlanView, and the planner knows goals, not their author. If none of these files names an EV type, neither the
        // guard's decision nor the planner's can depend on the module except through the goals it states.
        var roots = new[]
        {
            "src/Switchboard.Domain/Guard", "src/Switchboard.Domain/Dispatch", "src/Switchboard.Application/Guard", "src/Switchboard.Application/Dispatch",
            "src/Switchboard.Application/Effector", "src/Switchboard.Domain/Planning", "src/Switchboard.Application/Planning", "src/Switchboard.Domain/Goals",
        };
        var files = roots.SelectMany(SourceFiles).ToList();

        var offenders = files.Where(f => ModuleWord().IsMatch(StripComments(File.ReadAllText(f)))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count > 40, "the guard, dispatch, effector and planner namespaces were not found");
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_type_the_guard_or_dispatch_is_built_from_reaches_a_module_type()
    {
        // Why: a snapshot of the car in anything the guard or dispatch holds would be the first step to letting a trip or a SoC move a limit.
        var roots = new[]
        {
            typeof(ProtectionInputs),
            typeof(DispatchInputs),
            typeof(Switchboard.Domain.Devices.DeviceSpec),
            typeof(Switchboard.Domain.Guard.ProtectionSetup),
            typeof(Switchboard.Domain.Dispatch.DispatchSetup),
            typeof(Switchboard.Application.Guard.GuardInput),
            typeof(DispatchInput),
            typeof(DispatchStatus),
        };

        var offenders = roots
            .SelectMany(r => SignalArchitectureTests.TypeGraph(r).Where(t => t.Namespace == typeof(EvSnapshot).Namespace).Select(t => $"{r.Name} -> {t.FullName}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_module_is_the_only_new_goal_source_and_its_snapshot_is_immutable()
    {
        // Why: the offer is published whole and never changed (B1); a mutable member would let the keeper change what the guard's step is reading.
        var mutable = new[] { typeof(EvSnapshot), typeof(EvPublished) }
            .SelectMany(t => t.GetProperties().Where(p => p.SetMethod is { IsPublic: true } set && !set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit")))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        Assert.Empty(mutable);
    }

    [Fact]
    public void The_scans_do_catch_what_they_look_for()
    {
        Assert.Matches(Commanding(), "private readonly EffectorGate _gate;");
        Assert.Matches(Commanding(), "var overrides = new DeviceOverrides();");
        Assert.Matches(ModuleWord(), "var s = new EvGoalSource(a, b);");
        Assert.Matches(ModuleWord(), "using Switchboard.Application.Ev;");
        Assert.DoesNotMatch(ModuleWord(), "var evening = 3; var level = 1;");
        Assert.Matches(EntityLiteral(), "var x = \"sensor.car_battery_level\";");
        Assert.DoesNotMatch(EntityLiteral(), StripComments("// the sensor.car_battery_level of the car\nvar x = 1;"));
    }

    private static string StripComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(RepoPaths.Combine(root.Split('/')), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"IDeviceCommandPort|ICallServiceChannel|EffectorGate|DeviceOverrides|IDeviceOverrideControl|KillSwitch|IControlSwitch|ControlSwitchState|DeviceTargets|HomeAssistantDeviceCommandPort|GuardEngine|DispatchEngine|LockoutClocks|GuardRunner|GuardStep|ManualGoalStore|IManualGoalControl|IPlanView", RegexOptions.CultureInvariant)]
    private static partial Regex Commanding();

    [GeneratedRegex(@"\b(sensor|switch|number|input_number|input_boolean|input_datetime|binary_sensor|select|input_select|calendar|climate|sun|weather)\.[a-z0-9_]+")]
    private static partial Regex EntityLiteral();

    [GeneratedRegex(@"\bEvGoalSource\b|\bEvSnapshot(Keeper)?\b|\bEvEngine\b|\bEvPublished\b|\bIEvSnapshotSource\b|\bEvComposer\b|\bEvAssessmentCodes?\b|Switchboard\.Application\.Ev\b|Switchboard\.Domain\.Ev\b", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleWord();
}
