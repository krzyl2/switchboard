using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// EV-02 by structure (docs/EV.md B7, CLAUDE.md "Protection outranks everything"): the car's SoC, range, plugged state and calendar, and the trip parsed from
/// them, are the EV module's inputs only. No type the guard or dispatch is built from may reach them, no guard, dispatch or effector source may name them,
/// and the code that reads them commands nothing, knows no entity and reads no wall clock. The pattern is that of <see cref="GoalArchitectureTests"/>.
/// </summary>
public sealed partial class EvSignalArchitectureTests
{
    private static bool IsEvType(Type type) =>
        type.Namespace is { } ns && ns.StartsWith("Switchboard.Domain.Ev", StringComparison.Ordinal)
        || type == typeof(EvInputs) || type == typeof(EvRead) || type == typeof(EvInputView) || type == typeof(IEvInputSource)
        || type == typeof(CalendarEvent) || type == typeof(CalendarEventValue) || type == typeof(DistanceValue) || type == typeof(ParsedCalendar);

    [Fact]
    public void No_type_the_guard_or_dispatch_is_built_from_reaches_an_ev_type()
    {
        // Why: a calendar or a SoC in anything the guard or dispatch holds would be the first step to letting a trip, a plugged flag or a battery level move a
        // limit. Protection decides from breaker data alone.
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

        var offenders = roots.SelectMany(r => SignalArchitectureTests.TypeGraph(r).Where(IsEvType).Select(t => $"{r.Name} -> {t.FullName}")).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_guard_dispatch_or_effector_source_names_an_ev_signal_role_kind_or_type()
    {
        // Why: the same promise as text. A role or kind is how a signal reaches a reader; if no guard, dispatch or effector file names the EV ones, none can read them.
        var files = new[] { "src/Switchboard.Domain/Guard", "src/Switchboard.Domain/Dispatch", "src/Switchboard.Application/Guard", "src/Switchboard.Application/Dispatch", "src/Switchboard.Application/Effector" }
            .SelectMany(SourceFiles)
            .ToList();

        var offenders = files.Where(f => EvWord().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count > 25, "the guard, dispatch and effector namespaces were not found");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_store_builds_protection_and_dispatch_inputs_from_no_ev_role()
    {
        // Why: ProtectionInputs and DispatchInputs are built in the signal store from named roles. The store names no EV role at all: it handles the EV signals as
        // ordinary signals (kind, freshness), and the EV view picks them by role out of the snapshot, so no builder of the two decision inputs can see them.
        var store = File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Application", "Signals", "SignalStore.cs"));

        Assert.DoesNotMatch(EvRole(), store);
    }

    [Fact]
    public void The_goal_inputs_still_read_only_the_four_goal_roles()
    {
        // Why: B7 says do not widen GoalInputs. Its role list is private, so the file is the evidence.
        var goalInputs = File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Application", "Signals", "GoalInputs.cs"));

        Assert.DoesNotMatch(EvWord(), goalInputs);
        Assert.Contains("[SignalRole.DeviceSoc, SignalRole.GoalTarget, SignalRole.GoalDeadline, SignalRole.GoalWant]", goalInputs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_code_that_reads_ev_signals_commands_nothing_knows_no_entity_and_reads_no_wall_clock()
    {
        // Why: the module proposes, it never commands; the core never knows an entity ID; time is an argument (injected clock).
        var files = new[]
        {
            RepoPaths.Combine("src", "Switchboard.Domain", "Ev", "TripParser.cs"),
            RepoPaths.Combine("src", "Switchboard.Application", "Signals", "EvInputs.cs"),
            RepoPaths.Combine("src", "Switchboard.Infrastructure", "HomeAssistant", "CalendarFields.cs"),
            RepoPaths.Combine("src", "Switchboard.Host", "Web", "EvEndpoint.cs"),
        };

        var commanding = files.Where(f => Commanding().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        var clocks = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"DateTime(Offset)?\.(Now|UtcNow)|TimeProvider\.System|TimeZoneInfo\.Local")).Select(Path.GetFileName).ToList();
        var entities = files.Take(2).Where(f => EntityLiteral().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.Empty(commanding);
        Assert.Empty(clocks);
        Assert.Empty(entities);
    }

    [Fact]
    public void The_ev_api_writes_only_the_modules_own_state_and_never_through_a_command_path()
    {
        // Why: EV-01/02 are read-only; S6 (EV-09) adds exactly three writes, the mode and the charge-now, which change the add-on's own state (ev.json, a manual goal)
        // and nothing else. Any other verb or route here would be a new command path, and the endpoint names no type that sends a command.
        var endpoint = File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Host", "Web", "EvEndpoint.cs"));
        var writes = System.Text.RegularExpressions.Regex.Matches(endpoint, @"Map(Post|Put|Delete|Patch)\(""([^""]+)""").Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}").Order().ToList();

        Assert.Contains("MapGet(\"/api/ev\"", endpoint, StringComparison.Ordinal);
        Assert.Equal(["Delete /api/ev/charge-now", "Post /api/ev/charge-now", "Post /api/ev/mode"], writes);
        Assert.DoesNotMatch(Commanding(), endpoint);
    }

    [Fact]
    public void The_scans_do_catch_what_they_look_for()
    {
        Assert.Matches(EvWord(), "var p = SignalRole.EvPlugged;");
        Assert.Matches(EvWord(), "kind == SignalKind.CalendarEvent");
        Assert.Matches(EvWord(), "using Switchboard.Domain.Ev;");
        Assert.Matches(EvWord(), "TripParser.Assess(e, now, max)");
        Assert.DoesNotMatch(EvWord(), "var evening = 3; // an event of the day");
        Assert.Matches(Commanding(), "private readonly IDeviceCommandPort _port;");
    }

    [Theory]
    [InlineData("var p = SignalRole.EvRange;")]
    [InlineData("var i = EvInputs.Empty;")]
    [InlineData("var i = EvInput;")]
    [InlineData("EvInputView view;")]
    [InlineData("IEvInputSource source;")]
    [InlineData("EvRead read;")]
    [InlineData("TripParser.Assess(e, n, m);")]
    public void The_store_scan_catches_each_of_its_alternatives(string line)
    {
        Assert.Matches(EvRole(), line);
    }

    [Fact]
    public void The_store_scan_ignores_words_that_only_contain_an_alternative()
    {
        Assert.DoesNotMatch(EvRole(), "var Prevread = 1; var xEvRead = 2; MyTripParserX t; SignalRoles.Evening;");
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(RepoPaths.Combine(root.Split('/')), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"SignalRole\.Ev|SignalKind\.(Distance|CalendarEvent)|\bEvInputs?\b|\bEvInputView\b|\bIEvInputSource\b|\bEvRead\b|\bEvDefinition\b|\bTripParser\b|\bTripAssessment\b|\bCalendarEvent(Value)?\b|\bDistanceValue\b|Switchboard\.Domain\.Ev", RegexOptions.CultureInvariant)]
    private static partial Regex EvWord();

    [GeneratedRegex(@"SignalRole\.Ev|\bEvInputs?\b|\bEvInputView\b|\bIEvInputSource\b|\bEvRead\b|\bTripParser\b", RegexOptions.CultureInvariant)]
    private static partial Regex EvRole();

    [GeneratedRegex(@"IDeviceCommandPort|ICallServiceChannel|EffectorGate|DeviceOverrides|IDeviceOverrideControl|KillSwitch|IControlSwitch|ControlSwitchState|DeviceTargets|HomeAssistantDeviceCommandPort|GuardEngine|DispatchEngine|LockoutClocks|GuardRunner|ManualGoalStore", RegexOptions.CultureInvariant)]
    private static partial Regex Commanding();

    [GeneratedRegex(@"\b(sensor|switch|number|input_number|input_boolean|input_datetime|binary_sensor|select|input_select|calendar|climate|sun|weather)\.[a-z0-9_]+")]
    private static partial Regex EntityLiteral();
}
