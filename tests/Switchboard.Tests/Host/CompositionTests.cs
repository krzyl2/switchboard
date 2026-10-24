using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Health;
using Switchboard.Application.Signals;
using Switchboard.Host.Composition;
using Switchboard.Host.Hosting;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// Hosted services stop in reverse registration order, and that order is the shutdown contract:
/// heartbeat first (no more ON), then MQTT (OFF, offline, disconnect), then the HA socket.
/// </summary>
public sealed class CompositionTests
{
    [Fact]
    public void The_whole_graph_resolves_with_the_shipped_settings()
    {
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<HeartbeatLoop>());
        Assert.NotNull(provider.GetRequiredService<MqttLink>());
        Assert.NotNull(provider.GetRequiredService<SignalStoreRunner>());
        Assert.NotNull(provider.GetRequiredService<ConfigurationFileWatcher>());
        Assert.Equal(6, provider.GetServices<IHostedService>().Count());
    }

    [Fact]
    public void Links_start_in_order_so_they_stop_heartbeat_first_then_mqtt_then_home_assistant()
    {
        // The signal pipeline the links feed starts first and stops last.
        using var provider = Build();

        Assert.Equal(
            [
                typeof(SignalStoreService), typeof(ConfigurationWatcherService), typeof(ReplayService),
                typeof(HomeAssistantLinkService), typeof(MqttLinkService), typeof(HeartbeatService),
            ],
            provider.GetServices<IHostedService>().Select(s => s.GetType()));
    }

    [Fact]
    public void Live_mode_feeds_the_store_from_home_assistant_on_the_host_clock()
    {
        using var provider = Build();

        Assert.IsType<HomeAssistantSignalSource>(provider.GetRequiredService<ISignalSource>());
        Assert.Same(provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<SignalClock>().Time);
    }

    [Fact]
    public void Protection_and_dispatch_ports_expose_only_their_own_inputs()
    {
        // SIG-04 by construction: the narrow ports and the wide diagnostics port are views of one holder.
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<SignalSnapshotHolder>(), provider.GetRequiredService<IProtectionInputSource>());
        Assert.Same(provider.GetRequiredService<SignalSnapshotHolder>(), provider.GetRequiredService<IDispatchInputSource>());
        Assert.Same(provider.GetRequiredService<SignalSnapshotHolder>(), provider.GetRequiredService<ISignalDiagnosticsSource>());
    }

    [Fact]
    public void The_heartbeat_is_published_through_the_mqtt_link()
    {
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<MqttLink>(), provider.GetRequiredService<IHealthPublisher>());
    }

    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoPaths.Combine("src", "Switchboard.Host", "appsettings.json"), optional: false)
            .AddInMemoryCollection([
                new("SWITCHBOARD_OPTIONS_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-options.json")),
                new("SWITCHBOARD_CONFIG_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-config.yaml")),
            ])
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSwitchboard();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
