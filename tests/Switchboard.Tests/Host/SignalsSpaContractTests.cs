using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Host.Composition;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Switchboard.Tests.Host;

/// <summary>
/// The SPA parses GET api/signals strictly: an unknown enum value or a renamed key makes the whole
/// diagnostics page "unavailable", which hides exactly the configuration errors it exists to show.
/// These tests hold the SPA's closed lists (web/src/api/signals.ts) and its sample payload
/// (web/src/api/signals.sample.json, which the SPA tests parse) to what the endpoint really writes.
/// </summary>
public sealed partial class SignalsSpaContractTests
{
    private static readonly string SpaContract = RepoPaths.Combine("web", "src", "api", "signals.ts");
    private static readonly string SpaSample = RepoPaths.Combine("web", "src", "api", "signals.sample.json");
    private static readonly SignalSourceSelection Live = new(SignalSourceKind.HomeAssistant, null, 1);

    public static TheoryData<string, string[]> BackendLists() => new()
    {
        { "SOURCE_KINDS", Snake<SignalSourceKind>() },
        { "SOURCE_STATUSES", Snake<SignalSourceStatus>() },
        { "CONFIGURATION_STATES", Snake<ConfigurationStatus>() },
        { "TOPOLOGIES", Snake<Topology>() },
        { "PROTECTION_INVALID_REASONS", Snake<ProtectionInvalidReason>() },
        { "FLOW_DIRECTIONS", Snake<FlowDirection>() },
        { "DIRECTION_SOURCES", Snake<DirectionSource>() },
        { "SIGNAL_KINDS", Snake<SignalKind>() },
        { "SIGNAL_ROLES", Snake<SignalRole>() },
        { "SIGNAL_STATES", Snake<SignalState>() },
        { "SIGNAL_DETAILS", Snake<SignalDetail>() },

        // The endpoint writes these two in their configuration spelling, not snake_case.
        { "PHASES", Enum.GetNames<Phase>() },
        { "CHARACTERISTICS", [.. Enum.GetValues<BreakerCharacteristic>().Select(c => c == BreakerCharacteristic.Unknown ? "unknown" : c.ToString())] },
        {
            "ISSUE_CODES",
            [.. typeof(ConfigurationIssueCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue()!)]
        },
    };

    [Theory]
    [MemberData(nameof(BackendLists))]
    public void Spa_closed_list_matches_the_backend_exactly(string list, string[] backend)
    {
        Assert.Equal(backend.Order(StringComparer.Ordinal), SpaList(list).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Spa_sample_payload_has_exactly_the_keys_the_endpoint_writes()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        // The options TypedResults.Json serializes the endpoint's answer with.
        var json = factory.Services.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;

        var endpoint = Shapes(EndpointResponses().Select(r => JsonSerializer.SerializeToElement(r, json)));
        using var sample = JsonDocument.Parse(File.ReadAllText(SpaSample));

        Assert.Equal(Describe(endpoint), Describe(Shapes([sample.RootElement])));
    }

    private static IEnumerable<SignalsResponse> EndpointResponses()
    {
        // A running three-phase meter: every nested object (connection, reserve, raw, direction)
        // is filled, plus a run-time warning so issue objects appear.
        var harness = new StoreHarness(TestConfigurations.ThreePhase(inverterCurrents: false));
        harness.Push("sensor.meter_power_a", "2070");
        harness.Push("sensor.meter_current_a", "9.0");
        var configuration = harness.Configuration.Configuration!;
        var running = new SignalDiagnostics(
            harness.Snapshot(),
            harness.Configuration,
            configuration.Signals.ToDictionary(s => s.Name, s => s.Entity).ToImmutableDictionary(),
            [new ConfigurationIssue(ConfigurationIssueCodes.MaxAgeShortForProbe, "signals.grid_current_l1.max_age", null, null, "Too short.")]);
        yield return SignalsEndpoint.Build(running, harness.Now, Live, TestConfigurations.Path);

        // A broken document: errors with line and column.
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        var broken = TestConfigurations.Parse(TestConfigurations.ThreePhase().Replace("max_age: 90s", "max_agee: 90s", StringComparison.Ordinal));
        store.ApplyConfiguration(broken, StoreHarness.Start);
        Assert.NotEmpty(broken.Errors);
        yield return SignalsEndpoint.Build(new SignalDiagnostics(store.Snapshot(StoreHarness.Start), broken, [], []), StoreHarness.Start, Live, TestConfigurations.Path);
    }

    /// <summary>
    /// Every object in the documents by its path (array items as <c>[]</c>), with its keys in order.
    /// Errors and warnings are the same issue object, so both count as <c>configuration.issues[]</c>.
    /// </summary>
    private static SortedDictionary<string, string[]> Shapes(IEnumerable<JsonElement> documents)
    {
        var shapes = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            Collect(document, "$", shapes);
        }

        return shapes;
    }

    private static void Collect(JsonElement element, string path, SortedDictionary<string, string[]> shapes)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = element.EnumerateObject().Select(p => p.Name).ToArray();
                if (shapes.TryGetValue(path, out var seen))
                {
                    Assert.True(seen.SequenceEqual(keys), $"{path} has two different shapes: {string.Join(",", seen)} / {string.Join(",", keys)}");
                }

                shapes[path] = keys;
                foreach (var property in element.EnumerateObject())
                {
                    var name = path == "$.configuration" && property.Name is "errors" or "warnings" ? "issues" : property.Name;
                    Collect(property.Value, $"{path}.{name}", shapes);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, path + "[]", shapes);
                }

                break;
        }
    }

    private static List<string> Describe(SortedDictionary<string, string[]> shapes) =>
        [.. shapes.Select(s => $"{s.Key}: {string.Join(",", s.Value)}")];

    private static string[] SpaList(string name)
    {
        var source = File.ReadAllText(SpaContract);
        var match = Regex.Match(source, $@"export const {name} = \[(?<items>[^\]]*)\] as const;");
        Assert.True(match.Success, $"{name} not found in {SpaContract}.");
        return [.. QuotedItem().Matches(match.Groups["items"].Value).Select(m => m.Groups[1].Value)];
    }

    private static string[] Snake<TEnum>()
        where TEnum : struct, Enum =>
        [.. Enum.GetNames<TEnum>().Select(JsonNamingPolicy.SnakeCaseLower.ConvertName)];

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex QuotedItem();
}
