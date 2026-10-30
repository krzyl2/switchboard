using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Guard;
using Switchboard.Application.Health;
using Switchboard.Application.Signals;
using Switchboard.Host.Composition;
using Switchboard.Host.Hosting;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// Hosted services stop in reverse registration order, and that order is the shutdown contract:
/// the guard first (shutdown safe state recorded and written while the links are still up), then
/// the presentation (the shutdown step counted, statistics written), the heartbeat (no more ON),
/// then MQTT (OFF, offline, disconnect), then the HA socket.
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
        Assert.NotNull(provider.GetRequiredService<GuardRunner>());
        Assert.NotNull(provider.GetRequiredService<JsonlDecisionLog>());
        Assert.NotNull(provider.GetRequiredService<Switchboard.Application.Presentation.PresentationPersistence>());
        Assert.Equal(15, provider.GetServices<IHostedService>().Count());
    }

    [Fact]
    public void Links_start_in_order_so_they_stop_guard_first_then_heartbeat_then_mqtt_then_home_assistant()
    {
        // The signal pipeline the links feed starts first and stops last; the guard stops before
        // everything, so its shutdown safe state is recorded while MQTT can still report it. The
        // EV module's loop is a proposer like the planner: it stops right after the guard.
        using var provider = Build();

        Assert.Equal(
            [
                typeof(SignalStoreService), typeof(ConfigurationWatcherService), typeof(ReplayService),
                typeof(HomeAssistantLinkService), typeof(MqttLinkService), typeof(HeartbeatService),
                typeof(PresentationService), typeof(SetupLinkService), typeof(RuntimeDerivedCheckService),
                typeof(PhaseEvidenceService), typeof(HourlyBalanceService), typeof(PlannerHostedService), typeof(EvSnapshotHostedService), typeof(CommandPathService), typeof(GuardService),
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

    [Fact]
    public async Task The_planner_is_idle_without_a_planner_section_and_stands_aside_until_the_guard_has_evaluated()
    {
        // Why: no planner: section is v2.0 exactly (idle service, empty holder, no timer to wait for); and before the guard's first evaluation (its status is the
        // initial safe state) the planner is suspended, so it can never plan from inputs the guard has not yet vetted.
        using var provider = Build();
        var service = provider.GetRequiredService<Switchboard.Application.Planning.PlannerService>();
        var holder = provider.GetRequiredService<Switchboard.Application.Planning.PlanHolder>();

        var wait = await service.StepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Timeout.InfiniteTimeSpan, wait);
        Assert.Equal(Switchboard.Application.Planning.PlanState.Empty, holder.Current);
        Assert.True(provider.GetRequiredService<Switchboard.Application.Planning.IPlannerSuspension>().Suspended);
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is PlannerHostedService);
    }

    [Fact]
    public void Dispatch_reads_the_plan_only_through_a_view_that_is_off_without_a_planner_section()
    {
        // Why: PLAN-13/14. The engine is built with the plan view (its one door to the plan), and without a planner: section the view says off and hands out no entry, so
        // a 0.10 file decides as v2 whatever the holder contains; the view and the planner share one holder and one configuration revision (the host's edge).
        using var provider = Build();

        var view = provider.GetRequiredService<Switchboard.Application.Planning.IPlanView>();

        Assert.IsType<Switchboard.Application.Planning.PlanView>(view);
        Assert.Equal(Switchboard.Domain.Planning.PlannerDispatchMode.Off, view.Mode);
        Assert.Null(view.For(Switchboard.Domain.Devices.DeviceName.Parse("ev_charger"), DateTimeOffset.UnixEpoch));
        Assert.NotNull(provider.GetRequiredService<Switchboard.Application.Dispatch.DispatchEngine>());
        Assert.Same(provider.GetRequiredService<Switchboard.Host.Composition.DiagnosticsPlannerSource>(), provider.GetRequiredService<Switchboard.Application.Planning.IPlannerSource>());
    }

    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoPaths.Combine("src", "Switchboard.Host", "appsettings.json"), optional: false)
            .AddInMemoryCollection([
                new("SWITCHBOARD_OPTIONS_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-options.json")),
                new("SWITCHBOARD_CONFIG_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-config.yaml")),
                new("SWITCHBOARD_DATA_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-data")),
            ])
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.ClearProviders());
        // Provided by the host itself in production (GuardService stops the add-on through it).
        services.AddSingleton<IHostApplicationLifetime, Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>();
        services.AddSwitchboard();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
