using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The Limits page parses GET api/dispatch strictly: an enum value it does not know or a renamed key
/// turns the dispatch card "unavailable", which would hide an override or a device that stopped
/// responding exactly when the maintainer reviews the shadow week. These tests hold the SPA's
/// closed lists (web/src/api/dispatch.ts) and its sample payload (dispatch.sample.json, which the
/// SPA tests parse) to what the endpoint really writes.
/// </summary>
public sealed partial class DispatchSpaContractTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly string DispatchContract = RepoPaths.Combine("web", "src", "api", "dispatch.ts");
    private static readonly string DispatchSample = RepoPaths.Combine("web", "src", "api", "dispatch.sample.json");

    public static TheoryData<string, string[]> BackendLists() => new()
    {
        { "SETTLING_STATUSES", Snake<SettlingStatus>() },
        { "HISTORY_KINDS", Snake<HistoryKind>() },
        { "OVERRIDE_SOURCES", Snake<OverrideSource>() },
        { "DISPATCH_INVALID_REASONS", Snake<DispatchInvalidReason>() },
    };

    [Theory]
    [MemberData(nameof(BackendLists))]
    public void Spa_closed_list_matches_the_backend_exactly(string list, string[] backend)
    {
        Assert.Equal(backend.Order(StringComparer.Ordinal), SpaList(list).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Spa_sample_has_exactly_the_keys_the_endpoint_writes()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        // Before the first evaluation (no phase, no device), then a status with every nested object.
        var initial = await GetAsync(factory);
        factory.Services.GetRequiredService<DispatchStatusHolder>().Publish(DispatchEndpointTests.Full());
        var full = await GetAsync(factory);

        using var sample = JsonDocument.Parse(File.ReadAllText(DispatchSample));
        Assert.Equal(Describe(Shapes([initial, full])), Describe(Shapes([sample.RootElement])));
    }

    [Fact]
    public void Spa_override_modes_are_the_ones_the_endpoint_accepts()
    {
        // POST api/devices/{id}/override takes exactly these snake_case values; the select offers them.
        var source = File.ReadAllText(RepoPaths.Combine("web", "src", "api", "decisions.ts"));
        var match = Regex.Match(source, @"export const OVERRIDE_MODES = \[(?<items>[^\]]*)\] as const;");
        Assert.True(match.Success, "OVERRIDE_MODES not found in decisions.ts.");
        Assert.Equal(Snake<OverrideMode>(), QuotedItem().Matches(match.Groups["items"].Value).Select(m => m.Groups[1].Value));
    }

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
                    Collect(property.Value, $"{path}.{property.Name}", shapes);
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
        var source = File.ReadAllText(DispatchContract);
        var match = Regex.Match(source, $@"export const {name} = \[(?<items>[^\]]*)\] as const;");
        Assert.True(match.Success, $"{name} not found in {DispatchContract}.");
        return [.. QuotedItem().Matches(match.Groups["items"].Value).Select(m => m.Groups[1].Value)];
    }

    private static string[] Snake<TEnum>()
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(ApiText.Snake)];

    private static async Task<JsonElement> GetAsync(SwitchboardHostFactory factory)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/dispatch";
            c.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
        });
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex QuotedItem();
}
