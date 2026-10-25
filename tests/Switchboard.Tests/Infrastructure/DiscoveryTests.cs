using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Options;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Application.Setup;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// UI-03: entity pickers are fed by Home Assistant's own state machine and entity registry, read with
/// read-only queries only. The answers are untrusted: a state list of the wrong shape is refused
/// whole, a registry of the wrong shape counts as unavailable, and the result is bounded.
/// </summary>
public sealed class DiscoveryParserTests
{
    [Fact]
    public void Get_states_fixture_yields_unit_device_class_and_last_reported()
    {
        var snapshot = SetupValidatorTests.Fixture();

        var current = snapshot.Find(EntityId.Parse("sensor.solaredge_m1_ac_current_a"))!;
        Assert.Equal(("A", "current", "measurement", "solaredge_modbus", "dev_m1", true), (current.Unit, current.DeviceClass, current.StateClass, current.Platform, current.DeviceId, current.InRegistry));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero), current.Reported);
        Assert.Equal("2.61", current.State);
        Assert.Equal(4, snapshot.Devices.Length);
        Assert.Equal("Przekaźniki CWU", snapshot.Devices.Single(d => d.Id == "dev_relays").Name);
        Assert.True(snapshot.RegistryAvailable);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public void Registry_platform_marks_derived_sensors()
    {
        var snapshot = SetupValidatorTests.Fixture();

        Assert.Equal(DerivedCheck.Derived, DerivedSensorRule.Check(snapshot.Find(EntityId.Parse("sensor.usage")), snapshot, SetupFixtures.Defaults));
        Assert.Equal(DerivedCheck.Derived, DerivedSensorRule.Check(snapshot.Find(EntityId.Parse("sensor.zuzycie_srednie_5m")), snapshot, SetupFixtures.Defaults));
        Assert.Equal(DerivedCheck.Derived, DerivedSensorRule.Check(snapshot.Find(EntityId.Parse("sensor.kettle_power")), snapshot, SetupFixtures.Defaults));
        Assert.Equal(DerivedCheck.NotInRegistry, DerivedSensorRule.Check(snapshot.Find(EntityId.Parse("sensor.house_load_yaml")), snapshot, SetupFixtures.Defaults));
        Assert.Equal(DerivedCheck.Primary, DerivedSensorRule.Check(snapshot.Find(EntityId.Parse("sensor.solaredge_m1_ac_power_a")), snapshot, SetupFixtures.Defaults));
        Assert.Equal(DerivedCheck.RegistryUnavailable, DerivedSensorRule.Check(null, SetupValidatorTests.Fixture(registry: false), SetupFixtures.Defaults));
    }

    [Theory]
    [InlineData("""{"entity_id":"sensor.a","state":"1"}""")]
    [InlineData("""[{"entity_id":"sensor.a"}]""")]
    [InlineData("""[{"entity_id":"Sensor A","state":"1"}]""")]
    [InlineData("""[{"entity_id":"sensor.a","state":"1","attributes":[]}]""")]
    public void Malformed_result_is_refused_whole(string states)
    {
        using var document = JsonDocument.Parse(states);

        var refused = Assert.Throws<DiscoveryUnavailableException>(() => DiscoveryResponseParser.Parse(document.RootElement, null, null, DateTimeOffset.UnixEpoch, 100));

        Assert.Equal(DiscoveryUnavailableException.HomeAssistantUnavailable, refused.Code);
    }

    [Fact]
    public void A_malformed_registry_counts_as_unavailable_not_as_primary()
    {
        using var states = JsonDocument.Parse("""[{"entity_id":"sensor.a","state":"1"}]""");
        using var registry = JsonDocument.Parse("""[{"platform":"template"}]""");

        var snapshot = DiscoveryResponseParser.Parse(states.RootElement, registry.RootElement, null, DateTimeOffset.UnixEpoch, 100);

        Assert.False(snapshot.RegistryAvailable);
        Assert.Equal(DerivedCheck.RegistryUnavailable, DerivedSensorRule.Check(snapshot.Find(EntityId.Parse("sensor.a")), snapshot, SetupFixtures.Defaults));
    }

    [Fact]
    public void Bounded_and_flags_truncation()
    {
        var snapshot = SetupValidatorTests.Fixture(max: 10);

        Assert.Equal(10, snapshot.Entities.Length);
        Assert.True(snapshot.Truncated);
        Assert.Equal(snapshot.Entities.Select(e => e.Entity).Order(), snapshot.Entities.Select(e => e.Entity));
    }
}

public sealed class EntityDiscoveryCacheTests
{
    [Fact]
    public async Task Cached_for_ttl_refreshed_on_request()
    {
        var time = new FakeTimeProvider();
        var source = new CountingSource();
        var cache = new EntityDiscoveryCache(source, time, TimeSpan.FromMinutes(1));

        await cache.GetAsync(refresh: false, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(59));
        await cache.GetAsync(refresh: false, CancellationToken.None);
        Assert.Equal(1, source.Reads);

        await cache.GetAsync(refresh: true, CancellationToken.None);
        Assert.Equal(2, source.Reads);

        time.Advance(TimeSpan.FromMinutes(1));
        await cache.GetAsync(refresh: false, CancellationToken.None);
        Assert.Equal(3, source.Reads);
    }

    private sealed class CountingSource : IEntityDiscoverySource
    {
        public int Reads { get; private set; }

        public Task<DiscoverySnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new DiscoverySnapshot(DateTimeOffset.UnixEpoch, [], [], true, false));
        }
    }
}

/// <summary>
/// The hard boundary of the discovery path: a query goes out only as one of the three read-only
/// types, exactly {id, type}, refused before the socket otherwise; its answer is matched by id and
/// timed out on the virtual clock, and a session that ends never leaves a query waiting.
/// </summary>
public sealed class HomeAssistantQueryTests : IAsyncDisposable
{
    private readonly ObservableTimeProvider _time = new();
    private readonly ScriptedTransportFactory _factory = new();
    private readonly HomeAssistantConnectionState _state = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HomeAssistantClient _client;
    private Task? _run;

    public HomeAssistantQueryTests()
    {
        _client = new HomeAssistantClient(
            _factory,
            new HomeAssistantEndpoint(new Uri("ws://supervisor/core/websocket"), "token"),
            _state,
            MsOptions.Create(TestOptions.HomeAssistant()),
            _time,
            NullLogger<HomeAssistantClient>.Instance,
            new RecordingSubscriptionSink());
    }

    [Theory]
    [InlineData("call_service")]
    [InlineData("fire_event")]
    [InlineData("execute_script")]
    [InlineData("subscribe_events")]
    [InlineData("subscribe_trigger")]
    [InlineData("config/entity_registry/update")]
    [InlineData("ping")]
    [InlineData("subscribe_entities")]
    public async Task State_changing_types_are_refused_before_the_socket(string type)
    {
        var transport = await ConnectAsync();
        var sentBefore = transport.Sent.Count;

        await Assert.ThrowsAsync<HomeAssistantCommandRefusedException>(() => _client.QueryAsync(type, TimeSpan.FromSeconds(7), CancellationToken.None));

        Assert.Equal(sentBefore, transport.Sent.Count);
    }

    [Fact]
    public async Task Read_only_queries_are_sent_as_id_and_type_only()
    {
        var transport = await ConnectAsync();

        var query = _client.QueryAsync(HomeAssistantCommandAllowList.GetStates, TimeSpan.FromSeconds(7), CancellationToken.None);
        using var sent = JsonDocument.Parse(await transport.NextSentAsync());

        Assert.Equal(["id", "type"], sent.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("get_states", sent.RootElement.GetProperty("type").GetString());
        await transport.PushAndWaitAsync($$"""{"id":{{sent.RootElement.GetProperty("id").GetInt32()}},"type":"result","success":true,"result":[]}""");
        Assert.Equal(JsonValueKind.Array, (await query.Soon()).ValueKind);
    }

    [Fact]
    public async Task Query_result_is_correlated_by_id()
    {
        var transport = await ConnectAsync();
        var states = _client.QueryAsync(HomeAssistantCommandAllowList.GetStates, TimeSpan.FromSeconds(7), CancellationToken.None);
        var statesId = Id(await transport.NextSentAsync());
        var devices = _client.QueryAsync(HomeAssistantCommandAllowList.DeviceRegistryList, TimeSpan.FromSeconds(7), CancellationToken.None);
        var devicesId = Id(await transport.NextSentAsync());

        // Answers out of order go to their own query.
        await transport.PushAndWaitAsync($$"""{"id":{{devicesId}},"type":"result","success":true,"result":[{"id":"d1"}]}""");
        await transport.PushAndWaitAsync($$"""{"id":{{statesId}},"type":"result","success":true,"result":[{"entity_id":"sensor.a","state":"1"}]}""");

        Assert.Equal("d1", (await devices.Soon())[0].GetProperty("id").GetString());
        Assert.Equal("sensor.a", (await states.Soon())[0].GetProperty("entity_id").GetString());
    }

    [Fact]
    public async Task Query_times_out_on_the_fake_clock()
    {
        var transport = await ConnectAsync();
        var query = _client.QueryAsync(HomeAssistantCommandAllowList.GetStates, TimeSpan.FromSeconds(7), CancellationToken.None);
        await transport.NextSentAsync();
        await _time.WaitForTimerAsync(TimeSpan.FromSeconds(7));

        _time.Advance(TimeSpan.FromSeconds(7));

        await Assert.ThrowsAsync<TimeoutException>(() => query.Soon());
    }

    [Fact]
    public async Task Refusal_by_home_assistant_is_a_failed_query()
    {
        var transport = await ConnectAsync();
        var query = _client.QueryAsync(HomeAssistantCommandAllowList.EntityRegistryList, TimeSpan.FromSeconds(7), CancellationToken.None);
        var id = Id(await transport.NextSentAsync());

        await transport.PushAndWaitAsync($$$"""{"id":{{{id}}},"type":"result","success":false,"error":{"code":"unauthorized","message":"Unauthorized"}}""");

        var failed = await Assert.ThrowsAsync<HomeAssistantQueryFailedException>(() => query.Soon());
        Assert.Contains("Unauthorized", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_ends_fails_the_pending_query()
    {
        var transport = await ConnectAsync();
        var query = _client.QueryAsync(HomeAssistantCommandAllowList.GetStates, TimeSpan.FromSeconds(7), CancellationToken.None);
        await transport.NextSentAsync();

        transport.Push(null);

        await Assert.ThrowsAsync<HomeAssistantQueryFailedException>(() => query.Soon());
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run.Soon();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static int Id(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private async Task<ScriptedWebSocketTransport> ConnectAsync()
    {
        _run = _client.RunAsync(_stop.Token);
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        await transport.NextSentAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_ok"}""");
        await _state.WaitForAsync(ConnectionState.Connected);
        return transport;
    }
}

/// <summary>
/// The discovery link is a second, on-demand connection: it never changes the protection link's
/// state, sends nothing but read-only messages, subscribes only to the entities the wizard shows
/// (bounded, for a lease or a self-test's hold) and closes once nothing needs it.
/// </summary>
public sealed class DiscoveryLinkTests : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeaseTime = TimeSpan.FromSeconds(30);

    // Keep-alives far apart, so the lease and idle timers of a test are never interleaved with pings.
    private static readonly TimeSpan PingInterval = TimeSpan.FromHours(1);

    private readonly ObservableTimeProvider _time = new();
    private readonly ScriptedTransportFactory _factory = new();
    private readonly HomeAssistantDiscoveryLink _link;
    private readonly HomeAssistantConnectionState _protection = new();

    public DiscoveryLinkTests()
    {
        _protection.Set(ConnectionState.Connected);
        _link = new HomeAssistantDiscoveryLink(
            _factory,
            new HomeAssistantEndpoint(new Uri("ws://supervisor/core/websocket"), "token"),
            MsOptions.Create(new HomeAssistantOptions
            {
                SupervisorWebSocketUrl = "ws://supervisor/core/websocket",
                SupervisorRestUrl = "http://supervisor/core/api/",
                PingInterval = PingInterval,
                PongTimeout = TestOptions.PongTimeout,
                ReconnectMinDelay = TestOptions.ReconnectMin,
                ReconnectMaxDelay = TestOptions.ReconnectMax,
                StateReadTimeout = TestOptions.StateReadTimeout,
                CommandTimeout = TestOptions.CommandTimeout,
                ShutdownCommandWait = TimeSpan.FromMilliseconds(500),
                CloseTimeout = TimeSpan.FromMilliseconds(500),
            }),
            MsOptions.Create(Options(maxReadouts: 3)),
            new CandidateReadoutStore(64, 256, SetupFixtures.Control.Signals.Refresh),
            new UnavailableEntityStateReader(),
            _time,
            new SignalClock(_time),
            NullLoggerFactory.Instance);
    }

    public static SetupOptions Options(int maxReadouts = 32) => new()
    {
        DiscoveryCacheTtl = TimeSpan.FromMinutes(1),
        DiscoveryLinkIdleTimeout = IdleTimeout,
        DiscoveryRequestTimeout = RequestTimeout,
        MaxDiscoveredEntities = 5000,
        MaxReadoutEntities = maxReadouts,
        ReadoutLeaseTime = LeaseTime,
        ReadoutHistoryLength = 64,
        MaxRetainedReadouts = 256,
        ReadoutProbeInterval = TimeSpan.FromSeconds(31),
        MaxBackups = 10,
        ReloadConfirmTimeout = TimeSpan.FromSeconds(9),
        EvidencePerDevice = 20,
        EvidenceMaxFileBytes = 262144,
    };

    [Fact]
    public async Task Discovery_sends_the_three_read_only_queries_and_parses_the_answers()
    {
        var read = _link.ReadAsync(CancellationToken.None);
        var transport = await AuthenticateAsync();

        var states = await Answer(transport, "get_states", """[{"entity_id":"sensor.a","state":"1","attributes":{"unit_of_measurement":"A","device_class":"current"}}]""");
        var registry = await Answer(transport, "config/entity_registry/list", """[{"entity_id":"sensor.a","platform":"template"}]""");
        var devices = await Answer(transport, "config/device_registry/list", "[]");
        var snapshot = await read.Soon();

        Assert.Equal(["get_states", "config/entity_registry/list", "config/device_registry/list"], [states, registry, devices]);
        Assert.Equal("template", snapshot.Find(EntityId.Parse("sensor.a"))!.Platform);
        Assert.All(transport.Sent.Skip(1), frame => Assert.Contains(Type(frame), HomeAssistantCommandAllowList.ReadOnlyQueries.Concat(["ping", "subscribe_entities", "unsubscribe_events"])));
    }

    [Fact]
    public async Task Discovery_link_failure_leaves_health_and_signal_source_connected()
    {
        // The protection link is another client with another state: the discovery link failing to
        // connect (or timing out) changes nothing of it.
        _factory.FailConnects = true;
        var read = _link.ReadAsync(CancellationToken.None);
        await _time.WaitForTimerAsync(RequestTimeout);
        _time.Advance(RequestTimeout);

        var refused = await Assert.ThrowsAsync<DiscoveryUnavailableException>(() => read.Soon());

        Assert.Equal(DiscoveryUnavailableException.HomeAssistantUnavailable, refused.Code);
        Assert.Equal(ConnectionState.Connected, _protection.Current);
        Assert.NotSame(_protection, _link.Connection);
    }

    [Fact]
    public async Task Query_times_out_on_the_fake_clock_as_unavailable()
    {
        var read = _link.ReadAsync(CancellationToken.None);
        var transport = await AuthenticateAsync();
        Assert.Equal("get_states", Type(await transport.NextSentAsync()));
        await _time.WaitForTimerAsync(RequestTimeout);
        await _time.WaitForTimerAsync(RequestTimeout);

        _time.Advance(RequestTimeout);

        Assert.Equal(DiscoveryUnavailableException.HomeAssistantUnavailable, (await Assert.ThrowsAsync<DiscoveryUnavailableException>(() => read.Soon())).Code);
    }

    [Fact]
    public async Task A_socket_reset_while_querying_is_unavailable_not_a_server_error()
    {
        // Home Assistant restarts while the discovery query is being sent: the state still says
        // connected, the send throws. Validate and save must go on without discovery (503 for GET
        // api/setup/discovery), never answer 500.
        var read = _link.ReadAsync(CancellationToken.None);
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        Assert.Equal("auth", Type(await transport.NextSentAsync()));
        transport.FailSends = true;
        await transport.PushAndWaitAsync("""{"type":"auth_ok"}""");

        var refused = await Assert.ThrowsAsync<DiscoveryUnavailableException>(() => read.Soon());

        Assert.Equal(DiscoveryUnavailableException.HomeAssistantUnavailable, refused.Code);
    }

    [Fact]
    public async Task A_closed_session_client_no_longer_follows_the_link()
    {
        // Each reopen after the idle timeout creates a new client; the closed one must not stay
        // subscribed to the link's tracked-set event for the life of the process.
        _link.Lease([EntityId.Parse("sensor.a")]);
        var first = await AuthenticateAsync();
        _time.Advance(IdleTimeout);
        Assert.True(first.CloseRequested);

        _link.Lease([EntityId.Parse("sensor.a")]);
        await AuthenticateAsync();
        // The old session closes in the background (fire and forget from the idle timer): bounded
        // polling for that completion, like Within.Soon, never a timing assumption of the rule.
        for (var i = 0; Followers() > 1 && i < 500; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, Followers());
    }

    [Fact]
    public async Task Readout_subscription_is_bounded_and_dropped_after_its_lease()
    {
        var entities = new[] { "sensor.a", "sensor.b", "sensor.c", "sensor.d" }.Select(EntityId.Parse).ToList();
        var readouts = _link.Lease(entities);
        var transport = await AuthenticateAsync();

        // At most MaxReadoutEntities (3 here), always with an entity filter.
        Assert.Equal(3, readouts.Length);
        using var subscribe = JsonDocument.Parse(await NextOfTypeAsync(transport, "subscribe_entities"));
        Assert.Equal(["sensor.a", "sensor.b", "sensor.c"], subscribe.RootElement.GetProperty("entity_ids").EnumerateArray().Select(e => e.GetString()));
        var subscription = subscribe.RootElement.GetProperty("id").GetInt32();
        await transport.PushAndWaitAsync($$"""{"id":{{subscription}},"type":"result","success":true,"result":null}""");

        // Not renewed: after the lease the subscription is ended.
        _time.Advance(LeaseTime);
        using var unsubscribe = JsonDocument.Parse(await NextOfTypeAsync(transport, "unsubscribe_events"));
        Assert.Equal(subscription, unsubscribe.RootElement.GetProperty("subscription").GetInt32());
        Assert.Empty(_link.Tracked);
    }

    [Fact]
    public async Task Link_closes_after_idle_timeout()
    {
        _link.Lease([EntityId.Parse("sensor.a")]);
        var transport = await AuthenticateAsync();

        _time.Advance(LeaseTime);
        Assert.False(transport.CloseRequested);
        _time.Advance(IdleTimeout - LeaseTime);

        Assert.True(transport.CloseRequested);
        Assert.Null(_link.Connection);
    }

    [Fact]
    public async Task Link_stays_open_while_a_self_test_session_is_active()
    {
        var hold = _link.Hold([EntityId.Parse("sensor.a"), EntityId.Parse("switch.heater")]);
        var transport = await AuthenticateAsync();

        _time.Advance(IdleTimeout);
        _time.Advance(IdleTimeout);

        Assert.False(transport.CloseRequested);
        Assert.Equal(["sensor.a", "switch.heater"], _link.Tracked.Select(e => e.Value));
        hold.Dispose();
        Assert.Empty(_link.Tracked);
    }

    [Fact]
    public async Task Self_test_sends_nothing_but_subscribe_unsubscribe_ping()
    {
        // The passive self-test watches: everything it makes the link send is read-only.
        var hold = _link.Hold([EntityId.Parse("sensor.a"), EntityId.Parse("switch.heater")]);
        var transport = await AuthenticateAsync();
        var subscribe = Id(await NextOfTypeAsync(transport, "subscribe_entities"));
        await transport.PushAndWaitAsync($$"""{"id":{{subscribe}},"type":"result","success":true,"result":null}""");
        _time.Advance(PingInterval);
        var ping = Id(await NextOfTypeAsync(transport, "ping"));
        await transport.PushAndWaitAsync($$"""{"id":{{ping}},"type":"pong"}""");
        hold.Dispose();
        await NextOfTypeAsync(transport, "unsubscribe_events");

        var types = transport.Sent.Skip(1).Select(Type).Distinct().Order();
        Assert.Equal(["ping", "subscribe_entities", "unsubscribe_events"], types);
    }

    public async ValueTask DisposeAsync() => await _link.DisposeAsync();

    /// <summary>The handlers of the link's TrackedChanged event: one per live client.</summary>
    private int Followers() =>
        (typeof(HomeAssistantDiscoveryLink).GetField(nameof(HomeAssistantDiscoveryLink.TrackedChanged), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_link) as Delegate)?.GetInvocationList().Length ?? 0;

    private async Task<ScriptedWebSocketTransport> AuthenticateAsync()
    {
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        Assert.Equal("auth", Type(await transport.NextSentAsync()));
        await transport.PushAndWaitAsync("""{"type":"auth_ok"}""");
        return transport;
    }

    private static async Task<string> Answer(ScriptedWebSocketTransport transport, string expected, string result)
    {
        var frame = await NextOfTypeAsync(transport, expected);
        await transport.PushAndWaitAsync($$"""{"id":{{Id(frame)}},"type":"result","success":true,"result":{{result}}}""");
        return Type(frame);
    }

    private static async Task<string> NextOfTypeAsync(ScriptedWebSocketTransport transport, string type)
    {
        while (true)
        {
            var frame = await transport.NextSentAsync();
            if (Type(frame) == type)
            {
                return frame;
            }
        }
    }

    private static int Id(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private static string Type(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("type").GetString()!;
    }
}

/// <summary>
/// 07-PITFALLS #1 in the wizard: subscribe_entities pushes nothing for a repeated value, so a healthy
/// constant entity (an idle phase at 0.00 A) would show an ever-growing age. A subscribed candidate
/// silent for ReadoutProbeInterval gets its last_reported read once (a read-only state read).
/// </summary>
public sealed class DiscoveryLinkProbeTests : IAsyncDisposable
{
    private readonly ObservableTimeProvider _time = new();
    private readonly ScriptedTransportFactory _factory = new();
    private readonly CountingReader _reader = new();
    private readonly HomeAssistantDiscoveryLink _link;

    public DiscoveryLinkProbeTests()
    {
        _link = new HomeAssistantDiscoveryLink(
            _factory,
            new HomeAssistantEndpoint(new Uri("ws://supervisor/core/websocket"), "token"),
            MsOptions.Create(new HomeAssistantOptions
            {
                SupervisorWebSocketUrl = "ws://supervisor/core/websocket",
                SupervisorRestUrl = "http://supervisor/core/api/",
                PingInterval = TimeSpan.FromHours(1),
                PongTimeout = TestOptions.PongTimeout,
                ReconnectMinDelay = TestOptions.ReconnectMin,
                ReconnectMaxDelay = TestOptions.ReconnectMax,
                StateReadTimeout = TestOptions.StateReadTimeout,
                CommandTimeout = TestOptions.CommandTimeout,
                ShutdownCommandWait = TimeSpan.FromMilliseconds(500),
                CloseTimeout = TimeSpan.FromMilliseconds(500),
            }),
            MsOptions.Create(DiscoveryLinkTests.Options()),
            new CandidateReadoutStore(64, 256, SetupFixtures.Control.Signals.Refresh),
            _reader,
            _time,
            new SignalClock(_time),
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task Constant_value_candidate_is_not_shown_stale_when_HA_last_reported_advances()
    {
        var entity = EntityId.Parse("sensor.meter_current_a");
        var reportedAt = _time.GetUtcNow() - TimeSpan.FromMinutes(2);
        _link.Lease([entity]);
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        await transport.NextSentAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_ok"}""");
        using var subscribe = JsonDocument.Parse(await transport.NextSentAsync());
        var id = subscribe.RootElement.GetProperty("id").GetInt32();
        var seconds = reportedAt.ToUnixTimeMilliseconds() / 1000.0;
        var lc = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await transport.PushAndWaitAsync("{\"id\":" + id + ",\"type\":\"event\",\"event\":{\"a\":{\"sensor.meter_current_a\":{\"s\":\"0.00\",\"a\":{\"unit_of_measurement\":\"A\"},\"lc\":" + lc + "}}}}");
        Assert.Equal(TimeSpan.FromMinutes(2), _link.Peek(entity)!.Age);

        // Home Assistant confirms the same value now; the next lease asks for it.
        _reader.Answer = new EntityUpdate(entity, "0.00", "A", reportedAt, _time.GetUtcNow());
        _link.Lease([entity]);
        await _reader.Asked.Task.Soon();

        Assert.Equal(TimeSpan.Zero, (await WaitForAge(entity)).Age);
        Assert.Equal(1, _reader.Reads);
    }

    public async ValueTask DisposeAsync() => await _link.DisposeAsync();

    private async Task<CandidateReadout> WaitForAge(EntityId entity)
    {
        for (var i = 0; i < 100; i++)
        {
            if (_link.Peek(entity) is { Age: { } age } readout && age == TimeSpan.Zero)
            {
                return readout;
            }

            await Task.Yield();
        }

        return _link.Peek(entity)!;
    }

    private sealed class CountingReader : IEntityStateReader
    {
        public EntityUpdate? Answer { get; set; }

        public int Reads { get; private set; }

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EntityStateRead> ReadAsync(EntityId entity, CancellationToken cancellationToken)
        {
            Reads++;
            Asked.TrySetResult();
            return Task.FromResult<EntityStateRead>(Answer is { } update ? new EntityStateRead.Found(update) : new EntityStateRead.NotFound());
        }
    }
}
