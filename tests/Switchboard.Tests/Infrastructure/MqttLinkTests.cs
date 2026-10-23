using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Health;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The MQTT link carries the watchdog entity. Its ordering rules are what make the entity honest:
/// availability before discovery before state, "offline" through the Last Will when the process dies,
/// and OFF then "offline" when it stops cleanly.
/// </summary>
public sealed class MqttLinkTests : IAsyncDisposable
{
    private const string Availability = "switchboard/availability";
    private const string State = "switchboard/healthy/state";
    private const string Discovery = "homeassistant/binary_sensor/switchboard/healthy/config";

    private readonly ObservableTimeProvider _time = new();
    private readonly FakeMqttTransport _transport = new(new MqttTopicGuard("homeassistant", "switchboard"));
    private readonly FakeCredentialSource _credentials = new();
    private readonly HomeAssistantConnectionState _homeAssistant = new();
    private readonly MqttConnectionState _mqttState = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly MqttLink _link;
    private Task? _run;

    public MqttLinkTests()
    {
        _link = CreateLink(_credentials);
    }

    [Fact]
    public void Last_will_is_offline_and_retained_on_our_availability_topic()
    {
        // Set on every CONNECT: if the process dies, the broker itself marks the entity unavailable.
        var settings = StartAndConnect();

        Assert.Equal(Availability, settings.WillTopic);
        Assert.Equal("offline", settings.WillPayload);
        Assert.True(settings.WillRetain);
        Assert.Equal("switchboard", settings.ClientId);
    }

    [Fact]
    public void Keep_alive_comes_from_configuration()
    {
        // How soon the broker notices a frozen process is a control parameter, not a library default.
        var settings = StartAndConnect();

        Assert.Equal(TestOptions.MqttKeepAlive, settings.KeepAlivePeriod);
    }

    [Fact]
    public async Task On_connect_publishes_online_then_discovery_then_state()
    {
        _homeAssistant.Set(ConnectionState.Connected);
        Start();

        var operations = await _transport.UntilAsync(o => o.Kind == "subscribe");

        Assert.Equal(
            [
                "connect",
                $"publish {Availability} online retained",
                $"publish {Discovery} {HealthEntityDiscovery.BuildConfig(TestOptions.Mqtt(), TestOptions.Heartbeat(), new AppVersion("0.1.0"))} retained",
                $"publish {State} ON",
                "subscribe homeassistant/status",
            ],
            operations.Select(o => o.ToString()));
        Assert.Equal(ConnectionState.Connected, _mqttState.Current);
    }

    [Fact]
    public async Task State_published_on_connect_is_OFF_while_Home_Assistant_is_down()
    {
        _homeAssistant.Set(ConnectionState.Connecting);
        Start();

        var operations = await _transport.UntilAsync(o => o.Kind == "subscribe");

        Assert.Contains(operations, o => o.Topic == State && o.Payload == "OFF" && !o.Retain);
    }

    [Fact]
    public async Task Home_Assistant_coming_online_triggers_a_republish_of_discovery_and_state()
    {
        _homeAssistant.Set(ConnectionState.Connected);
        Start();
        await _transport.UntilAsync(o => o.Kind == "subscribe");

        await _transport.DeliverAsync("homeassistant/status", "online");
        var republished = await _transport.UntilAsync(o => o.Topic == State);

        Assert.Equal([Discovery, State], republished.Select(o => o.Topic));
        Assert.True(republished[0].Retain);
    }

    [Fact]
    public async Task Home_Assistant_going_offline_triggers_nothing()
    {
        Start();
        await _transport.UntilAsync(o => o.Kind == "subscribe");
        var before = _transport.Log.Count;

        await _transport.DeliverAsync("homeassistant/status", "offline");
        await _transport.DeliverAsync("homeassistant/status", "online");
        await _transport.UntilAsync(o => o.Topic == State);

        // Only the "online" message produced the two republish operations.
        Assert.Equal(before + 2, _transport.Log.Count);
    }

    [Fact]
    public async Task Heartbeat_publishes_are_not_retained_and_follow_the_requested_state()
    {
        Start();
        await _transport.UntilAsync(o => o.Kind == "subscribe");

        await _link.PublishHealthAsync(true, CancellationToken.None);
        await _link.PublishHealthAsync(false, CancellationToken.None);

        var beats = _transport.Log.Where(o => o.Topic == State).Skip(1).ToList();
        Assert.Equal(["ON", "OFF"], beats.Select(o => o.Payload));
        Assert.All(beats, o => Assert.False(o.Retain));
    }

    [Fact]
    public async Task Heartbeat_before_the_session_is_announced_is_dropped()
    {
        // A state ahead of its discovery config would be ignored by HA at best.
        await _link.PublishHealthAsync(true, CancellationToken.None);

        Assert.Empty(_transport.Log);
    }

    [Fact]
    public async Task Shutdown_publishes_OFF_then_offline_then_disconnects()
    {
        _homeAssistant.Set(ConnectionState.Connected);
        Start();
        await _transport.UntilAsync(o => o.Kind == "subscribe");
        var before = _transport.Log.Count;

        await _stop.CancelAsync();
        await _run!.Soon();
        await _link.ShutdownAsync(CancellationToken.None);

        Assert.Equal(
            [$"publish {State} OFF", $"publish {Availability} offline retained", "disconnect"],
            _transport.Log.Skip(before).Select(o => o.ToString()));
        Assert.Equal(ConnectionState.Disconnected, _mqttState.Current);
    }

    [Fact]
    public async Task Shutdown_gives_up_at_its_own_deadline_when_the_broker_stops_acknowledging()
    {
        // The container is killed a few seconds after SIGTERM, and the Home Assistant socket is closed
        // after this step: a broker that never acknowledges must not hold the shutdown until the kill.
        Start();
        await _transport.UntilAsync(o => o.Kind == "subscribe");
        await _stop.CancelAsync();
        await _run!.Soon();
        _transport.HoldPublishes = true;

        var shutdown = _link.ShutdownAsync(CancellationToken.None);
        await _time.WaitForTimerAsync(TestOptions.MqttShutdownTimeout);
        _time.Advance(TestOptions.MqttShutdownTimeout);

        // Returns normally: an escaping cancellation would fail the host's stop instead.
        await shutdown.Soon();
        Assert.Equal(ConnectionState.Disconnected, _mqttState.Current);
    }

    [Fact]
    public async Task No_heartbeat_can_republish_ON_after_shutdown()
    {
        Start();
        await _transport.UntilAsync(o => o.Kind == "subscribe");
        await _link.ShutdownAsync(CancellationToken.None);
        var after = _transport.Log.Count;

        await _link.PublishHealthAsync(true, CancellationToken.None);

        Assert.Equal(after, _transport.Log.Count);
    }

    [Fact]
    public async Task Lost_connection_reconnects_with_fresh_credentials_and_announces_again()
    {
        _homeAssistant.Set(ConnectionState.Connected);
        Start();
        await _transport.UntilAsync(o => o.Kind == "subscribe");

        await _transport.DropAsync();
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);
        Assert.NotEqual(ConnectionState.Connected, _mqttState.Current);

        _time.Advance(TestOptions.ReconnectMin);
        var again = await _transport.UntilAsync(o => o.Kind == "subscribe");

        Assert.Equal(["connect", "publish", "publish", "publish", "subscribe"], again.Select(o => o.Kind));
        Assert.Equal(2, _credentials.Calls);
        Assert.Equal(ConnectionState.Connected, _mqttState.Current);
    }

    [Fact]
    public async Task A_session_refused_after_connect_is_left_and_retried_with_growing_delay()
    {
        // The broker accepts CONNECT but refuses discovery (an ACL without homeassistant/#). The client
        // is still connected then, and a connected client refuses every further connect: unless the
        // link leaves that session it never reconnects, and the entity stays gone until a restart.
        _transport.RefusedTopic = Discovery;
        Start();

        var first = await _transport.UntilAsync(o => o.Kind == "disconnect");
        Assert.Equal(
            ["connect", $"publish {Availability} online retained", $"publish {Availability} offline retained", "disconnect"],
            first.Select(o => o.ToString()));

        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);
        _time.Advance(TestOptions.ReconnectMin);
        var second = await _transport.UntilAsync(o => o.Kind == "disconnect");
        Assert.Equal("connect", second[0].Kind);

        // CONNACK alone is not a recovered session: the delay grows instead of retrying at the minimum forever.
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin * 2);
        Assert.Equal(2, _transport.Connects.Count);
        Assert.Equal(ConnectionState.Disconnected, _mqttState.Current);
    }

    [Fact]
    public async Task Reconnect_backoff_doubles_and_caps()
    {
        _transport.FailConnects = true;
        Start();

        var expected = new[] { 1, 2, 4, 8, 8 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
        foreach (var delay in expected)
        {
            await _time.WaitForTimerAsync(delay);
            _time.Advance(delay);
        }

        Assert.Equal(expected, _time.Armed.Take(expected.Length));
        Assert.Equal(ConnectionState.Disconnected, _mqttState.Current);
    }

    [Fact]
    public async Task Without_broker_configuration_the_link_reports_and_stays_down()
    {
        var link = CreateLink(new FakeCredentialSource { ConfigurationProblem = "no broker" });

        await link.RunAsync(CancellationToken.None).Soon();

        Assert.Empty(_transport.Connects);
        Assert.Equal(ConnectionState.Disconnected, _mqttState.Current);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.Soon();
        }

        _stop.Dispose();
    }

    private MqttLink CreateLink(IMqttCredentialSource credentials) => new(
        _transport,
        credentials,
        _mqttState,
        new HealthEvaluator(_homeAssistant, _mqttState),
        MsOptions.Create(TestOptions.Mqtt()),
        MsOptions.Create(TestOptions.Heartbeat()),
        new AppVersion("0.1.0"),
        _time,
        NullLogger<MqttLink>.Instance);

    private void Start() => _run = _link.RunAsync(_stop.Token);

    private MqttConnectSettings StartAndConnect()
    {
        Start();
        return Assert.Single(_transport.Connects);
    }
}
