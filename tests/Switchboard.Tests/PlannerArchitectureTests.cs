using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Application.Planning;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// The planner is structurally apart from the guard, dispatch and the effector (15-PLANNER, "The three-loop contract" and Non-goals; CLAUDE.md hard rules): it
/// computes a plan and publishes it to a holder, and that is all. In slice 4 nothing reads the plan, so these tests also hold the other direction: no guard,
/// dispatch or effector source may name a plan type, and no type those are built from may reach one. Why it matters: a planner that could call the effector, or a
/// guard that could read a plan, would put the optimisation above protection, which the design forbids ("protection outranks everything").
/// </summary>
public sealed partial class PlannerArchitectureTests
{
    private static readonly Assembly Domain = typeof(PlanSnapshot).Assembly;
    private static readonly Assembly Application = typeof(AppVersion).Assembly;

    private static readonly string[] PlannerDirectories = ["src/Switchboard.Domain/Planning", "src/Switchboard.Application/Planning"];

    private static readonly string[] ForbiddenNamespaces =
    [
        "Switchboard.Application.Effector",
        "Switchboard.Application.Guard",
        "Switchboard.Application.Dispatch",
        "Switchboard.Domain.Guard",
    ];

    private static IEnumerable<string> SourceFiles(params string[] roots) =>
        roots.SelectMany(root => Directory.GetFiles(RepoPaths.Combine(root.Split('/')), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<Type> PlannerTypes() =>
        Domain.GetTypes().Where(t => t.Namespace == "Switchboard.Domain.Planning")
            .Concat(Application.GetTypes().Where(t => t.Namespace == "Switchboard.Application.Planning"));

    [Fact]
    public void The_planner_code_names_no_effector_command_port_override_kill_switch_or_guard_runtime()
    {
        // Why: the planner proposes, it never commands. The one thing it asks of the guard (stand aside in safe state, kill switch, no Home Assistant) is a yes or
        // no through IPlannerSuspension, implemented at the host's edge, so no source in the planner's directories mentions the effector gate, the command port,
        // overrides, the control or kill switch, or the guard's and dispatch's engines and status types.
        var files = SourceFiles(PlannerDirectories).Append(RepoPaths.Combine("src", "Switchboard.Host", "Hosting", "PlannerHostedService.cs")).ToList();

        var offenders = files.Where(f => Commanding().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count >= 15, $"the planner sources were not found ({files.Count})");
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_member_of_a_planner_type_names_a_type_of_the_effector_guard_or_dispatch_namespaces()
    {
        // Why: the same promise by type, so a rename or an alias cannot slip past the text scan: no field, property, parameter or return type of any planner type
        // (Domain.Planning, Application.Planning) lives in the effector, guard or dispatch namespaces.
        var offenders = PlannerTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => MemberTypes(m).Where(x => ForbiddenNamespaces.Any(ns => x.Namespace?.StartsWith(ns, StringComparison.Ordinal) ?? false)).Select(x => $"{t.FullName}.{m.Name}: {x.FullName}")))
            .ToList();

        Assert.NotEmpty(PlannerTypes());
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_guard_effector_or_dispatch_setup_source_names_a_plan_the_planner_service_or_its_holder()
    {
        // Why: protection never sees a plan, a forecast or a price (D2 to D4). The guard, its domain, the effector (the only thing that commands) and the
        // dispatch domain (the setup and the pure rules) stay free of every plan word; a plan type in any of these would be the first step to a plan moving a limit.
        var files = SourceFiles("src/Switchboard.Domain/Guard", "src/Switchboard.Domain/Dispatch", "src/Switchboard.Application/Guard", "src/Switchboard.Application/Effector").ToList();

        var offenders = files.Where(f => PlanWord().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count > 15, "the guard namespaces were not found");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_dispatch_engine_names_the_plan_only_through_its_view_and_the_names_of_what_it_reads()
    {
        // Why: slice 5 is the first time dispatch reads a plan, and the promise is that it reads one entry through IPlanView and never the holder, the snapshot, the
        // service, the planner's input or its verifier. The only plan words the engine may use are the view and its entry, the want and the mode it reads.
        var files = SourceFiles("src/Switchboard.Application/Dispatch").ToList();
        var allowed = AllowedInDispatch();

        var offenders = files.Where(f => PlanWord().IsMatch(allowed.Replace(File.ReadAllText(f), string.Empty))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count >= 8, "the dispatch sources were not found");
        Assert.Contains(files, f => Path.GetFileName(f) == "DispatchEngine.Plan.cs");
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_type_the_guard_is_built_from_reaches_a_plan_type()
    {
        // Why: SIG-04 by type for the plan. The roots are what protection holds (inputs, setups, the guard's input, the effector's command); none of them may
        // reach the plan, the holder, the service or the view, in Domain.Planning or Application.Planning.
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
            typeof(Switchboard.Application.Effector.DeviceCommand),
        };

        var offenders = roots
            .SelectMany(r => SignalArchitectureTests.TypeGraph(r).Where(t => t.Namespace is "Switchboard.Domain.Planning" or "Switchboard.Application.Planning").Select(t => $"{r.Name} -> {t.FullName}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_planner_service_takes_only_the_declared_dependencies()
    {
        // Why: the service's reach is its constructor. A new parameter (the control switch, the effector, the guard) would be a new way for the planner to act, and
        // has to be a deliberate change to this test, not a side effect of a refactor.
        var constructor = typeof(PlannerService).GetConstructors().Single();

        var parameters = constructor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Equal(
            [typeof(IPlannerSource), typeof(IPlannerSuspension), typeof(PlanHolder), typeof(TimeProvider), typeof(Microsoft.Extensions.Logging.ILogger<PlannerService>), typeof(Func<PlanInput, CancellationToken, PlanSnapshot>)],
            parameters);
    }

    [Fact]
    public void The_application_planner_code_reads_no_wall_clock_and_names_no_entity()
    {
        // Why: control logic is tested on an injected clock and the core never knows an entity ID: the service gets its time from the TimeProvider it is given
        // (the signal clock), never from DateTime.Now or the system provider, and the planner's inputs reach it as logical names and numbers.
        var files = SourceFiles("src/Switchboard.Application/Planning").ToList();

        var clocks = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"DateTime(Offset)?\.(Now|UtcNow)|TimeProvider\.System|TimeZoneInfo\.Local|Stopwatch")).Select(Path.GetFileName).ToList();
        var entities = files.Where(f => EntityLiteral().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.NotEmpty(files);
        Assert.Empty(clocks);
        Assert.Empty(entities);
    }

    [Fact]
    public void The_pure_planner_has_no_clock_no_io_and_no_randomness()
    {
        // Why: the plan is a pure function of its input (same input, same bytes). Any clock, file, network, environment or random source in the planner, the
        // verifier or their types would make that untrue.
        var files = new[] { "Planner.cs", "PlanContext.cs", "CapacityLedger.cs", "GoalWork.cs", "GoalPlacer.cs", "SpreadPass.cs", "OrderRefiner.cs", "PlanAssembler.cs", "PlanVerifier.cs", "PlanTypes.cs" }.Select(f => RepoPaths.Combine("src", "Switchboard.Domain", "Planning", f)).ToList();

        var offenders = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"DateTime(Offset)?\.(Now|UtcNow)|TimeProvider|Stopwatch|System\.IO|File\.|Directory\.|HttpClient|Environment\.|new Random|Random\.Shared|Guid\.NewGuid|Task\.Delay|Thread\.Sleep")).Select(Path.GetFileName).ToList();

        Assert.Equal(10, files.Count);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scans_do_catch_what_they_look_for()
    {
        // The scans must be able to fail: each pattern matches a line it is meant to forbid.
        Assert.Matches(Commanding(), "private readonly EffectorGate _gate;");
        Assert.Matches(Commanding(), "var port = new HomeAssistantDeviceCommandPort();");
        Assert.Matches(PlanWord(), "var view = PlanView.For(device, now);");
        Assert.Matches(PlanWord(), "var plan = new PlanSnapshot();");
        Assert.DoesNotMatch(PlanWord(), "var view = _plan.For(device, now); // an IPlanView");
        Assert.Matches(PlanWord(), AllowedInDispatch().Replace("var holder = PlanHolder.Current; PlannerDispatchMode m;", string.Empty));
        Assert.Matches(PlanWord(), "private readonly PlanHolder _plans;");
        Assert.DoesNotMatch(PlanWord(), "var plain = airplane.Plane();");
    }

    /// <summary>The plan words the dispatch engine may name: the view it reads through, its entry, the want and the mode.</summary>
    private static Regex AllowedInDispatch() => new(@"\b(IPlanView|PlanEntryView|PlanDecisionInputs|PlanWant|PlannerDispatchMode)\b", RegexOptions.CultureInvariant);

    [Fact]
    public void No_planner_ev_or_economics_source_converts_between_watts_and_kilowatts_by_hand()
    {
        // Why: a hand-written /1000 or *1000 in the planner is a unit change nobody checks (the predecessor planner had sixteen, and a percent once met a kWh
        // epsilon). Watts x duration, kW x duration and price x kWh live in Domain/Units, the signal layer normalises on the way in; nothing else may scale
        // by a thousand or by 3600 in the places that plan, compose an EV goal or settle money.
        var files = SourceFiles("src/Switchboard.Domain/Planning", "src/Switchboard.Domain/Ev", "src/Switchboard.Domain/Economics", "src/Switchboard.Application/Planning", "src/Switchboard.Application/Ev")
            .Append(RepoPaths.Combine("src", "Switchboard.Application", "Dispatch", "PlanReader.cs"))
            .ToList();

        var offenders = files
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Code: StripComments(line))))
            .Where(x => ManualScale().IsMatch(x.Code))
            .Select(x => $"{x.File}:{x.Line}")
            .ToList();

        Assert.True(files.Count >= 30, $"the sources were not found ({files.Count})");
        Assert.Empty(offenders);
    }

    /// <summary>Drops comments only. String text stays, so arithmetic inside an interpolation hole is still seen.</summary>
    private static string StripComments(string line) => LineComment().Replace(line, string.Empty);

    [GeneratedRegex(@"//.*$|/\*.*?\*/", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();

    // 1000, 1_000, 1e3, 0.001, 1e-3, 3600 (optionally with .0 or a d/f/m suffix) next to a * or /.
    [GeneratedRegex(@"[/*]\s*(1000|1_000|1[eE]3|1[eE]\+3|0\.001|1[eE]-3|3600)(\.0+)?[dfmDFM]?(?![0-9._])|(?<![0-9._])(1000|1_000|1[eE]3|1[eE]\+3|0\.001|1[eE]-3|3600)(\.0+)?[dfmDFM]?\s*[/*]", RegexOptions.CultureInvariant)]
    private static partial Regex ManualScale();

    [Fact]
    public void The_manual_scale_check_sees_plain_interpolated_and_alternative_spellings()
    {
        // Why: the check is only worth anything if it cannot be dodged by a string, a 1e3 or a digit separator.
        foreach (var code in new[] { "var k = x / 1000;", "var s = $\"limit {w / 1000 * h:0.###} kWh\";", "var k = x * 1e3;", "var k = x * 0.001;", "var k = x / 1_000;", "var k = 3600 * h;", "var k = x/1000.0;" })
        {
            Assert.True(ManualScale().IsMatch(StripComments(code)), code);
        }

        foreach (var code in new[] { "var k = x / 10000;", "// x / 1000", "/* x * 1000 */ var k = x;", "var k = x * 0.0015;", "var n = 21000;" })
        {
            Assert.False(ManualScale().IsMatch(StripComments(code)), code);
        }
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
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

    [GeneratedRegex(@"IDeviceCommandPort|ICallServiceChannel|EffectorGate|DeviceOverrides|IDeviceOverrideControl|KillSwitch|IControlSwitch|ControlSwitchState|DeviceTargets|HomeAssistantDeviceCommandPort|GuardEngine|DispatchEngine|LockoutClocks|GuardRunner|GuardStatus|IGuardStatusSource|IGuardEnvelopeSource|DispatchStatus", RegexOptions.CultureInvariant)]
    private static partial Regex Commanding();

    [GeneratedRegex(@"\b(Plan(Holder|State|Snapshot|Entry|View|Verifier|Violation|Want|Goal|Input|Inputs|Device|Cost)|Planner\w*|IPlanner\w+|DiagnosticsPlannerSource|SolveReport)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PlanWord();

    [GeneratedRegex(@"\b(sensor|switch|number|input_number|input_boolean|input_datetime|binary_sensor|select|input_select|climate|sun|weather)\.[a-z0-9_]+")]
    private static partial Regex EntityLiteral();
}
