using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The Limits page parses GET api/guard and the decisions strictly: an enum value it does not know
/// or a renamed key makes the whole page "unavailable", which would hide a safe state or a shed
/// exactly when it matters. These tests hold the SPA's closed lists (web/src/api/guard.ts and
/// decisions.ts) and its sample payloads (guard.sample.json, decisions.sample.json, which the SPA
/// tests parse) to what the endpoints really write.
/// </summary>
public sealed partial class GuardSpaContractTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly string GuardContract = RepoPaths.Combine("web", "src", "api", "guard.ts");
    private static readonly string DecisionsContract = RepoPaths.Combine("web", "src", "api", "decisions.ts");
    private static readonly string GuardSample = RepoPaths.Combine("web", "src", "api", "guard.sample.json");
    private static readonly string DecisionsSample = RepoPaths.Combine("web", "src", "api", "decisions.sample.json");

    public static TheoryData<string, string, string[]> BackendLists() => new()
    {
        { GuardContract, "SAFE_STATE_SCOPES", Snake<SafeStateScope>() },
        { GuardContract, "BUDGET_TRENDS", Snake<BudgetTrend>() },
        { GuardContract, "PHASE_GUARD_STATES", Snake<PhaseGuardState>() },
        { GuardContract, "DEVICE_TYPES", Snake<DeviceType>() },
        { GuardContract, "DEVICE_GUARD_STATES", Snake<DeviceGuardState>() },
        { GuardContract, "GUARD_TARGETS", Snake<GuardTarget>() },
        { GuardContract, "STATE_FILE_STATUSES", Snake<StateFileStatus>() },
        { DecisionsContract, "REASON_CODES", [.. ReasonCodes.All.Select(ReasonCodes.Name)] },
        { DecisionsContract, "DECISION_SCOPES", Snake<DecisionScope>() },
        { DecisionsContract, "DECISION_ACTIONS", Snake<DecisionAction>() },
        { DecisionsContract, "OPERATING_MODES", Snake<OperatingMode>() },
        { DecisionsContract, "MODE_NOTES", Snake<ModeNote>() },
        { DecisionsContract, "KILL_SWITCH_SOURCES", Snake<KillSwitchSource>() },
        { DecisionsContract, "MODE_CONDITIONS", Snake<ModeCondition>() },
        { DecisionsContract, "CONTROL_SWITCH_SOURCES", Snake<ControlSwitchSource>() },
        { DecisionsContract, "SAFE_STATE_CAUSES", Snake<SafeStateCause>() },
        { DecisionsContract, "TIER_REACTIONS", Snake<TierReaction>() },
        { DecisionsContract, "DEVICE_STATE_INVALID_REASONS", Snake<DeviceStateInvalidReason>() },
        { DecisionsContract, "RELAY_STATES", [ApiText.Applied(new RelayApplied(true))!, ApiText.Applied(new RelayApplied(false))!] },
        { DecisionsContract, "DECISION_ORIGINS", Snake<DecisionOrigin>() },
        { DecisionsContract, "SETTLING_STATES", Snake<SettlingState>() },
        { DecisionsContract, "VERDICTS", Snake<Verdict>() },
        { DecisionsContract, "MEASURE_UNITS", Snake<MeasureUnit>() },
        { DecisionsContract, "OVERRIDE_MODES", Snake<OverrideMode>() },
    };

    [Theory]
    [MemberData(nameof(BackendLists))]
    public void Spa_closed_list_matches_the_backend_exactly(string file, string list, string[] backend)
    {
        Assert.Equal(backend.Order(StringComparer.Ordinal), SpaList(file, list).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Spa_reason_codes_are_list_version_4_in_the_order_of_04()
    {
        // The SPA renders one sentence per code; its list is the closed list of 04 §6, version 4.
        Assert.Equal(4, ReasonCodes.ListVersion);
        Assert.Equal(ReasonCodes.All.Select(ReasonCodes.Name), SpaList(DecisionsContract, "REASON_CODES"));
    }

    [Fact]
    public async Task Spa_samples_have_exactly_the_keys_the_endpoints_write()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        // Before the first evaluation: no breaker, no phase, no device.
        var initial = await GetAsync(factory, "/api/guard");
        Publish(factory);
        var running = await GetAsync(factory, "/api/guard");
        var decisions = await GetAsync(factory, "/api/decisions?limit=500");

        using var guardSample = JsonDocument.Parse(File.ReadAllText(GuardSample));
        using var decisionsSample = JsonDocument.Parse(File.ReadAllText(DecisionsSample));
        Assert.Equal(Describe(Shapes([initial, running])), Describe(Shapes([guardSample.RootElement])));
        Assert.Equal(Describe(Shapes([decisions])), Describe(Shapes([decisionsSample.RootElement])));
    }

    /// <summary>
    /// The reference installation with an overload on L1 and the kill switch engaged, through the
    /// real store, guard and runtime state, so every nested object of both answers is written:
    /// the breaker, last known states, and decisions with phase, device, mode and kill switch inputs;
    /// plus one dispatch decision with every nested part of format 2 (target, headroom on a main
    /// phase and a circuit group, contribution, in-flight change, verification).
    /// </summary>
    private static void Publish(SwitchboardHostFactory factory)
    {
        var harness = new GuardRunnerHarness(TestDevices.Document());
        var state = factory.Services.GetRequiredService<RuntimeStateCoordinator>();
        harness.Runner.Stepped += (_, step) => state.OnStepped(step);
        harness.PushReference(l1: 8);
        harness.Runner.Step();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        harness.PushReference(l1: 33);
        harness.Runner.Step();
        harness.KillSwitch.Set(true, KillSwitchSource.Api);
        harness.Runner.Step();
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(harness.Holder.Current);
        factory.Services.GetRequiredService<GuardStatusHolder>().Publish(harness.Status.Current);
        factory.Services.GetRequiredService<IDecisionRecorder>().Record([.. harness.Recorder.Decisions, DispatchDecision(harness.Now)]);
    }

    private static Decision DispatchDecision(DateTimeOffset at)
    {
        var carport = GroupName.Parse("carport");
        var inputs = new DispatchDecisionInputs(
            [new ConstraintHeadroom(ConstraintKey.Main(Phase.L1), new Amperes(9.4), new Amperes(9.4)), new ConstraintHeadroom(new ConstraintKey(carport, Phase.L1), new Amperes(4), null)],
            new Amperes(1.3),
            new Amperes(-0.87),
            4,
            ImmutableDictionary<Phase, Amperes>.Empty.Add(Phase.L1, Amperes.Zero),
            ImmutableDictionary<ConstraintKey, Amperes>.Empty.Add(ConstraintKey.Main(Phase.L1), Amperes.Zero).Add(new ConstraintKey(carport, Phase.L1), Amperes.Zero),
            SettlingState.Closed,
            new VerificationInputs(1500, 12, MeasureUnit.Watts, Verdict.Mismatch),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(42),
            at,
            OverrideMode.Auto)
        {
            Balance = new BalanceDecisionInputs(240.5, 3100, 1650, 410.25, 150, 525, 1320, false),
        };
        return new Decision(
            "contract-000000001", at, DecisionScope.Device, DeviceName.Parse("heater_l1"), null, Phase.L1, DecisionAction.MarkedUnresponsive, ReasonCode.DeviceUnresponsive, null,
            OperatingMode.Shadow, false, null, DecisionInputs.None with { Dispatch = inputs }, DecisionOrigin.Dispatch, carport, DeviceLevel.Relay(false));
    }

    /// <summary>Every object in the documents by its path (array items as <c>[]</c>), with its keys in order.</summary>
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

    private static string[] SpaList(string file, string name)
    {
        var source = File.ReadAllText(file);
        var match = Regex.Match(source, $@"export const {name} = \[(?<items>[^\]]*)\] as const;");
        Assert.True(match.Success, $"{name} not found in {file}.");
        return [.. QuotedItem().Matches(match.Groups["items"].Value).Select(m => m.Groups[1].Value)];
    }

    private static string[] Snake<TEnum>()
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(ApiText.Snake)];

    private static async Task<JsonElement> GetAsync(SwitchboardHostFactory factory, string pathAndQuery)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            var query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
            c.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
            c.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery[query..]);
            c.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
        });
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex QuotedItem();
}
