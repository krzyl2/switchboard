using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// SAFE-02 end to end through the real host wiring: starting announces the watchdog entity, and
/// stopping the host (SIGTERM) leaves it OFF and offline instead of letting a stale state linger
/// until expiry. Broker and HA socket are fakes; the clock is virtual.
/// </summary>
public sealed class HostLifecycleTests
{
    [Fact]
    public async Task Host_start_announces_and_host_stop_leaves_the_entity_off_and_offline()
    {
        var transport = new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard"));
        using var root = new TempWebRoot();
        var factory = new SwitchboardHostFactory(root.Path, replaceLinks: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new ObservableTimeProvider());
            services.RemoveAll<IMqttTransport>();
            services.AddSingleton<IMqttTransport>(transport);
            services.RemoveAll<IMqttCredentialSource>();
            services.AddSingleton<IMqttCredentialSource>(new FakeCredentialSource());
            services.RemoveAll<IWebSocketTransportFactory>();
            services.AddSingleton<IWebSocketTransportFactory>(new ScriptedTransportFactory());
        });

        _ = factory.Server;
        var started = await transport.UntilAsync(o => o.Kind == "subscribe");
        Assert.Contains(started, o => o.Topic == "switchboard/availability" && o.Payload == "online" && o.Retain);
        Assert.Contains(started, o => o.Topic == "homeassistant/binary_sensor/switchboard/healthy/config" && o.Retain);
        var beforeStop = transport.Log.Count;

        await factory.DisposeAsync();

        // A heartbeat may land between start and stop; the stop sequence itself must close the log.
        var tail = transport.Log.Skip(beforeStop).Select(o => o.ToString()).ToList();
        Assert.Equal(
            ["publish switchboard/healthy/state OFF", "publish switchboard/availability offline retained", "disconnect"],
            tail.TakeLast(3));
        Assert.False(transport.IsConnected);
    }
}
