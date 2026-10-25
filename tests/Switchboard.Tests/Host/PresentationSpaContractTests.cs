using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The runtime UI parses GET api/power, api/statistics, api/excursions and api/devices strictly: an
/// enum value it does not know or a renamed key turns a screen "unavailable", which would blank the
/// dashboard exactly when a phase is over its reserve. These tests hold the SPA's closed lists
/// (web/src/api/*.ts) and its sample payloads (*.sample.json, which the SPA tests parse) to what
/// the endpoints write. The samples come from the real serializer over the synthetic morning
/// (SWITCHBOARD_WRITE_SPA_SAMPLES=1 regenerates them): power at 09:21, while the car charges and a
/// kettle runs on L1.
/// </summary>
public sealed class PresentationSpaContractTests
{
    private static readonly string Power = RepoPaths.Combine("web", "src", "api", "power.ts");
    private static readonly string Statistics = RepoPaths.Combine("web", "src", "api", "statistics.ts");
    private static readonly string Excursions = RepoPaths.Combine("web", "src", "api", "excursions.ts");
    private static readonly string Devices = RepoPaths.Combine("web", "src", "api", "devices.ts");
    private static readonly string Decisions = RepoPaths.Combine("web", "src", "api", "decisions.ts");
    private static readonly string Dispatch = RepoPaths.Combine("web", "src", "api", "dispatch.ts");

    public static TheoryData<string, string, string[]> BackendLists() => new()
    {
        { Power, "POWER_UNKNOWN_REASONS", Snake<PowerUnknownReason>() },
        { Power, "GENERATION_NOTES", Snake<GenerationNote>() },
        { Power, "SEGMENT_KINDS", Snake<SegmentKind>() },
        { Power, "POWER_SOURCES", Snake<PowerSource>() },
        { Power, "REACTION_TIMINGS", Snake<ReactionTiming>() },
        { Dispatch, "DISPATCH_INVALID_REASONS", Snake<DispatchInvalidReason>() },
        { Decisions, "DECISION_KINDS", Snake<DecisionKind>() },
        { Devices, "CONFIGURATION_STATES", Snake<ConfigurationStatus>() },
        { Devices, "STATE_REPORTS", Snake<StateFreshness>() },
    };

    [Theory]
    [MemberData(nameof(BackendLists))]
    public void Spa_closed_list_matches_the_backend_exactly(string file, string list, string[] backend)
    {
        Assert.Equal(backend.Order(StringComparer.Ordinal), SpaContract.List(file, list).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Spa_samples_have_exactly_the_keys_the_endpoints_write()
    {
        using var root = new TempWebRoot();

        // The endpoints' "now" is the signal clock, on the replay's data time as in development
        // replay, so a sample's at, ages and last hour agree with its data.
        var clock = new SteppableTime(PresentationDayRun.At(6, 0));
        using var factory = new SwitchboardHostFactory(
            root.Path,
            new Dictionary<string, string?>
            {
                // Compact samples: the shapes are what is compared, not the number of samples.
                ["Switchboard:Statistics:ExcursionMaxSamples"] = "24",
                ["Switchboard:Statistics:ExcursionMaxDecisions"] = "8",
            },
            signalTime: clock);
        var initial = await Endpoints(factory);

        var data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", PresentationDayRun.File));
        var harness = new GuardReplayHarness(data, TestConfigurations.Example);
        var hub = factory.Services.GetRequiredService<PresentationHub>();
        harness.Runner.Stepped += (_, step) => hub.Observe(step);
        var poweredAt = PresentationDayRun.At(9, 21) + TimeSpan.FromSeconds(2);
        var endAt = PresentationDayRun.At(9, 40) + TimeSpan.FromSeconds(2);
        harness.RunUntil(PresentationDayRun.At(9, 21));
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(harness.Replay.Current);
        clock.Advance(poweredAt - clock.Now);
        var power = await GetAsync(factory, "/api/power");
        harness.RunUntil(PresentationDayRun.At(9, 40));
        clock.Advance(endAt - clock.Now);
        var running = await Endpoints(factory, "?from=2026-10-22T09:20:00Z&to=2026-10-22T09:30:00Z");

        Assert.NotEmpty(power.GetProperty("banners").EnumerateArray());
        Assert.NotEqual(0, running.Statistics.GetProperty("lastHour").GetProperty("buckets").GetInt32());
        Compare(RepoPaths.Combine("web", "src", "api", "power.sample.json"), initial.Power, power, poweredAt);
        Compare(RepoPaths.Combine("web", "src", "api", "statistics.sample.json"), initial.Statistics, running.Statistics, endAt);
        Compare(RepoPaths.Combine("web", "src", "api", "excursions.sample.json"), initial.Excursions, running.Excursions, endAt);
        Compare(RepoPaths.Combine("web", "src", "api", "devices.sample.json"), initial.Devices, running.Devices, endAt);
    }

    private static void Compare(string samplePath, JsonElement initial, JsonElement full, DateTimeOffset at)
    {
        var sample = SpaContract.Sample(samplePath, full, at);
        Assert.Equal(SpaContract.Describe([initial, full]), SpaContract.Describe([sample]));
    }

    private static async Task<(JsonElement Power, JsonElement Statistics, JsonElement Excursions, JsonElement Devices)> Endpoints(SwitchboardHostFactory factory, string statisticsQuery = "") => (
        await GetAsync(factory, "/api/power"),
        await GetAsync(factory, "/api/statistics" + statisticsQuery),
        await GetAsync(factory, "/api/excursions"),
        await GetAsync(factory, "/api/devices"));

    private static async Task<JsonElement> GetAsync(SwitchboardHostFactory factory, string pathAndQuery)
    {
        var context = await PresentationEndpointTests.SendAsync(factory, "GET", pathAndQuery);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }

    private static string[] Snake<TEnum>()
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(ApiText.Snake)];
}
