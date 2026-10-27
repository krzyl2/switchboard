using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// The goal model and the planner section are structurally apart from protection and from dispatch (15-PLANNER, D2 to D4, CLAUDE.md): in
/// slice 1 a goal is read and shown, it decides nothing. The planner decides when energy is wanted, never whether it is allowed, so no type
/// the guard or dispatch holds may reach a goal, no guard or dispatch source may name one, and the goal code commands nothing and knows no
/// entity. Slice 5 gave dispatch a plan view (an entry of the plan, through <c>IPlanView</c>): dispatch may now name the planner mode it reads and nothing else
/// of the goal model, and the guard, its domain and the effector still name none of it, and see no plan, forecast or price.
/// </summary>
public sealed partial class GoalArchitectureTests
{
    private static readonly Assembly Domain = typeof(GoalSpec).Assembly;
    private static readonly Assembly Application = typeof(AppVersion).Assembly;

    private static bool IsGoalType(Type type) =>
        type.Namespace is { } ns
        && (ns.StartsWith("Switchboard.Domain.Goals", StringComparison.Ordinal)
            || ns.StartsWith("Switchboard.Domain.Planning", StringComparison.Ordinal)
            || ns.StartsWith("Switchboard.Application.Goals", StringComparison.Ordinal))
        || type.Name is "GoalDraft" or "GoalInputs" or "GoalInputSample" or "GoalInputView" or "IGoalInputSource";

    [Fact]
    public void No_type_the_guard_or_dispatch_is_built_from_reaches_a_goal_or_the_planner()
    {
        // Why: D2 to D4. Protection must keep working on fresh breaker data alone, and dispatch must decide as 0.10 did: a goal, a deadline or a
        // planner mode in anything they hold would be the first step to letting a plan move a limit.
        var roots = new[]
        {
            typeof(ProtectionInputs),
            typeof(DispatchInputs),
            typeof(Switchboard.Domain.Devices.DeviceSpec),
            typeof(Switchboard.Domain.Guard.ProtectionSetup),
            typeof(Switchboard.Domain.Dispatch.DispatchSetup),
            typeof(Switchboard.Application.Guard.GuardInput),
            typeof(Switchboard.Application.Dispatch.DispatchInput),
            typeof(Switchboard.Application.Dispatch.DispatchStatus),
        };

        var offenders = roots.SelectMany(r => SignalArchitectureTests.TypeGraph(r).Where(IsGoalType).Select(t => $"{r.Name} -> {t.FullName}")).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_guard_dispatch_or_effector_source_names_a_goal_the_ledger_or_the_planner_beyond_the_mode_dispatch_reads()
    {
        // Why: the same promise as text. The guard, the effector and the gate never read a goal; dispatch reads the plan's entry (a want, a class, a ceiling) and the
        // mode that says whether to, and nothing of the goal model: it never names a goal, the ledger, the tracker or the planner's parameters. The one planner
        // word dispatch may use is the mode enum, so a goal's content can never decide a dispatch step.
        var closed = new[] { "Switchboard.Domain/Guard", "Switchboard.Domain/Dispatch", "Switchboard.Application/Guard", "Switchboard.Application/Effector" }
            .SelectMany(dir => SourceFiles("src/" + dir))
            .ToList();
        var dispatch = SourceFiles("src/Switchboard.Application/Dispatch").ToList();
        var mode = new Regex(@"\bPlannerDispatchMode\b", RegexOptions.CultureInvariant);

        var offenders = closed.Where(f => GoalWord().IsMatch(File.ReadAllText(f)))
            .Concat(dispatch.Where(f => GoalWord().IsMatch(mode.Replace(File.ReadAllText(f), string.Empty))))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(closed.Count > 15, "the guard namespaces were not found");
        Assert.True(dispatch.Count >= 8, "the dispatch namespace was not found");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_goal_code_commands_nothing_and_holds_no_effector_override_or_kill_switch()
    {
        // Why: the planner proposes, it never commands (15-PLANNER, Non-goals): no reference to the command port, the effector gate, the
        // overrides, the control switch or the guard's runtime. The tracker listens to the runner's Stepped event as the presentation does.
        var files = SourceFiles("src/Switchboard.Domain/Goals").Concat(SourceFiles("src/Switchboard.Domain/Planning")).Concat(SourceFiles("src/Switchboard.Application/Goals")).ToList();

        var offenders = files.Where(f => Commanding().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count >= 8);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_domain_goal_and_planner_modules_name_no_entity_and_read_no_clock()
    {
        // Why: the core never knows an entity ID (signals are named logically) and control logic is tested on an injected clock: no
        // DateTime.Now or TimeProvider in the goal maths, which takes every time as an argument.
        var files = SourceFiles("src/Switchboard.Domain/Goals").Concat(SourceFiles("src/Switchboard.Domain/Planning")).ToList();

        var entities = files.Where(f => EntityLiteral().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        var clocks = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"DateTime(Offset)?\.(Now|UtcNow)|TimeProvider|Stopwatch")).Select(Path.GetFileName).ToList();

        Assert.NotEmpty(files);
        Assert.Empty(entities);
        Assert.Empty(clocks);
    }

    [Fact]
    public void The_application_goal_code_and_parsers_name_no_entity_and_read_no_wall_clock()
    {
        // Why: the same rule one layer up. Only the configuration parsers and the signal layer meet an entity ID, and nothing in the goal code
        // reads the machine clock: deadlines and progress run on the injected clock and zone.
        var files = SourceFiles("src/Switchboard.Application/Goals")
            .Append(RepoPaths.Combine("src", "Switchboard.Application", "Configuration", "PlannerSectionParser.cs"))
            .Append(RepoPaths.Combine("src", "Switchboard.Host", "Web", "PlanEndpoint.cs"))
            .ToList();

        var entities = files.Where(f => EntityLiteral().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        var clocks = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"DateTime(Offset)?\.(Now|UtcNow)|TimeProvider\.System|TimeZoneInfo\.Local")).Select(Path.GetFileName).ToList();

        Assert.Empty(entities);
        Assert.Empty(clocks);
    }

    [Fact]
    public void The_goal_model_sees_the_signal_layer_only_through_goal_inputs()
    {
        // Why: SIG-04 by structure: only the signal layer and diagnostics hold the full snapshot. The goal model reads a reduced view of the
        // goal signals by logical name, so a goal can never read watts in protection or amperes in dispatch.
        var wide = new[] { typeof(SignalSnapshot), typeof(SignalReading), typeof(Switchboard.Application.Signals.SignalDiagnostics), typeof(Switchboard.Application.Signals.ISignalDiagnosticsSource) };
        var goalTypes = Application.GetTypes().Where(t => t.Namespace == "Switchboard.Application.Goals").Concat(Domain.GetTypes().Where(IsGoalType)).ToList();

        var offenders = goalTypes
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => Types(m).Where(wide.Contains).Select(s => $"{t.FullName}.{m.Name}: {s.Name}")))
            .ToList();

        Assert.NotEmpty(goalTypes);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scan_does_catch_what_it_looks_for()
    {
        // The scans must be able to fail: each pattern matches a line it is meant to forbid.
        Assert.Matches(GoalWord(), "var plan = planner.Dispatch;");
        Assert.Matches(GoalWord(), "device.Goal?.Hard");
        Assert.Matches(Commanding(), "private readonly IDeviceCommandPort _port;");
        Assert.Matches(EntityLiteral(), "\"sensor.car_battery_level\"");
        Assert.DoesNotMatch(GoalWord(), "var gold = 1; // a hard goal of the guard? no");
    }

    private static IEnumerable<Type> Types(MemberInfo member) => member switch
    {
        MethodBase method => method.GetParameters().Select(p => p.ParameterType).Append(method is MethodInfo info ? info.ReturnType : typeof(void)).SelectMany(Unwrap),
        PropertyInfo property => Unwrap(property.PropertyType),
        FieldInfo field => Unwrap(field.FieldType),
        _ => [],
    };

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsArray || type.IsByRef ? type.GetElementType()! : type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(RepoPaths.Combine(root.Split('/')), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"\b(Planner\w*|PlannerParameters|GoalSpec|GoalRequest|GoalLedger\w*|GoalTracker|GoalBoard|GoalKind|GoalOnMet)\b|\.Planner\b|\.Goal\b|\bplanner\b", RegexOptions.CultureInvariant)]
    private static partial Regex GoalWord();

    [GeneratedRegex(@"IDeviceCommandPort|ICallServiceChannel|EffectorGate|DeviceOverrides|IDeviceOverrideControl|KillSwitch|IControlSwitch|ControlSwitchState|DeviceTargets|HomeAssistantDeviceCommandPort|GuardEngine|DispatchEngine|LockoutClocks", RegexOptions.CultureInvariant)]
    private static partial Regex Commanding();

    [GeneratedRegex(@"\b(sensor|switch|number|input_number|input_boolean|input_datetime|binary_sensor|select|input_select|climate|sun|weather)\.[a-z0-9_]+")]
    private static partial Regex EntityLiteral();
}
