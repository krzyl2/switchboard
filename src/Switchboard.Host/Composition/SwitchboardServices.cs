using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Balance;
using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Application.Goals;
using Switchboard.Application.Guard;
using Switchboard.Application.Health;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.Planning;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;
using Switchboard.Host.Hosting;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Infrastructure.Replay;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Host.Composition;

/// <summary>The development plant, when one is loaded (replay only).</summary>
public sealed record PlantHolder(SimulatedPlant? Plant);

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
        // recorded and, in active mode, sent through the command path while it still runs, decision
        // log and state.json written), then the command path, then the heartbeat, then MQTT leaves
        // the entity OFF and offline, then the Home Assistant socket closes; the signal pipeline
        // (which the links feed) starts before them and stops after them.
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
        services.AddHostedService<RuntimeDerivedCheckService>();
        services.AddHostedService<PhaseEvidenceService>();
        services.AddHostedService<HourlyBalanceService>();
        services.AddHostedService<PlannerHostedService>();

        // The command path's sender runs until the guard has stopped, so the shutdown safe state's
        // commands still leave (bounded by HomeAssistant:ShutdownCommandWait).
        services.AddHostedService<CommandPathService>();
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
    /// The guard and dispatch (on the signal clock, like the store they read), the effector gate with
    /// the one command port (Home Assistant service calls through the outbound allow-list, or in
    /// development replay the development sink), the kill switch, the control switch, the overrides,
    /// the mode, the decision log and runtime state in the data folder, and the guard's and
    /// dispatch's MQTT entities. Only the gate holds the command port.
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
        services.AddSingleton<SelfTestSlot>();

        // The command path: the actuators of the configuration loaded now (published by the signal
        // store runner), the channel (the protection link, or the development sink in replay) and the
        // port, which only the effector gate receives.
        services.AddSingleton<ActuatorDirectoryHolder>();
        services.AddSingleton<IActuatorDirectory>(sp => sp.GetRequiredService<ActuatorDirectoryHolder>());
        services.AddSingleton<HomeAssistantCallServiceChannel>();
        services.AddSingleton(sp => DevPlant(sp));
        services.AddSingleton<IOwnEntityCheck>(sp => new OwnMqttEntities(sp.GetRequiredService<IDiscoveryLedger>()));
        services.AddSingleton<ICallServiceChannel>(sp =>
        {
            // Resolved first, whatever the source: a development plant set as an add-on stops the start.
            var plant = sp.GetRequiredService<PlantHolder>().Plant;
            return sp.GetRequiredService<SignalSourceSelection>().IsReplay
                ? new DevCommandSink(sp.GetRequiredService<SignalClock>(), plant)
                : sp.GetRequiredService<HomeAssistantCallServiceChannel>();
        });
        services.AddSingleton(sp => new HomeAssistantDeviceCommandPort(
            sp.GetRequiredService<ICallServiceChannel>(),
            sp.GetRequiredService<IActuatorDirectory>(),
            sp.GetRequiredService<IModeSource>(),
            sp.GetRequiredService<GuardInbox>(),
            sp.GetRequiredService<IOptions<HomeAssistantOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HomeAssistantDeviceCommandPort>>(),
            sp.GetRequiredService<ControlSwitchState>(),
            sp.GetRequiredService<IOwnEntityCheck>()));
        services.AddSingleton<ICommandPathStatus>(sp => sp.GetRequiredService<HomeAssistantDeviceCommandPort>());
        services.AddSingleton(sp => new EffectorSettings(sp.GetRequiredService<IOptions<GuardOptions>>().Value.DeviceHistoryLength));
        services.AddSingleton(sp => new EffectorGate(
            sp.GetRequiredService<LockoutClocks>(),
            sp.GetRequiredService<DeviceTargets>(),
            sp.GetRequiredService<DecisionIdGenerator>(),
            sp.GetRequiredService<EffectorSettings>(),
            sp.GetRequiredService<ILogger<EffectorGate>>(),
            sp.GetRequiredService<HomeAssistantDeviceCommandPort>(),
            sp.GetRequiredService<SelfTestSlot>()));
        services.AddSingleton<DeviceOverrides>();
        services.AddSingleton<IDeviceOverrideControl>(sp => sp.GetRequiredService<DeviceOverrides>());
        services.AddSingleton<IDeviceOverrideView>(sp => sp.GetRequiredService<DeviceOverrides>());
        services.AddSingleton<KillSwitchState>();
        services.AddSingleton<IKillSwitchControl>(sp => sp.GetRequiredService<KillSwitchState>());
        services.AddSingleton<IKillSwitchView>(sp => sp.GetRequiredService<KillSwitchState>());
        services.AddSingleton<ControlSwitchState>();
        services.AddSingleton<IControlSwitch>(sp => sp.GetRequiredService<ControlSwitchState>());
        services.AddSingleton<IControlSwitchView>(sp => sp.GetRequiredService<ControlSwitchState>());

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

        // The add-on option is the ceiling; the control switch persisted in state.json counts only
        // under it, and only with a valid configuration. A change of the switch wakes the guard,
        // which records the mode decision (MODE-02).
        services.AddSingleton(sp => new ModeState(
            sp.GetRequiredService<ResolvedAddonOptions>().Mode,
            sp.GetRequiredService<IControlSwitchView>(),
            sp.GetRequiredService<IControlInputSource>()));
        services.AddSingleton<IModeSource>(sp => sp.GetRequiredService<ModeState>());
        // Before the first guard step nothing is resolved: the initial status reads no input (a
        // broken input port must fail the guard loop, not the composition) and claims no validity.
        services.AddSingleton(sp => new GuardStatusHolder(
            GuardStatus.Initial(sp.GetRequiredService<SignalClock>().Time.GetUtcNow(), sp.GetRequiredService<IModeSource>().For(false)),
            sp.GetRequiredService<ILogger<GuardStatusHolder>>()));
        services.AddSingleton<IGuardStatusSource>(sp => sp.GetRequiredService<GuardStatusHolder>());
        services.AddSingleton<IGuardEnvelopeSource>(sp => sp.GetRequiredService<GuardStatusHolder>());
        services.AddSingleton(sp => new DispatchStatusHolder(
            DispatchStatus.Initial(sp.GetRequiredService<SignalClock>().Time.GetUtcNow(), sp.GetRequiredService<IModeSource>().For(false)),
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
                sp.GetRequiredService<ILogger<GuardEntities>>(),
                sp.GetRequiredService<IControlSwitch>());
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
        // The planner's load profile: its own file, touched only while a planner: section exists (the feed is a no-op without one).
        services.AddSingleton<IPresentationStore<LoadProfile>>(sp => new LoadProfileFile(
            sp.GetRequiredService<RuntimeEnvironment>().DataPath,
            sp.GetRequiredService<ILogger<LoadProfileFile>>()));
        services.AddSingleton(sp =>
        {
            var diagnostics = sp.GetRequiredService<ISignalDiagnosticsSource>();
            return new LoadProfileFeed(
                sp.GetRequiredService<IPresentationStore<LoadProfile>>(),
                () => diagnostics.Current.Configuration?.Configuration,
                sp.GetRequiredService<SignalClock>());
        });
        services.AddSingleton<PresentationPersistence>();

        // The goal ledger and tracker (PLAN-03, read-only): a listener of the guard runner like the hub, plus the sources the API merges.
        services.AddSingleton<IGoalInputSource, DiagnosticsGoalInputSource>();
        services.AddSingleton<GoalLedger>();
        services.AddSingleton<IGoalSource, ConfigGoalSource>();

        // The manual goals ("want now", PLAN-19): the add-on's own state, a goal source of origin manual; no entity, no command.
        services.AddSingleton<ManualGoalStore>();
        services.AddSingleton<IManualGoalControl>(sp => sp.GetRequiredService<ManualGoalStore>());
        services.AddSingleton<IGoalSource>(sp => sp.GetRequiredService<ManualGoalStore>());
        services.AddSingleton<GoalEvaluator>();
        services.AddSingleton<GoalTracker>();

        // The planner (PLAN-10 to 12): its own loop and holder, reading the signal layer and the goal model, publishing a plan that nothing reads yet. Idle
        // without a planner: section. It names no effector, command port, override or guard type: the one thing it asks of the guard is IPlannerSuspension.
        services.AddSingleton<PlanHolder>();
        services.AddSingleton<DiagnosticsPlannerSource>();
        services.AddSingleton<IPlannerSource>(sp => sp.GetRequiredService<DiagnosticsPlannerSource>());
        services.AddSingleton<IPlannerSuspension, PlannerSuspension>();

        // Dispatch reads the plan only through this view (PLAN-13): behind planner.dispatch (off by default), and a plan that is missing, stale or made for
        // something else is no plan at all (v2 exactly).
        services.AddSingleton<IPlanCurrency, DiagnosticsPlanCurrency>();
        services.AddSingleton<IPlanView, PlanView>();
        services.AddSingleton(sp => new PlannerService(
            sp.GetRequiredService<IPlannerSource>(),
            sp.GetRequiredService<IPlannerSuspension>(),
            sp.GetRequiredService<PlanHolder>(),
            sp.GetRequiredService<SignalClock>().Time,
            sp.GetRequiredService<ILogger<PlannerService>>()));
        services.AddSingleton<IPlanTrigger>(sp => sp.GetRequiredService<PlannerService>());
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
        services.AddSingleton<IBalanceBaselineFile>(sp => new BalanceBaselineFile(sp.GetRequiredService<RuntimeEnvironment>().DataPath, sp.GetRequiredService<ILogger<BalanceBaselineFile>>()));
        services.AddSingleton<HourlyBalanceMonitor>();
        services.AddSingleton<IBalanceSource>(sp => sp.GetRequiredService<HourlyBalanceMonitor>());

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

    /// <summary>
    /// The development plant (SWITCHBOARD_DEV_PLANT): replay and standalone only, refused as an
    /// add-on like replay itself; it answers the development sink's calls in the replayed signals.
    /// </summary>
    private static PlantHolder DevPlant(IServiceProvider sp)
    {
        var environment = sp.GetRequiredService<RuntimeEnvironment>();
        if (environment.DevPlantPath is not { } path)
        {
            return new PlantHolder(null);
        }

        var selection = sp.GetRequiredService<SignalSourceSelection>();
        if (environment.IsAddon || !selection.IsReplay)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableNames.DevPlant} is for standalone development replay only; refusing to start with it set {(environment.IsAddon ? "as an add-on" : $"without {EnvironmentVariableNames.DevReplay}")}.");
        }

        var diagnostics = sp.GetRequiredService<ISignalDiagnosticsSource>();
        var plant = new SimulatedPlant(PlantDefinition.Load(path), sp.GetRequiredService<ReplayData>(), () => diagnostics.Current.Configuration?.Configuration);
        plant.Attach(sp.GetRequiredService<ReplaySignalSource>());
        return new PlantHolder(plant);
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
