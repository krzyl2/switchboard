using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// Stage 5 adds a way to read Home Assistant (discovery) and a way to write switchboard.yaml (the
/// wizard), and neither may become a way to command anything (the maintainer's kickoff rule, SAFE-05).
/// Structurally: the HA allow-list grew only by three read-only queries of exactly {id, type}; no
/// literal in src names a state-changing message; only the discovery link sends queries; the setup
/// code holds no command port, kill switch or override control; and guard, dispatch, the effector
/// gate and the presentation never see the setup code.
/// </summary>
public sealed partial class Stage5ArchitectureTests
{
    private static readonly Type[] Setup = [.. typeof(SetupService).Assembly.GetTypes().Where(t => t.Namespace?.StartsWith("Switchboard.Application.Setup", StringComparison.Ordinal) == true)];

    [Fact]
    public void Allow_list_is_three_live_types_one_service_call_and_three_read_only_queries()
    {
        Assert.Equal(["call_service", "ping", "subscribe_entities", "unsubscribe_events"], HomeAssistantCommandAllowList.Allowed.Except(HomeAssistantCommandAllowList.ReadOnlyQueries).Order(StringComparer.Ordinal));
        Assert.Equal(["config/device_registry/list", "config/entity_registry/list", "get_states"], HomeAssistantCommandAllowList.ReadOnlyQueries.Order(StringComparer.Ordinal));
        foreach (var query in HomeAssistantCommandAllowList.ReadOnlyQueries)
        {
            // Exactly {id, type}: a query carries nothing that could select or change anything.
            HomeAssistantCommandAllowList.EnsurePayloadAllowed(System.Text.Encoding.UTF8.GetBytes($$"""{"id":1,"type":"{{query}}"}"""));
            Assert.Throws<HomeAssistantCommandRefusedException>(() =>
                HomeAssistantCommandAllowList.EnsurePayloadAllowed(System.Text.Encoding.UTF8.GetBytes($$"""{"id":1,"type":"{{query}}","entity_id":"switch.x"}""")));
        }
    }

    [Fact]
    public void No_literal_in_src_names_a_state_changing_message_type()
    {
        // Stage C: the allow-list alone holds call_service and its four services, as whole literals.
        var offenders = new List<string>();
        foreach (var file in SourceFiles("src"))
        {
            var allowList = Path.GetFileName(file) == "HomeAssistantCommandAllowList.cs";
            foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
            {
                if (StateChanging().IsMatch(literal.Value) && !(allowList && ServiceCall().IsMatch(literal.Value)))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {literal.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_discovery_link_calls_QueryAsync()
    {
        var callers = SourceFiles("src")
            .Where(f => QueryCall().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["HomeAssistantClient.cs", "HomeAssistantDiscoveryLink.cs"], callers);
    }

    [Fact]
    public void Setup_never_references_command_port_kill_switch_or_override_control()
    {
        var forbidden = new[] { typeof(IDeviceCommandPort), typeof(IKillSwitchControl), typeof(KillSwitchState), typeof(IDeviceOverrideControl), typeof(DeviceOverrides), typeof(EffectorGate), typeof(GuardEngine), typeof(DispatchEngine) };
        var offenders = Setup
            .SelectMany(t => Signatures(t).Where(s => forbidden.Any(f => f.IsAssignableFrom(s))).Select(s => $"{t.Name}: {s.Name}"))
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

    [Fact]
    public void Setup_is_invisible_to_guard_dispatch_effector_and_presentation()
    {
        var consumers = typeof(AppVersion).Assembly.GetTypes()
            .Where(t => t.Namespace is "Switchboard.Application.Guard" or "Switchboard.Application.Dispatch" or "Switchboard.Application.Effector" or "Switchboard.Application.Presentation")
            .ToList();
        var offenders = consumers
            .SelectMany(t => Signatures(t).Where(s => s.Namespace?.StartsWith("Switchboard.Application.Setup", StringComparison.Ordinal) == true).Select(s => $"{t.Name}: {s.Name}"))
            .ToList();

        Assert.NotEmpty(consumers);
        Assert.Empty(offenders);
        foreach (var file in SourceFiles("src/Switchboard.Application/Guard", "src/Switchboard.Application/Dispatch", "src/Switchboard.Application/Effector", "src/Switchboard.Application/Presentation"))
        {
            Assert.DoesNotContain("Switchboard.Application.Setup", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_evidence_recorder_only_listens_to_the_guard_runner()
    {
        // Like the presentation hub: it reads the step it is handed and the narrow runtime view, and
        // has no way to hand anything back to the control loop. Stage C adds a read-only question to
        // the active self-test (was this transition its own?), which answers a bool and nothing more.
        var parameters = typeof(PhaseEvidenceRecorder).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Equal([typeof(PhaseEvidenceStore), typeof(ISetupRuntimeView), typeof(SetupDefaults), typeof(int), typeof(IActiveTestTransitions)], parameters);
        Assert.Equal([typeof(bool)], typeof(IActiveTestTransitions).GetMethods().Select(m => m.ReturnType));
    }

    private static IEnumerable<string> SourceFiles(params string[] roots) =>
        roots.SelectMany(r => Directory.GetFiles(RepoPaths.Combine(r.Split('/')), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<Type> Signatures(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(m => m switch
            {
                MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
                PropertyInfo property => [property.PropertyType],
                FieldInfo field => [field.FieldType],
                ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
                EventInfo @event => [@event.EventHandlerType!],
                _ => [],
            })
            .SelectMany(Unwrap);

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsByRef || type.IsArray ? type.GetElementType()! : type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"call_service|fire_event|execute_script|subscribe_trigger|render_template|config/[a-z_]+/(?:update|remove|delete|create)|config_entries/|api/services|turn_on|turn_off|set_value|select_option", RegexOptions.IgnoreCase)]
    private static partial Regex StateChanging();

    [GeneratedRegex(@"\bQueryAsync\(")]
    private static partial Regex QueryCall();

    [GeneratedRegex("^\"(call_service|turn_on|turn_off|set_value|select_option)\"$")]
    private static partial Regex ServiceCall();
}
