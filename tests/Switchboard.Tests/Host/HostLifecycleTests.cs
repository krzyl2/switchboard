using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
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

    [Fact]
    public async Task Host_stop_records_the_shutdown_and_leaves_a_clean_state_file()
    {
        // SAFE-01 graceful stop: the guard stops first, records the shutdown safe state, writes the
        // decision log and state.json (clean_shutdown true, so the next start may credit the downtime),
        // and the MQTT entity still goes OFF and offline after it.
        var transport = new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard"));
        using var root = new TempWebRoot();
        var configPath = Path.Combine(root.Path, "switchboard.yaml");
        await File.WriteAllTextAsync(configPath, TestDevices.Document(), TestContext.Current.CancellationToken);
        var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_CONFIG_PATH"] = configPath }, services =>
        {
            services.RemoveAll<IMqttTransport>();
            services.AddSingleton<IMqttTransport>(transport);
            services.RemoveAll<IMqttCredentialSource>();
            services.AddSingleton<IMqttCredentialSource>(new FakeCredentialSource());
            services.RemoveAll<IWebSocketTransportFactory>();
            services.AddSingleton<IWebSocketTransportFactory>(new ScriptedTransportFactory());
        });

        // Started once the guard knows the configuration (without one there is nothing to protect,
        // and such a run never counts as a clean stop) and MQTT is announced.
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = factory.Services.GetRequiredService<Switchboard.Application.Guard.GuardStatusHolder>();
        holder.Published += (_, status) =>
        {
            if (status.Breaker is not null)
            {
                configured.TrySetResult();
            }
        };
        if (holder.Current.Breaker is not null)
        {
            configured.TrySetResult();
        }

        await configured.Task.Soon();
        await transport.UntilAsync(o => o.Kind == "subscribe" && o.Topic == "switchboard/kill_switch/set");
        await factory.DisposeAsync();

        var data = Path.Combine(root.Path, "data");
        var stateText = await File.ReadAllTextAsync(Path.Combine(data, "state.json"), TestContext.Current.CancellationToken);
        var decisions = Directory.GetFiles(Path.Combine(data, "decisions"), "*.jsonl").SelectMany(File.ReadAllLines).ToList();
        using var state = System.Text.Json.JsonDocument.Parse(stateText);
        Assert.True(state.RootElement.GetProperty("clean_shutdown").GetBoolean(), stateText + Environment.NewLine + string.Join(Environment.NewLine, decisions));
        Assert.Contains(decisions, l => l.Contains("\"action\":\"mode_resolved\"", StringComparison.Ordinal));
        Assert.Contains(decisions, l => l.Contains("\"action\":\"switch_off\"", StringComparison.Ordinal) && l.Contains("\"device\":\"heater_l1\"", StringComparison.Ordinal));
        Assert.Equal(
            ["publish switchboard/healthy/state OFF", "publish switchboard/availability offline retained", "disconnect"],
            transport.Log.Select(o => o.ToString()).TakeLast(3));
    }

    [Fact]
    public async Task A_failed_guard_loop_stops_the_add_on()
    {
        // Fail-safe: a guard loop that dies (here: its input port throws on every read) must not
        // leave a process serving a frozen status while the health entity stays ON and the
        // HA-side shed automation never fires. The add-on stops (s6 and the Supervisor restart it),
        // the stop path survives the same failure again, and the entity goes OFF and offline.
        var transport = new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard"));
        using var root = new TempWebRoot();
        var factory = new SwitchboardHostFactory(root.Path, replaceLinks: services =>
        {
            services.RemoveAll<IMqttTransport>();
            services.AddSingleton<IMqttTransport>(transport);
            services.RemoveAll<IMqttCredentialSource>();
            services.AddSingleton<IMqttCredentialSource>(new FakeCredentialSource());
            services.RemoveAll<IWebSocketTransportFactory>();
            services.AddSingleton<IWebSocketTransportFactory>(new ScriptedTransportFactory());
            services.RemoveAll<IProtectionInputSource>();
            services.AddSingleton<IProtectionInputSource>(new BrokenProtectionSource());
        });

        _ = factory.Server;
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());
        await stopping.Task.Soon();
        await transport.UntilAsync(o => o.Kind == "subscribe" && o.Topic == "switchboard/kill_switch/set");
        await factory.DisposeAsync();

        Assert.Equal(
            ["publish switchboard/healthy/state OFF", "publish switchboard/availability offline retained", "disconnect"],
            transport.Log.Select(o => o.ToString()).TakeLast(3));
    }

    /// <summary>A defect in the guard's input path: every read throws.</summary>
    private sealed class BrokenProtectionSource : IProtectionInputSource
    {
        public ProtectionInputs Current => throw new InvalidOperationException("A defect in the guard's input path.");

        public event EventHandler<ProtectionInputs>? Published
        {
            add { }
            remove { }
        }
    }
}
