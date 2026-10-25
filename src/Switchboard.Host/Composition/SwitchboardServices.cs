using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Health;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Host.Hosting;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Persistence;
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
            return new MqttTopicGuard(mqtt.DiscoveryPrefix, mqtt.BaseTopic, DeclaredDevices(sp));
        });
        services.AddSingleton<IMqttTransport, MqttNetTransport>();
        services.AddSingleton<MqttLink>();
        services.AddSingleton<IHealthPublisher>(sp => sp.GetRequiredService<MqttLink>());
        services.AddSingleton<HeartbeatLoop>();

        services.AddSignalPipeline();
        services.AddGuard();
        services.AddPresentation();
        services.AddSetup();

        services.AddSingleton(sp => new IngressAccessPolicy(
            sp.GetRequiredService<IOptions<IngressOptions>>().Value,
            sp.GetRequiredService<RuntimeEnvironment>().DevTrustAllRequests));

        // Started in this order, stopped in reverse: the guard stops first (shutdown safe state
        // recorded, decision log and state.json written), then the heartbeat, then MQTT leaves the
        // entity OFF and offline, then the Home Assistant socket closes; the signal pipeline (which
        // the links feed) starts before them and stops after them.
        services.AddHostedService<SignalStoreService>();
        services.AddHostedService<ConfigurationWatcherService>();
        services.AddHostedService<ReplayService>();
        services.AddHostedService<HomeAssistantLinkService>();
        services.AddHostedService<MqttLinkService>();
        services.AddHostedService<HeartbeatService>();

        // Before the guard: it loads the statistics before the first step, and it stops right after
        // the guard, so the shutdown step is counted before the final save.
        services.AddHostedService<PresentationService>();

        // Stops right after the guard: a self-test session ends and the read-only discovery link closes.
        services.AddHostedService<SetupLinkService>();
        services.AddHostedService<GuardService>();
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
        services.AddValidatedOptions<PersistenceOptions, PersistenceOptionsValidator>(PersistenceOptions.SectionName);
        services.AddValidatedOptions<GuardOptions, GuardOptionsValidator>(GuardOptions.SectionName);
        services.AddValidatedOptions<StatisticsOptions, StatisticsOptionsValidator>(StatisticsOptions.SectionName);
        services.AddValidatedOptions<SetupOptions, SetupOptionsValidator>(SetupOptions.SectionName);
        return services;
    }

    /// <summary>
    /// The guard and dispatch (on the signal clock, like the store they read), the effector gate that
    /// only records, the kill switch, the overrides, the mode, the decision log and runtime state in
    /// the data folder, and the guard's and dispatch's MQTT entities. Nothing here holds a Home
    /// Assistant command sender, and no command port is registered.
    /// </summary>
    private static IServiceCollection AddGuard(this IServiceCollection services)
    {
        // A fresh random boot id per process: decision ids stay unique across restarts and clock steps.
        services.AddSingleton(_ => new DecisionIdGenerator(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))));
        services.AddSingleton<GuardInbox>();
        services.AddSingleton<LockoutClocks>();
        services.AddSingleton<ILockoutView>(sp => sp.GetRequiredService<LockoutClocks>());
        services.AddSingleton<DeviceTargets>();
        services.AddSingleton<IInFlightView>(sp => sp.GetRequiredService<DeviceTargets>());
        services.AddSingleton<GuardEngine>();
        services.AddSingleton<DispatchEngine>();

        // The gate is built through its public constructor, which takes no command port: this
        // version commands nothing, and no command port is registered anywhere in production.
        services.AddSingleton(sp => new EffectorSettings(sp.GetRequiredService<IOptions<GuardOptions>>().Value.DeviceHistoryLength));
        services.AddSingleton<EffectorGate>();
        services.AddSingleton<DeviceOverrides>();
        services.AddSingleton<IDeviceOverrideControl>(sp => sp.GetRequiredService<DeviceOverrides>());
        services.AddSingleton<IDeviceOverrideView>(sp => sp.GetRequiredService<DeviceOverrides>());
        services.AddSingleton<KillSwitchState>();
        services.AddSingleton<IKillSwitchControl>(sp => sp.GetRequiredService<KillSwitchState>());
        services.AddSingleton<IKillSwitchView>(sp => sp.GetRequiredService<KillSwitchState>());

        services.AddSingleton<IRuntimeStateStore>(sp => new RuntimeStateFile(
            sp.GetRequiredService<RuntimeEnvironment>().DataPath,
            sp.GetRequiredService<IOptions<PersistenceOptions>>().Value,
            sp.GetRequiredService<ILogger<RuntimeStateFile>>()));
        services.AddSingleton(sp => sp.GetRequiredService<IRuntimeStateStore>().Load());
        services.AddSingleton(sp => new RuntimeStateCoordinator(
            sp.GetRequiredService<IRuntimeStateStore>(),
            sp.GetRequiredService<RuntimeStateLoad>(),
            () => sp.GetRequiredService<GuardEngine>().Setup?.Budget,
            sp.GetRequiredService<IOptions<PersistenceOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RuntimeStateCoordinator>>()));
        services.AddSingleton<IDiscoveryLedger>(sp => sp.GetRequiredService<RuntimeStateCoordinator>());
        services.AddSingleton<ILastKnownStates>(sp => sp.GetRequiredService<RuntimeStateCoordinator>());

        // The add-on option is the ceiling; the UI choice persisted in state.json counts only under it.
        // A change of the UI choice wakes the guard, which records it (MODE-02).
        services.AddSingleton(sp => new ModeState(
            sp.GetRequiredService<ResolvedAddonOptions>().Mode,
            sp.GetRequiredService<RuntimeStateLoad>().State.UiMode,
            sp.GetRequiredService<GuardInbox>()));
        services.AddSingleton<IModeSource>(sp => sp.GetRequiredService<ModeState>());
        services.AddSingleton(sp => new GuardStatusHolder(
            GuardStatus.Initial(sp.GetRequiredService<SignalClock>().Time.GetUtcNow(), sp.GetRequiredService<IModeSource>().Current),
            sp.GetRequiredService<ILogger<GuardStatusHolder>>()));
        services.AddSingleton<IGuardStatusSource>(sp => sp.GetRequiredService<GuardStatusHolder>());
        services.AddSingleton<IGuardEnvelopeSource>(sp => sp.GetRequiredService<GuardStatusHolder>());
        services.AddSingleton(sp => new DispatchStatusHolder(
            DispatchStatus.Initial(sp.GetRequiredService<SignalClock>().Time.GetUtcNow(), sp.GetRequiredService<IModeSource>().Current),
            sp.GetRequiredService<ILogger<DispatchStatusHolder>>()));
        services.AddSingleton<IDispatchStatusSource>(sp => sp.GetRequiredService<DispatchStatusHolder>());

        services.AddSingleton<IDecisionFileSystem, DecisionFileSystem>();
        services.AddSingleton(sp =>
        {
            var diagnostics = sp.GetRequiredService<ISignalDiagnosticsSource>();
            return new JsonlDecisionLog(
                sp.GetRequiredService<IDecisionFileSystem>(),
                sp.GetRequiredService<RuntimeEnvironment>().DataPath,
                sp.GetRequiredService<IOptions<PersistenceOptions>>().Value,
                () => diagnostics.Current.Configuration?.Configuration?.Control.DecisionLog.RetentionDays,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<JsonlDecisionLog>>());
        });
        services.AddSingleton<IDecisionRecorder>(sp => sp.GetRequiredService<JsonlDecisionLog>());
        services.AddSingleton<GuardRunner>();

        services.AddSingleton(sp =>
        {
            var diagnostics = sp.GetRequiredService<ISignalDiagnosticsSource>();
            return new GuardEntities(
                sp.GetRequiredService<IGuardStatusSource>(),
                sp.GetRequiredService<IDecisionRecorder>(),
                sp.GetRequiredService<IKillSwitchControl>(),
                sp.GetRequiredService<IDiscoveryLedger>(),
                () => diagnostics.Current.Snapshot.Connection?.Voltage,
                sp.GetRequiredService<IOptions<MqttOptions>>(),
                sp.GetRequiredService<AppVersion>(),
                sp.GetRequiredService<ILogger<GuardEntities>>());
        });
        services.AddSingleton<IMqttEntitySource>(sp => sp.GetRequiredService<GuardEntities>());
        services.AddSingleton(sp => new DispatchEntities(
            sp.GetRequiredService<IDispatchStatusSource>(),
            sp.GetRequiredService<IDeviceOverrideControl>(),
            sp.GetRequiredService<IDiscoveryLedger>(),
            DeclaredDevices(sp),
            sp.GetRequiredService<IOptions<MqttOptions>>(),
            sp.GetRequiredService<AppVersion>(),
            sp.GetRequiredService<ILogger<DispatchEntities>>()));
        services.AddSingleton<IMqttEntitySource>(sp => sp.GetRequiredService<DispatchEntities>());
        return services;
    }

    /// <summary>
    /// The runtime UI's read side: the presentation hub (a read-only listener of the guard runner),
    /// statistics.json and excursions.json in the data folder, and their writer. Nothing here holds a
    /// command port, the kill switch or the overrides.
    /// </summary>
    private static IServiceCollection AddPresentation(this IServiceCollection services)
    {
        services.AddSingleton<PresentationHub>();
        services.AddSingleton<IPresentationSource>(sp => sp.GetRequiredService<PresentationHub>());
        services.AddSingleton<IPresentationStore<StatisticsState>>(sp => new StatisticsFile(
            sp.GetRequiredService<RuntimeEnvironment>().DataPath,
            sp.GetRequiredService<IOptions<StatisticsOptions>>().Value,
            sp.GetRequiredService<ILogger<StatisticsFile>>()));
        services.AddSingleton<IPresentationStore<ExcursionState>>(sp => new ExcursionFile(
            sp.GetRequiredService<RuntimeEnvironment>().DataPath,
            sp.GetRequiredService<IOptions<StatisticsOptions>>().Value,
            sp.GetRequiredService<ILogger<ExcursionFile>>()));
        services.AddSingleton<PresentationPersistence>();
        return services;
    }

    /// <summary>
    /// The devices of the current valid configuration (null while there is none): the only devices
    /// an override can address and whose override command topic the MQTT guard lets in (SAFE-05).
    /// Read from the published snapshot, not the guard's input port, so a defect there cannot take
    /// the MQTT link (and with it the health entity going OFF) down too.
    /// </summary>
    private static Func<IReadOnlyCollection<DeviceName>?> DeclaredDevices(IServiceProvider sp) => () =>
        sp.GetRequiredService<ISignalDiagnosticsSource>().Current.Snapshot.Protection.Setup is { } setup ? [.. setup.Devices.Select(d => d.Id)] : null;

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
        services.AddSingleton<IControlInputSource>(sp => sp.GetRequiredService<SignalSnapshotHolder>());
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
