using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// Stage 2 is shadow only, and that is structural: no code path can produce a Home Assistant
/// service call, over the WebSocket or over REST. The only outbound HA types are ping,
/// subscribe_entities, unsubscribe_events and, since Stage 5, three read-only discovery queries
/// (pinned in HomeAssistantCommandAllowListTests), and nothing that persists or publishes the
/// guard's results can reach the HA client at all.
/// </summary>
public sealed partial class Stage2ArchitectureTests
{
    [Fact]
    public void Allow_list_is_exactly_the_live_types_and_the_read_only_queries()
    {
        // Stage 5 adds read-only queries only (setup discovery); nothing that changes state.
        Assert.Equal(
            ["config/device_registry/list", "config/entity_registry/list", "get_states", "ping", "subscribe_entities", "unsubscribe_events"],
            HomeAssistantCommandAllowList.Allowed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_source_mentions_call_service()
    {
        // String literals only (comments may explain what is refused): no source builds a message
        // or a URL that changes state in Home Assistant.
        var offenders = new List<string>();
        foreach (var file in SourceFiles("src"))
        {
            foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
            {
                if (Commanding().IsMatch(literal.Value))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {literal.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void State_reader_only_GETs_entity_states()
    {
        // The REST freshness probe is the only other HTTP path to Home Assistant: GET on
        // api/states/<entity>, never a POST (api/services/... would switch a device).
        var source = File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Infrastructure", "HomeAssistant", "HomeAssistantStateReader.cs"));

        Assert.Contains("HttpMethod.Get", source, StringComparison.Ordinal);
        Assert.Matches(new Regex("\"states/\""), source);
        Assert.DoesNotMatch(new Regex(@"HttpMethod\.(Post|Put|Patch|Delete)|PostAsync|PutAsync|PatchAsync|DeleteAsync"), source);
    }

    [Fact]
    public void Persistence_and_published_entities_cannot_reach_the_home_assistant_client()
    {
        // The decision log, state.json and the MQTT entities see decisions and status only; none of
        // them holds anything from the Home Assistant namespace.
        var types = new[] { typeof(JsonlDecisionLog), typeof(RuntimeStateFile), typeof(DecisionJson), typeof(GuardEntities), typeof(MqttLink) };
        var offenders = types
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType)
                .Concat(t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)))
            .Where(t => t.Namespace?.StartsWith("Switchboard.Infrastructure.HomeAssistant", StringComparison.Ordinal) == true)
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Application_has_no_file_or_stream_in_any_signature()
    {
        // The guard's thread never touches a disk: everything that writes is behind a port
        // (IDecisionRecorder, IRuntimeStateStore) implemented in Infrastructure and drained by
        // writer tasks there.
        var forbidden = new[] { typeof(Stream), typeof(FileSystemInfo), typeof(TextWriter), typeof(TextReader) };
        var offenders = typeof(AppVersion).Assembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => Signature(m).Where(s => forbidden.Any(f => f.IsAssignableFrom(s))).Select(s => $"{t.Name}.{m.Name}: {s.Name}")))
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> Signature(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        _ => [],
    };

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(RepoPaths.Combine(root), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"call_service|fire_event|execute_script|api/services|/services/(?!mqtt)|turn_on|turn_off|set_value", RegexOptions.IgnoreCase)]
    private static partial Regex Commanding();
}
