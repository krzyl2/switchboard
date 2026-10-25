using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Application.Modes;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The setup wizard's API through the real host: every POST requires application/json (a cross-site
/// form cannot send it without a CORS preflight the add-on never answers), discovery says plainly
/// when Home Assistant cannot be reached, readouts compute ages on the signal clock, the mode can be
/// chosen at run time but never active in this version, and the decision history pages the retained
/// files. In development replay (SWITCHBOARD_DEV_REPLAY + SWITCHBOARD_DEV_DISCOVERY) discovery and
/// readouts come from the committed fixtures.
/// </summary>
public sealed class SetupEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    internal static readonly string ReplayCsv = RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-dispatch-day.csv");
    internal static readonly string DiscoveryFixtures = RepoPaths.Combine("tests", "fixtures", "discovery", "synthetic-house");

    [Theory]
    [InlineData("POST", "/api/setup/validate", null)]
    [InlineData("POST", "/api/setup/validate", "text/plain")]
    [InlineData("POST", "/api/setup/save", null)]
    [InlineData("POST", "/api/setup/save", "application/x-www-form-urlencoded")]
    [InlineData("POST", "/api/setup/self-test", "text/plain")]
    [InlineData("POST", "/api/control", "text/plain")]
    [InlineData("POST", "/api/control", "multipart/form-data")]
    [InlineData("POST", "/api/control/confirm", "text/plain")]
    public async Task Setup_posts_and_delete_require_json_or_preflight(string method, string path, string? contentType)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, method, path, contentType, """{"draft":{},"allowed":true,"confirm":true}""");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(File.Exists(Path.Combine(root.Path, "no-switchboard.yaml")));
        Assert.False(factory.Services.GetRequiredService<IControlSwitchView>().Status.Allowed);
    }

    [Fact]
    public async Task Unknown_keys_in_a_draft_are_refused()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "POST", "/api/setup/validate", "application/json", """{"draft":{"connection":{"topolgy":"three_phase"}}}""");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task Discovery_without_home_assistant_is_503_with_its_code()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "GET", "/api/setup/discovery");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("home_assistant_unavailable", Json(context).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?entity=Sensor%20A")]
    [InlineData("?entity=sensor.a&entity=sensor.b&entity=sensor.c&entity=sensor.d")]
    public async Task Readouts_ask_for_a_bounded_list_of_valid_entities(string query)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["Switchboard:Setup:MaxReadoutEntities"] = "3", ["Switchboard:Setup:MaxRetainedReadouts"] = "3" });

        var context = await SendAsync(factory, "GET", "/api/setup/readouts" + query);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task Replay_readout_age_uses_the_signal_clock()
    {
        // In development replay the readouts are the recording's at the signal clock, never the host's wall time.
        using var root = new TempWebRoot();
        var clock = new SteppableTime(At(7, 41, 3));
        using var factory = ReplayHost(root, clock);

        var body = Json(await SendAsync(factory, "GET", "/api/setup/readouts?entity=sensor.solaredge_m1_ac_power_b&entity=switch.grzalka_1"));

        Assert.Equal(At(7, 41, 3), body.GetProperty("at").GetDateTimeOffset());
        var power = body.GetProperty("items")[0];
        Assert.Equal(("found", "W"), (power.GetProperty("status").GetString(), power.GetProperty("unit").GetString()));
        Assert.InRange(power.GetProperty("ageS").GetDouble(), 0, 10);
        Assert.Equal(10, power.GetProperty("refresh").GetProperty("intervalS").GetDouble());
        var relay = body.GetProperty("items")[1];
        Assert.Equal("on", relay.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Dev_discovery_flags_derived_sensors_and_unverifiable_ones()
    {
        using var root = new TempWebRoot();
        using var factory = ReplayHost(root, new SteppableTime(At(7, 0)));

        var body = Json(await SendAsync(factory, "GET", "/api/setup/discovery"));

        var entities = body.GetProperty("entities").EnumerateArray().ToDictionary(e => e.GetProperty("entityId").GetString()!);
        Assert.Equal("template", entities["sensor.usage"].GetProperty("derived").GetProperty("platform").GetString());
        Assert.Equal("powercalc", entities["sensor.kettle_power"].GetProperty("derived").GetProperty("platform").GetString());
        Assert.False(entities["sensor.house_load_yaml"].GetProperty("inRegistry").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entities["sensor.solaredge_m1_ac_current_a"].GetProperty("derived").ValueKind);
        Assert.True(body.GetProperty("registryAvailable").GetBoolean());
    }

    [Fact]
    public void Dev_discovery_is_refused_without_replay()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_DEV_DISCOVERY"] = DiscoveryFixtures });

        Assert.ThrowsAny<Exception>(() => factory.Server);
    }

    [Fact]
    public void Dev_discovery_is_refused_as_an_addon()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?>
        {
            ["SUPERVISOR_TOKEN"] = "supervisor",
            ["SWITCHBOARD_DEV_DISCOVERY"] = DiscoveryFixtures,
        });

        Assert.ThrowsAny<Exception>(() => factory.Server);
    }

    [Fact]
    public async Task Clean_install_is_saved_through_the_api()
    {
        using var root = new TempWebRoot();
        using var factory = ReplayHost(root, new SteppableTime(At(7, 0)), example: false);
        var draft = Json(await SendAsync(factory, "GET", "/api/setup/draft"));
        Assert.Equal(("missing", JsonValueKind.Null), (draft.GetProperty("fileState").GetString(), draft.GetProperty("revision").ValueKind));

        var context = await SendAsync(factory, "POST", "/api/setup/save", "application/json", JsonSerializer.Serialize(new { draft = SetupFixtures.MaintainerDraft(), baseRevision = (string?)null }, Web));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var saved = Json(context);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("backup").ValueKind);
        Assert.Equal(Switchboard.Application.Configuration.ConfigurationStatus.Valid, TestConfigurations.Parse(File.ReadAllText(ConfigPath(root))).Status);
    }

    [Fact]
    public async Task Save_with_a_stale_revision_is_409()
    {
        using var root = new TempWebRoot();
        using var factory = ReplayHost(root, new SteppableTime(At(7, 0)));

        var context = await SendAsync(factory, "POST", "/api/setup/save", "application/json", JsonSerializer.Serialize(new { draft = SetupFixtures.MaintainerDraft(), baseRevision = "0000" }, Web));

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("revision_conflict", Json(context).GetProperty("code").GetString());
        Assert.Equal(TestConfigurations.Example, File.ReadAllText(ConfigPath(root)));
    }

    [Fact]
    public async Task Save_with_errors_is_422_naming_the_field()
    {
        using var root = new TempWebRoot();
        using var factory = ReplayHost(root, new SteppableTime(At(7, 0)), example: false);
        var draft = SetupFixtures.MaintainerDraft(acknowledged: false);

        var context = await SendAsync(factory, "POST", "/api/setup/save", "application/json", JsonSerializer.Serialize(new { draft, baseRevision = (string?)null }, Web));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, context.Response.StatusCode);
        Assert.Contains(Json(context).GetProperty("issues").EnumerateArray(), i => i.GetProperty("field").GetString() == "acknowledgements.notProtective");
        Assert.False(File.Exists(ConfigPath(root)));
    }

    [Fact]
    public async Task Decision_history_pages_the_retained_files()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        WriteDecisions(root);

        var first = Json(await SendAsync(factory, "GET", "/api/decisions/history?limit=2"));
        var next = first.GetProperty("next").GetString();
        var second = Json(await SendAsync(factory, "GET", $"/api/decisions/history?limit=2&cursor={next}"));
        var bad = await SendAsync(factory, "GET", "/api/decisions/history?cursor=nope");

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(StatusCodes.Status400BadRequest, bad.Response.StatusCode);
    }

    [Fact]
    public async Task Self_test_watches_a_declared_device_and_refuses_what_it_cannot_judge()
    {
        using var root = new TempWebRoot();
        var clock = new SteppableTime(At(7, 39));
        using var factory = ReplayHost(root, clock);
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(TestConfigurations.Example)).Draft;

        var charger = await SendAsync(factory, "POST", "/api/setup/self-test", "application/json", JsonSerializer.Serialize(new { draft, device = new { index = 3 } }, Web));
        var started = await SendAsync(factory, "POST", "/api/setup/self-test", "application/json", JsonSerializer.Serialize(new { draft, device = new { index = 1 } }, Web));
        var busy = await SendAsync(factory, "POST", "/api/setup/self-test", "application/json", JsonSerializer.Serialize(new { draft, device = new { index = 0 } }, Web));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, charger.Response.StatusCode);
        Assert.Equal(StatusCodes.Status202Accepted, started.Response.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, busy.Response.StatusCode);
        var id = Json(started).GetProperty("id").GetString();
        // Node-RED switches grzalka_1 on at 07:40:05 (one switching: unconfirmed) and off at 07:46:05.
        clock.Advance(At(7, 41, 10) - clock.GetUtcNow());
        var afterOn = Json(await SendAsync(factory, "GET", $"/api/setup/self-test/{id}"));
        Assert.Equal(("watching", "unconfirmed"), (afterOn.GetProperty("status").GetString(), afterOn.GetProperty("verdict").GetProperty("reason").GetString()));
        clock.Advance(At(7, 47, 10) - clock.GetUtcNow());
        var view = Json(await SendAsync(factory, "GET", $"/api/setup/self-test/{id}"));
        Assert.Equal(("finished", "confirmed"), (view.GetProperty("status").GetString(), view.GetProperty("verdict").GetProperty("verdict").GetString()));
        Assert.Equal(StatusCodes.Status204NoContent, (await SendAsync(factory, "DELETE", $"/api/setup/self-test/{id}")).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(factory, "DELETE", "/api/setup/self-test/0123")).Response.StatusCode);
    }

    internal static readonly JsonSerializerOptions Web = SetupEndpoint.Answers;

    internal static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 22, hour, minute, second, TimeSpan.Zero);

    internal static string ConfigPath(TempWebRoot root) => Path.Combine(root.Path, "switchboard.yaml");

    /// <summary>Development replay of the synthetic morning with the discovery fixtures, the signal clock at <paramref name="clock"/>.</summary>
    internal static SwitchboardHostFactory ReplayHost(TempWebRoot root, TimeProvider clock, bool example = true)
    {
        if (example)
        {
            File.WriteAllText(ConfigPath(root), TestConfigurations.Example);
        }

        var factory = new SwitchboardHostFactory(
            root.Path,
            new Dictionary<string, string?>
            {
                ["SWITCHBOARD_DEV_REPLAY"] = ReplayCsv,
                ["SWITCHBOARD_DEV_DISCOVERY"] = DiscoveryFixtures,
                ["SWITCHBOARD_CONFIG_PATH"] = ConfigPath(root),
            },
            signalTime: clock);
        _ = factory.Server;
        return factory;
    }

    internal static void WriteDecisions(TempWebRoot root)
    {
        var directory = Path.Combine(root.Path, "data", JsonlDecisionLog.DirectoryName);
        Directory.CreateDirectory(directory);
        foreach (var at in new[] { At(7, 40), At(7, 41), At(7, 42) })
        {
            File.AppendAllText(Path.Combine(directory, JsonlDecisionLog.FileNameFor(at)), DecisionJson.Serialize(TestDecisions.Shed(at)) + "\n");
        }
    }

    internal static Task<HttpContext> SendAsync(SwitchboardHostFactory factory, string method, string pathAndQuery, string? contentType = null, string? body = null) =>
        factory.Server.SendAsync(context =>
        {
            var split = pathAndQuery.Split('?', 2);
            context.Request.Method = method;
            context.Request.Path = split[0];
            context.Request.QueryString = split.Length > 1 ? new QueryString("?" + split[1]) : QueryString.Empty;
            context.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
            if (contentType is not null)
            {
                context.Request.ContentType = contentType;
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
            }
        });

    internal static JsonElement Json(HttpContext context)
    {
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }
}

/// <summary>
/// The wizard's SPA parses every setup answer strictly (web/src/api/setup.ts, mode.ts,
/// decisionHistory.ts): its committed samples (*.sample.json, parsed by the SPA tests) must have
/// exactly the keys the endpoints write, and its closed lists the backend's values. The samples come
/// from the real serializer in development replay of the synthetic morning
/// (SWITCHBOARD_WRITE_SPA_SAMPLES=1 regenerates them).
/// </summary>
public sealed class SetupSpaContractTests
{
    private static string Sample(string name) => RepoPaths.Combine("web", "src", "api", name);

    public static TheoryData<string, string, string[]> BackendLists() => new()
    {
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "SETUP_ISSUE_CODES", [.. SetupIssueCodes.All] },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "READOUT_STATUSES", Snake<Switchboard.Application.Setup.Readouts.ReadoutStatus>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "SELF_TEST_STATUSES", Snake<SelfTestStatus>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "PHASE_STEP_VERDICTS", Snake<PhaseStepVerdict>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "INCONCLUSIVE_REASONS", Snake<InconclusiveReason>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "SETUP_SEVERITIES", ["error", "warning"] },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "OBSERVATION_SOURCES", Snake<ObservationSource>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "ACTIVE_TEST_REFUSALS", Snake<ActiveTestRefusalCode>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "ACTIVE_TEST_STATES", Snake<ActiveTestState>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "ACTIVE_TEST_PHASES", Snake<ActiveTestPhase>() },
        { RepoPaths.Combine("web", "src", "api", "setup.ts"), "ACTIVE_TEST_ABORT_REASONS", Snake<ActiveTestAbortReason>() },
        { RepoPaths.Combine("web", "src", "api", "control.ts"), "CONTROL_LOCK_REASONS", Snake<ControlLockReason>() },
        { RepoPaths.Combine("web", "src", "api", "control.ts"), "COMMAND_OUTCOMES", Snake<Switchboard.Application.Effector.CommandOutcome>() },
        { RepoPaths.Combine("web", "src", "api", "control.ts"), "COMMAND_NOT_SENT_REASONS", Snake<Switchboard.Application.Effector.CommandNotSentReason>() },
        { RepoPaths.Combine("web", "src", "api", "decisions.ts"), "TIER_REACTIONS", Snake<TierReaction>() },
        { RepoPaths.Combine("web", "src", "api", "decisions.ts"), "MODE_NOTES", Snake<ModeNote>() },
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
        var clock = new SteppableTime(SetupEndpointTests.At(7, 39));
        using var factory = SetupEndpointTests.ReplayHost(root, clock);
        SetupEndpointTests.WriteDecisions(root);

        var discovery = await Get(factory, "/api/setup/discovery");
        var readouts = await Get(factory, "/api/setup/readouts?entity=sensor.solaredge_m1_ac_current_a&entity=switch.grzalka_1&entity=sensor.not_there");
        var draft = await Get(factory, "/api/setup/draft");
        var templates = await Get(factory, "/api/setup/templates");
        var mode = await Get(factory, "/api/mode");
        var history = await Get(factory, "/api/decisions/history?limit=2");
        var control = await Get(factory, "/api/control");
        // Shadow (the default): the active self-test is refused, with its reasons.
        var preconditions = await Get(factory, "/api/setup/active-test/preconditions?device=heater_l1&stage=1");
        var activeRefused = await Post(factory, "/api/setup/active-test", new { device = "heater_l1", stage = 1 }, StatusCodes.Status409Conflict);

        var parsed = SetupDocumentMapper.Read(SetupFixtures.Tree(TestConfigurations.Example)).Draft;
        var withTotal = parsed with { Meter = parsed.Meter with { Total = new MeterTotal { Power = SetupFixtures.Signal("sensor.solaredge_m1_ac_power", "W") } } };
        var validation = await Post(factory, "/api/setup/validate", new { draft = withTotal, baseRevision = draft.GetProperty("revision").GetString() }, StatusCodes.Status200OK);
        var started = await Post(factory, "/api/setup/self-test", new { draft = parsed, device = new { index = 1 } }, StatusCodes.Status202Accepted);
        // grzalka_1 switched on at 07:40:05 and off at 07:46:05: a finished, confirmed watch.
        clock.Advance(SetupEndpointTests.At(7, 47, 10) - clock.GetUtcNow());
        var selfTest = await Get(factory, $"/api/setup/self-test/{started.GetProperty("id").GetString()}");
        var refusal = await Post(factory, "/api/setup/save", new { draft = parsed with { Acknowledgements = new AcknowledgementsDraft() }, baseRevision = draft.GetProperty("revision").GetString() }, StatusCodes.Status422UnprocessableEntity);
        var saved = await Post(factory, "/api/setup/save", new { draft = parsed with { Acknowledgements = new AcknowledgementsDraft { NotProtective = true, CommentsDropped = true } }, baseRevision = draft.GetProperty("revision").GetString() }, StatusCodes.Status200OK);
        PublishConfiguration(factory);
        var evidence = await Get(factory, "/api/setup/evidence");

        Assert.Contains(validation.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "phase_self_test_missing");
        Assert.NotEqual(JsonValueKind.Null, validation.GetProperty("reserve").ValueKind);
        Assert.NotEmpty(selfTest.GetProperty("observations").EnumerateArray());
        Assert.NotEmpty(evidence.GetProperty("devices").EnumerateArray().SelectMany(d => d.GetProperty("observations").EnumerateArray()));

        Compare("setup-discovery.sample.json", Trim(discovery, "entities", 8));
        Compare("setup-readouts.sample.json", readouts);
        Compare("setup-draft.sample.json", draft);
        Compare("setup-validation.sample.json", validation);
        Compare("setup-templates.sample.json", templates);
        Compare("setup-self-test-started.sample.json", started);
        Compare("setup-self-test.sample.json", selfTest);
        Compare("setup-refusal.sample.json", refusal);
        Compare("setup-saved.sample.json", saved);
        Compare("setup-evidence.sample.json", evidence);
        Compare("mode.sample.json", mode);
        Compare("decision-history.sample.json", history);
        Compare("control.sample.json", control);
        Compare("setup-active-test-preconditions.sample.json", preconditions);
        Compare("setup-active-test-refused.sample.json", activeRefused);
        Assert.Contains(preconditions.GetProperty("refusals").EnumerateArray(), r => r.GetProperty("code").GetString() == "control_not_active");
    }

    private static void Compare(string sample, JsonElement full)
    {
        var committed = SpaContract.Sample(Sample(sample), full);
        string[] free = ["$.issues[].args", "$.fileIssues[].args", "$.templates[].prefill", "$.observations[].deltas", "$.devices[].observations[].deltas"];
        Assert.Equal(SpaContract.Describe([full], free), SpaContract.Describe([committed], free));
    }

    /// <summary>A compact sample: the first entities of a long list, with derived and unverifiable ones kept.</summary>
    private static JsonElement Trim(JsonElement document, string list, int keep)
    {
        var node = JsonNode.Parse(document.GetRawText())!;
        var items = node[list]!.AsArray();
        var kept = items.Where((item, i) => i < keep || item!["derived"] is not null || item!["inRegistry"]!.GetValue<bool>() == false
            || item!["entityId"]!.GetValue<string>().StartsWith("sensor.solaredge_m1_ac_", StringComparison.Ordinal)
            || item!["entityId"]!.GetValue<string>().StartsWith("switch.", StringComparison.Ordinal)).Select(i => i!.DeepClone()).ToList();
        node[list] = new JsonArray([.. kept]);
        using var json = JsonDocument.Parse(node.ToJsonString());
        return json.RootElement.Clone();
    }

    /// <summary>The running add-on's configuration (the example) and a recorded switch on and off, for api/setup/evidence.</summary>
    private static void PublishConfiguration(SwitchboardHostFactory factory)
    {
        var harness = new GuardRunnerHarness(TestConfigurations.Example);
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(new SignalDiagnostics(harness.Holder.Current.Snapshot, harness.Configuration, harness.Holder.Current.Entities, []));
        var terms = new[] { ("a", Phase.L1), ("b", Phase.L2), ("c", Phase.L3) }.ToImmutableDictionary(t => t.Item2, t => new MeterTerm(EntityId.Parse($"sensor.solaredge_m1_ac_power_{t.Item1}"), "W", "positive_export"));
        var phases = terms.Keys.ToImmutableDictionary(p => p, p => new PhaseStepMeasurement(400, 410, p == Phase.L2 ? 2100 : 405, 25));
        var off = terms.Keys.ToImmutableDictionary(p => p, p => new PhaseStepMeasurement(2100, 2100, p == Phase.L2 ? 405 : 2095, 25));
        var store = factory.Services.GetRequiredService<PhaseEvidenceStore>();
        store.Add(new PhaseObservation(SetupEndpointTests.At(7, 40, 5), 1, EntityId.Parse("switch.grzalka_1"), ObservationSource.Recorded, "W", false, true, terms, phases, true, null));
        store.Add(new PhaseObservation(SetupEndpointTests.At(7, 46, 5), -1, EntityId.Parse("switch.grzalka_1"), ObservationSource.Recorded, "W", false, true, terms, off, true, null));
    }

    private static async Task<JsonElement> Get(SwitchboardHostFactory factory, string path)
    {
        var context = await SetupEndpointTests.SendAsync(factory, "GET", path);
        Assert.True(context.Response.StatusCode == StatusCodes.Status200OK, $"{path}: {context.Response.StatusCode}");
        return SetupEndpointTests.Json(context);
    }

    private static async Task<JsonElement> Post(SwitchboardHostFactory factory, string path, object body, int status)
    {
        var context = await SetupEndpointTests.SendAsync(factory, "POST", path, "application/json", JsonSerializer.Serialize(body, SetupEndpointTests.Web));
        Assert.True(context.Response.StatusCode == status, $"{path}: {context.Response.StatusCode}");
        return SetupEndpointTests.Json(context);
    }

    private static string[] Snake<TEnum>()
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(ApiText.Snake)];
}
