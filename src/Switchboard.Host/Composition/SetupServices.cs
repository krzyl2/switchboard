using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Options;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Infrastructure.Replay;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Host.Composition;

/// <summary>The control parameters shipped in control-defaults.yaml alone (no document); the wizard's fallback.</summary>
public sealed record ShippedControl(ControlParameters Parameters);

/// <summary>
/// The wizard's narrow view of the running add-on, read from the published signal diagnostics and,
/// for the devices Switchboard holds reduced, the published dispatch status (read-only).
/// </summary>
public sealed class DiagnosticsRuntimeView(ISignalDiagnosticsSource diagnostics, IDispatchStatusSource? dispatch = null) : ISetupRuntimeView
{
    public SwitchboardConfiguration? Configuration => diagnostics.Current.Configuration?.Configuration;

    public IReadOnlyCollection<DeviceName> ReducedBySwitchboard =>
        dispatch is null ? [] : [.. dispatch.Current.Devices.Where(d => d.Baseline is { Commanded: true }).Select(d => d.Id)];

    public IReadOnlyDictionary<EntityId, TimeSpan> DetectedRefresh
    {
        get
        {
            var current = diagnostics.Current;
            var refresh = new Dictionary<EntityId, TimeSpan>();
            foreach (var reading in current.Snapshot.Signals)
            {
                if (reading.Name is { } name && reading.DetectedRefresh is { } interval && current.Entities.TryGetValue(name, out var entity))
                {
                    refresh.TryAdd(entity, interval);
                }
            }

            return refresh;
        }
    }
}

/// <summary>Where the wizard's discovery and readouts come from: the read-only discovery link, the development replay fixtures, or nothing.</summary>
public sealed record SetupSources(IEntityDiscoverySource Discovery, ICandidateReadouts Readouts);

/// <summary>
/// The setup wizard's backend (Stage 5). It reads Home Assistant only through the read-only
/// discovery link, writes only the configuration file (with backups next to it), and holds no
/// command port, kill switch or override control: the active phase self-test asks the effector gate
/// through the self-test slot, below protection, like any other ordinary change. In development replay with
/// SWITCHBOARD_DEV_DISCOVERY, discovery and readouts come from fixtures instead; that setting is
/// refused in add-on mode and without replay, like replay itself.
/// </summary>
public static class SetupServices
{
    public static IServiceCollection AddSetup(this IServiceCollection services)
    {
        services.AddSingleton(_ => EmbeddedSetupDefaults.Load());
        services.AddSingleton<ISetupRuntimeView>(sp => new DiagnosticsRuntimeView(sp.GetRequiredService<ISignalDiagnosticsSource>(), sp.GetRequiredService<IDispatchStatusSource>()));
        services.AddSingleton(_ => new ShippedControl(
            SwitchboardDocumentParser.BindControl(null, EmbeddedControlDefaults.Load())
            ?? throw new InvalidOperationException("The embedded control defaults do not bind.")));
        services.AddSingleton(sp => new PhaseEvidenceStore(sp.GetRequiredService<IOptions<SetupOptions>>().Value.EvidencePerDevice));
        services.AddSingleton<IPhaseEvidenceFile>(sp => new PhaseEvidenceFile(
            sp.GetRequiredService<RuntimeEnvironment>().DataPath,
            sp.GetRequiredService<IOptions<SetupOptions>>().Value,
            sp.GetRequiredService<ILogger<PhaseEvidenceFile>>()));
        services.AddSingleton<PhaseEvidencePersistence>();
        services.AddSingleton<ActivePhaseTestService>();
        services.AddSingleton<ConfigurationWarningsHolder>();
        services.AddSingleton(sp => new RuntimeDerivedCheckRunner(
            sp.GetRequiredService<EntityDiscoveryCache>(),
            sp.GetRequiredService<ISetupRuntimeView>(),
            sp.GetRequiredService<IControlInputSource>(),
            sp.GetRequiredService<SetupDefaults>(),
            sp.GetRequiredService<ConfigurationWarningsHolder>(),
            sp.GetRequiredService<IOptions<SetupOptions>>().Value.DiscoveryCacheTtl,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RuntimeDerivedCheckRunner>>()));
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SetupOptions>>().Value;
            return new CandidateReadoutStore(options.ReadoutHistoryLength, options.MaxRetainedReadouts, sp.GetRequiredService<ShippedControl>().Parameters.Signals.Refresh);
        });
        services.AddSingleton(Sources);
        services.AddSingleton(sp => sp.GetRequiredService<SetupSources>().Discovery);
        services.AddSingleton(sp => sp.GetRequiredService<SetupSources>().Readouts);
        services.AddSingleton(sp => new EntityDiscoveryCache(
            sp.GetRequiredService<IEntityDiscoverySource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<SetupOptions>>().Value.DiscoveryCacheTtl));
        services.AddSingleton<ISetupDocumentStore>(sp => new ConfigurationDocumentStore(
            sp.GetRequiredService<ConfigurationFileReader>(),
            sp.GetRequiredService<IOptions<SetupOptions>>().Value.MaxBackups,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ConfigurationDocumentStore>>()));
        services.AddSingleton<ISetupDocumentCodec>(sp => new SetupDocumentCodec(sp.GetRequiredService<ConfigurationFileReader>(), EmbeddedControlDefaults.Load()));
        services.AddSingleton<IConfigurationReloads>(sp => sp.GetRequiredService<ConfigurationFileWatcher>());
        services.AddSingleton(sp => new SetupService(
            sp.GetRequiredService<ISetupDocumentStore>(),
            sp.GetRequiredService<ISetupDocumentCodec>(),
            sp.GetRequiredService<IConfigurationReloads>(),
            sp.GetRequiredService<EntityDiscoveryCache>(),
            sp.GetRequiredService<ICandidateReadouts>(),
            sp.GetRequiredService<PhaseEvidenceStore>(),
            sp.GetRequiredService<ISetupRuntimeView>(),
            sp.GetRequiredService<SetupDefaults>(),
            sp.GetRequiredService<ShippedControl>().Parameters,
            sp.GetRequiredService<SignalClock>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<SetupOptions>>(),
            sp.GetRequiredService<ILogger<SetupService>>()));
        services.AddSingleton(sp =>
        {
            var diagnostics = sp.GetRequiredService<ISignalDiagnosticsSource>();
            var shipped = sp.GetRequiredService<ShippedControl>().Parameters;
            return new PhaseSelfTestService(
                sp.GetRequiredService<ICandidateReadouts>(),
                sp.GetRequiredService<PhaseEvidenceStore>(),
                sp.GetRequiredService<SetupDefaults>(),
                () => (diagnostics.Current.Configuration?.Configuration?.Control ?? shipped).Signals.CurrentLagPolls,
                sp.GetRequiredService<SignalClock>(),
                sp.GetRequiredService<ILogger<PhaseSelfTestService>>());
        });
        services.AddSingleton(sp => new PhaseEvidenceRecorder(
            sp.GetRequiredService<PhaseEvidenceStore>(),
            sp.GetRequiredService<ISetupRuntimeView>(),
            sp.GetRequiredService<SetupDefaults>(),
            sp.GetRequiredService<IOptions<SetupOptions>>().Value.ReadoutHistoryLength,
            sp.GetRequiredService<ActivePhaseTestService>()));
        services.AddSingleton<IDecisionHistory>(sp =>
        {
            var diagnostics = sp.GetRequiredService<ISignalDiagnosticsSource>();
            var shipped = sp.GetRequiredService<ShippedControl>().Parameters;
            return new JsonlDecisionHistory(
                sp.GetRequiredService<IDecisionFileSystem>(),
                sp.GetRequiredService<RuntimeEnvironment>().DataPath,
                sp.GetRequiredService<IOptions<PersistenceOptions>>().Value,
                () => diagnostics.Current.Configuration?.Configuration?.Control.DecisionLog.RetentionDays ?? shipped.DecisionLog.RetentionDays,
                sp.GetRequiredService<TimeProvider>());
        });
        return services;
    }

    private static SetupSources Sources(IServiceProvider sp)
    {
        var environment = sp.GetRequiredService<RuntimeEnvironment>();
        var selection = sp.GetRequiredService<SignalSourceSelection>();
        var options = sp.GetRequiredService<IOptions<SetupOptions>>().Value;
        if (environment.DevDiscoveryPath is { } fixtures)
        {
            if (environment.IsAddon || !selection.IsReplay)
            {
                throw new InvalidOperationException(
                    $"{EnvironmentVariableNames.DevDiscovery} is for standalone development replay only; refusing to start with it set {(environment.IsAddon ? "as an add-on" : $"without {EnvironmentVariableNames.DevReplay}")}.");
            }

            var fixture = ReplayDiscoveryFixture.Load(fixtures);
            var readouts = new ReplayCandidateReadouts(
                sp.GetRequiredService<ReplayData>(),
                fixture,
                sp.GetRequiredService<SignalClock>(),
                sp.GetRequiredService<ShippedControl>().Parameters.Signals.Refresh,
                options.ReadoutHistoryLength);
            return new SetupSources(new ReplayDiscoverySource(fixture, readouts, sp.GetRequiredService<SignalClock>(), options.MaxDiscoveredEntities), readouts);
        }

        if (selection.IsReplay || sp.GetRequiredService<HomeAssistantEndpointResolution>().Endpoint is not { } endpoint)
        {
            var nothing = new UnavailableSetupSource();
            return new SetupSources(nothing, nothing);
        }

        var link = new HomeAssistantDiscoveryLink(
            sp.GetRequiredService<IWebSocketTransportFactory>(),
            endpoint,
            sp.GetRequiredService<IOptions<HomeAssistantOptions>>(),
            sp.GetRequiredService<IOptions<SetupOptions>>(),
            sp.GetRequiredService<CandidateReadoutStore>(),
            sp.GetRequiredService<IEntityStateReader>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<SignalClock>(),
            sp.GetRequiredService<ILoggerFactory>());
        return new SetupSources(link, link);
    }
}
