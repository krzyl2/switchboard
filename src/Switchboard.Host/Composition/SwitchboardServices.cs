using System.Reflection;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Health;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.Signals;
using Switchboard.Host.Hosting;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Replay;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Host.Composition;

public static class SwitchboardServices
{
    private const string SupervisorHttpClient = "supervisor";

    /// <summary>
    /// Everything is resolved lazily from the container, so tests can replace configuration and
    /// the environment before anything is read.
    /// </summary>
    public static IServiceCollection AddSwitchboard(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => RuntimeEnvironment.Read(key => sp.GetRequiredService<IConfiguration>()[key]));
        services.AddSingleton(sp =>
        {
            var environment = sp.GetRequiredService<RuntimeEnvironment>();
            return AddonOptionsResolver.Resolve(
                AddonOptionsFile.TryRead(environment.OptionsPath),
                environment.ModeOverride,
                environment.LogLevelOverride);
        });
        services.AddSingleton(AppVersion.FromInformationalVersion(
            typeof(SwitchboardServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));

        services.AddSingleton<IConfigureOptions<LoggerFilterOptions>>(sp => new ConfigureOptions<LoggerFilterOptions>(
            filter => filter.MinLevel = ToLogLevel(sp.GetRequiredService<ResolvedAddonOptions>().LogLevel)));

        services.AddSwitchboardOptions();

        services.AddSingleton<HomeAssistantConnectionState>();
        services.AddSingleton<IHomeAssistantConnectionState>(sp => sp.GetRequiredService<HomeAssistantConnectionState>());
        services.AddSingleton<MqttConnectionState>();
        services.AddSingleton<IMqttConnectionState>(sp => sp.GetRequiredService<MqttConnectionState>());
        services.AddSingleton<IHealthStatus, HealthEvaluator>();

        services.AddSingleton<IWebSocketTransportFactory, ClientWebSocketTransportFactory>();
        services.AddSingleton(sp => HomeAssistantEndpointResolution.Resolve(
            sp.GetRequiredService<RuntimeEnvironment>(),
            sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value));

        services.AddHttpClient(SupervisorHttpClient);
        services.AddSingleton<IMqttCredentialSource>(sp => ActivatorUtilities.CreateInstance<MqttCredentialSource>(
            sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(SupervisorHttpClient)));
        services.AddSingleton(sp =>
        {
            var mqtt = sp.GetRequiredService<IOptions<MqttOptions>>().Value;
            return new MqttTopicGuard(mqtt.DiscoveryPrefix, mqtt.BaseTopic);
        });
        services.AddSingleton<IMqttTransport, MqttNetTransport>();
        services.AddSingleton<MqttLink>();
        services.AddSingleton<IHealthPublisher>(sp => sp.GetRequiredService<MqttLink>());
        services.AddSingleton<HeartbeatLoop>();

        services.AddSignalPipeline();

        services.AddSingleton(sp => new IngressAccessPolicy(
            sp.GetRequiredService<IOptions<IngressOptions>>().Value,
            sp.GetRequiredService<RuntimeEnvironment>().DevTrustAllRequests));

        // Started in this order, stopped in reverse: the heartbeat stops first, then MQTT leaves the
        // entity OFF and offline, then the Home Assistant socket closes; the signal pipeline (which
        // the links feed) starts before them and stops after them.
        services.AddHostedService<SignalStoreService>();
        services.AddHostedService<ConfigurationWatcherService>();
        services.AddHostedService<ReplayService>();
        services.AddHostedService<HomeAssistantLinkService>();
        services.AddHostedService<MqttLinkService>();
        services.AddHostedService<HeartbeatService>();
        return services;
    }

    /// <summary>Binds every parameter section and refuses to start on a missing or nonsensical value.</summary>
    public static IServiceCollection AddSwitchboardOptions(this IServiceCollection services)
    {
        services.AddValidatedOptions<IngressOptions, IngressOptionsValidator>(IngressOptions.SectionName);
        services.AddValidatedOptions<HomeAssistantOptions, HomeAssistantOptionsValidator>(HomeAssistantOptions.SectionName);
        services.AddValidatedOptions<MqttOptions, MqttOptionsValidator>(MqttOptions.SectionName);
        services.AddValidatedOptions<HeartbeatOptions, HeartbeatOptionsValidator>(HeartbeatOptions.SectionName);
        services.AddValidatedOptions<ConfigurationOptions, ConfigurationOptionsValidator>(ConfigurationOptions.SectionName);
        return services;
    }

    /// <summary>
    /// Signal source (Home Assistant or development replay), configuration watcher, store, runner and
    /// the three read ports. The replay clock is registered only as the signal clock: nothing outside
    /// the signal pipeline ever runs on it.
    /// </summary>
    private static IServiceCollection AddSignalPipeline(this IServiceCollection services)
    {
        services.AddSingleton(sp => SignalSourceSelection.Resolve(sp.GetRequiredService<RuntimeEnvironment>()));
        services.AddSingleton(sp => ReplayData.Load(
            sp.GetRequiredService<SignalSourceSelection>().ReplayPath
            ?? throw new InvalidOperationException("Replay data requested outside replay mode.")));
        services.AddSingleton(sp =>
        {
            var selection = sp.GetRequiredService<SignalSourceSelection>();
            var time = sp.GetRequiredService<TimeProvider>();
            return new SignalClock(selection.IsReplay
                ? new ReplayTimeProvider(time, sp.GetRequiredService<ReplayData>().Start, selection.ReplaySpeed)
                : time);
        });

        services.AddSingleton<SignalInbox>();
        services.AddSingleton(sp => new SignalStoreSettings(
            sp.GetRequiredService<SignalSourceSelection>().IsReplay
                ? TimeSpan.Zero
                : sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value.StateReadTimeout));
        services.AddSingleton(sp => new SignalStore(sp.GetRequiredService<SignalStoreSettings>()));
        services.AddSingleton(sp =>
        {
            // Until the runner publishes its first cycle: nothing configured, nothing valid.
            var empty = new SignalStore(sp.GetRequiredService<SignalStoreSettings>())
                .Snapshot(sp.GetRequiredService<SignalClock>().Time.GetUtcNow());
            return new SignalSnapshotHolder(new SignalDiagnostics(empty, null, [], []));
        });
        services.AddSingleton<IProtectionInputSource>(sp => sp.GetRequiredService<SignalSnapshotHolder>());
        services.AddSingleton<IDispatchInputSource>(sp => sp.GetRequiredService<SignalSnapshotHolder>());
        services.AddSingleton<ISignalDiagnosticsSource>(sp => sp.GetRequiredService<SignalSnapshotHolder>());
        services.AddSingleton<SignalStoreRunner>();

        // The freshness probe carries the HA token: its own client, never redirected, timed out on the clock.
        services.AddHttpClient(HomeAssistantStateReader.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IEntityStateReader>(sp =>
        {
            var endpoint = sp.GetRequiredService<HomeAssistantEndpointResolution>().Endpoint;
            if (endpoint?.RestBaseUri is not { } restBase)
            {
                return new UnavailableEntityStateReader();
            }

            return new HomeAssistantStateReader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HomeAssistantStateReader.HttpClientName),
                restBase,
                endpoint.AccessToken,
                sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value.StateReadTimeout,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<HomeAssistantStateReader>>());
        });
        services.AddSingleton<HomeAssistantSignalSource>();
        services.AddSingleton<IEntitySubscriptionSink>(sp => sp.GetRequiredService<HomeAssistantSignalSource>());
        services.AddSingleton<ReplaySignalSource>();
        services.AddSingleton<ISignalSource>(sp => sp.GetRequiredService<SignalSourceSelection>().IsReplay
            ? sp.GetRequiredService<ReplaySignalSource>()
            : sp.GetRequiredService<HomeAssistantSignalSource>());

        services.AddSingleton(sp => new ConfigurationFileReader(
            sp.GetRequiredService<RuntimeEnvironment>().ConfigPath,
            sp.GetRequiredService<IOptions<ConfigurationOptions>>().Value.MaxFileBytes,
            EmbeddedControlDefaults.Load()));
        services.AddSingleton<ConfigurationFileWatcher>();
        return services;
    }

    private static void AddValidatedOptions<TOptions, TValidator>(this IServiceCollection services, string section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>()
            .BindConfiguration(section, binder => binder.ErrorOnUnknownConfiguration = true)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
    }

    private static LogLevel ToLogLevel(AddonLogLevel level) => level switch
    {
        AddonLogLevel.Debug => LogLevel.Debug,
        AddonLogLevel.Warning => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}
