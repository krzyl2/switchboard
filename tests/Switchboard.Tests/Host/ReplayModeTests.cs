using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Switchboard.Application.Health;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Replay;
using Switchboard.Infrastructure.Runtime;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// Development replay must stay a sandbox: never as an add-on, never a connection to Home Assistant
/// or the house broker (a replay instance there would take over the live add-on's MQTT identity and
/// report it unhealthy, and the documented shed automation would switch real loads off), and its
/// sped-up clock must never reach heartbeat, backoff or keep-alive.
/// </summary>
public sealed class ReplayModeTests
{
    private static readonly string SyntheticCsv = RepoPaths.Combine("tests", "fixtures", "replay", ReplayFixtures.SyntheticFile);

    [Fact]
    public void Replay_refuses_to_start_as_addon()
    {
        var environment = new RuntimeEnvironment { SupervisorToken = "supervisor", DevReplayPath = SyntheticCsv };

        var refused = Assert.Throws<InvalidOperationException>(() => SignalSourceSelection.Resolve(environment));

        Assert.Contains("SWITCHBOARD_DEV_REPLAY", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_in_an_addon_stops_the_process_at_startup()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?>
        {
            ["SUPERVISOR_TOKEN"] = "supervisor",
            ["SWITCHBOARD_DEV_REPLAY"] = SyntheticCsv,
        });

        Assert.ThrowsAny<Exception>(() => factory.Server);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("fast")]
    public void Replay_speed_outside_its_range_is_refused(string speed)
    {
        var environment = new RuntimeEnvironment { DevReplayPath = SyntheticCsv, DevReplaySpeed = speed };

        Assert.Throws<InvalidOperationException>(() => SignalSourceSelection.Resolve(environment));
    }

    [Fact]
    public async Task Replay_never_creates_HA_client_and_never_connects_MQTT()
    {
        var sockets = new ScriptedTransportFactory();
        var broker = new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard"));
        var time = new ObservableTimeProvider();
        using var root = new TempWebRoot();
        await using var factory = ReplayHost(root, time, sockets, broker);

        var holder = factory.Services.GetRequiredService<SignalSnapshotHolder>();

        // The replay clock starts at the first recorded instant and moves only with the host clock.
        time.Advance(TimeSpan.FromSeconds(15));
        await SignalsEndpointTests.WaitForAsync(holder, d => d.Snapshot.Protection.IsDataValid(Phase.L1));
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(0, sockets.CreatedCount);
        Assert.Empty(broker.Log);
        Assert.Empty(broker.Connects);
    }

    [Fact]
    public async Task Replay_clock_does_not_drive_heartbeat_or_backoff()
    {
        var time = new ObservableTimeProvider();
        using var root = new TempWebRoot();
        await using var factory = ReplayHost(root, time, new ScriptedTransportFactory(), new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard")));

        var signalClock = factory.Services.GetRequiredService<SignalClock>().Time;

        Assert.IsType<ReplayTimeProvider>(signalClock);
        Assert.Same(time, factory.Services.GetRequiredService<TimeProvider>());
        Assert.Equal(ReplayFixtures.SyntheticData.Start, signalClock.GetUtcNow());
        Assert.NotNull(factory.Services.GetRequiredService<HeartbeatLoop>());
    }

    [Fact]
    public async Task Replay_mode_serves_recorded_signals_on_the_api()
    {
        var time = new ObservableTimeProvider();
        using var root = new TempWebRoot();
        await using var factory = ReplayHost(root, time, new ScriptedTransportFactory(), new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard")));
        var holder = factory.Services.GetRequiredService<SignalSnapshotHolder>();

        // The replay clock starts at the first recorded instant and moves only with the host clock.
        time.Advance(TimeSpan.FromSeconds(15));
        await SignalsEndpointTests.WaitForAsync(holder, d => d.Snapshot.Protection.IsDataValid(Phase.L1));

        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/signals";
            c.Connection.RemoteIpAddress = IPAddress.Loopback;
        }, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(context.Response.Body);

        Assert.Equal("replay", json.RootElement.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("connected", json.RootElement.GetProperty("source").GetProperty("status").GetString());
        var current = json.RootElement.GetProperty("signals").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "grid_current_l1");
        Assert.Equal("sensor.solaredge_m1_ac_current_a", current.GetProperty("entity").GetString());
        Assert.Equal(("A", "A"), (current.GetProperty("unit").GetString(), current.GetProperty("raw").GetProperty("unit").GetString()));
    }

    private static SwitchboardHostFactory ReplayHost(TempWebRoot root, TimeProvider time, ScriptedTransportFactory sockets, FakeMqttTransport broker)
    {
        var config = Path.Combine(root.Path, "switchboard.yaml");
        File.WriteAllText(config, TestConfigurations.Example);
        var factory = new SwitchboardHostFactory(
            root.Path,
            new Dictionary<string, string?>
            {
                ["SWITCHBOARD_DEV_REPLAY"] = SyntheticCsv,
                ["SWITCHBOARD_CONFIG_PATH"] = config,

                // Both links would connect if replay let them run.
                ["SWITCHBOARD_HA_URL"] = "http://homeassistant.invalid:8123",
                ["SWITCHBOARD_HA_TOKEN"] = "token",
                ["SWITCHBOARD_MQTT_HOST"] = "broker.invalid",
            },
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(time);
                services.RemoveAll<IWebSocketTransportFactory>();
                services.AddSingleton<IWebSocketTransportFactory>(sockets);
                services.RemoveAll<IMqttTransport>();
                services.AddSingleton<IMqttTransport>(broker);
                services.RemoveAll<IMqttCredentialSource>();
                services.AddSingleton<IMqttCredentialSource>(new FakeCredentialSource());
            });
        _ = factory.Server;
        return factory;
    }
}
