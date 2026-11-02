using System.Text.RegularExpressions;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.ArchitectureProbe;

namespace Switchboard.Tests;

/// <summary>
/// Banned symbols on the Home Assistant wire: no code path can produce a service call or any
/// state-changing message except the one gated call_service on a declared actuator. The outbound
/// allow-list is exactly the live types, the read-only discovery queries (exactly {id, type}) and
/// call_service; no source literal names a state-changing message type outside the allow-list; the
/// REST state reader only GETs; only the discovery link sends queries; Application never names a
/// service.
/// </summary>
public sealed partial class HomeAssistantSurfaceArchitectureTests
{
    [Fact]
    public void Allow_list_is_exactly_the_live_types_the_read_only_queries_and_one_service_call()
    {
        // Stage 5 adds read-only queries only (setup discovery); Stage C the one state-changing type,
        // call_service on a declared actuator (ControlArchitectureTests).
        Assert.Equal(
            ["call_service", "config/device_registry/list", "config/entity_registry/list", "get_states", "ping", "subscribe_entities", "unsubscribe_events"],
            HomeAssistantCommandAllowList.Allowed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_the_allow_list_names_a_service_call()
    {
        // String literals only (comments may explain what is refused): since Stage C the allow-list
        // holds the one service call and its four services; no other source builds a message or a
        // URL that changes state in Home Assistant, and nothing anywhere names fire_event,
        // execute_script or the REST services API.
        var offenders = new List<string>();
        foreach (var file in SourceFiles("src"))
        {
            var allowList = Path.GetFileName(file) == "HomeAssistantCommandAllowList.cs";
            foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
            {
                if (Commanding().IsMatch(literal.Value) && !(allowList && ServiceCall().IsMatch(literal.Value)))
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
    public void Allow_list_and_sources_stay_free_of_service_calls()
    {
        // The allow-list grew by the read-only discovery queries of Stage 5 and the one service call of
        // Stage C; Application never names a service (the allow-list maps ActuatorService to wire names).
        Assert.Equal(["call_service", "config/device_registry/list", "config/entity_registry/list", "get_states", "ping", "subscribe_entities", "unsubscribe_events"], HomeAssistantCommandAllowList.Allowed.Order(StringComparer.Ordinal));
        foreach (var file in SourceFiles("src/Switchboard.Application"))
        {
            Assert.DoesNotMatch(ServiceLiteral(), File.ReadAllText(file));
        }
    }

    [Fact]
    public void Allow_list_holds_only_the_live_types_and_the_read_only_queries()
    {
        // The runtime UI added nothing; Stage 5 added read-only discovery queries, Stage C call_service.
        Assert.Equal(["call_service", "config/device_registry/list", "config/entity_registry/list", "get_states", "ping", "subscribe_entities", "unsubscribe_events"], HomeAssistantCommandAllowList.Allowed.Order(StringComparer.Ordinal));
    }

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

    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"call_service|fire_event|execute_script|api/services|/services/(?!mqtt)|turn_on|turn_off|set_value|select_option", RegexOptions.IgnoreCase)]
    private static partial Regex Commanding();

    [GeneratedRegex("^\"(call_service|turn_on|turn_off|set_value|select_option)\"$")]
    private static partial Regex ServiceCall();

    [GeneratedRegex("\"[^\"\\n]*(call_service|api/services|turn_on|turn_off|set_value)[^\"\\n]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceLiteral();

    [GeneratedRegex(@"call_service|fire_event|execute_script|subscribe_trigger|render_template|config/[a-z_]+/(?:update|remove|delete|create)|config_entries/|api/services|turn_on|turn_off|set_value|select_option", RegexOptions.IgnoreCase)]
    private static partial Regex StateChanging();

    [GeneratedRegex(@"\bQueryAsync\(")]
    private static partial Regex QueryCall();
}
