using System.Collections.Frozen;
using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// The signal set of one valid configuration and every table derived from it, built once and never
/// changed: a reload makes a new map and the old one is simply dropped, an invalid or missing
/// document is <see cref="Empty"/>. The runtimes it lists are the mutable per-signal state (a kept
/// signal is the same runtime in the old and the new map, so its samples survive a reload).
/// </summary>
internal sealed class SignalMap
{
    private readonly FrozenDictionary<EntityId, ImmutableArray<SignalRuntime>> _byEntity;
    private readonly FrozenDictionary<SignalName, SignalRuntime> _byName;

    private SignalMap()
    {
        Signals = [];
        _byEntity = FrozenDictionary<EntityId, ImmutableArray<SignalRuntime>>.Empty;
        _byName = FrozenDictionary<SignalName, SignalRuntime>.Empty;
        Dependents = ImmutableDictionary<SignalName, ImmutableArray<DeviceName>>.Empty;
        DeviceStates = [];
        CurrentDraws = [];
        PowerDraws = [];
        DerivedDraws = [];
        GroupPhases = [];
    }

    private SignalMap(SwitchboardConfiguration configuration, ImmutableArray<SignalRuntime> signals)
    {
        Configuration = configuration;
        Setup = ProtectionSetupFactory.Build(configuration);
        DispatchSetup = DispatchSetupFactory.Build(configuration);
        Dependents = DependentsOf(configuration);
        DeviceStates =
        [
            .. configuration.Devices.SelectMany(d => d.Stages
                .Select(s => (d.Id, s.Index, s.StateSignal, d.Type == DeviceType.Storage, (StopOptions?)null))
                .Concat(d.StopSignal is { } stop ? [(d.Id, DeviceStateSample.StopStage, stop, false, d.StopOptions)] : [])),
        ];
        CurrentDraws = [.. configuration.Devices.Where(d => d.Draw is not null).SelectMany(d => d.Draw!.Current.OrderBy(c => c.Key).Select(c => (d.Id, c.Key, c.Value)))];
        PowerDraws = [.. configuration.Devices.Where(d => d.Draw is not null).SelectMany(d => d.Draw!.Power.OrderBy(c => c.Key).Select(c => (d.Id, c.Key, c.Value)))];
        DerivedDraws = [.. configuration.Devices.SelectMany(d => d.DerivedCurrentPhases(configuration.VoltagePhases).Select(p => (d.Id, p, d.Draw!.Power[p])))];
        GroupPhases = [.. configuration.Groups.SelectMany(g => g.Phases.Select(p => (g.Id, p, g.Current.TryGetValue(p, out var name) ? name : (SignalName?)null)))];
        Signals = signals;
        _byEntity = signals.GroupBy(s => s.Definition.Entity).ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
        _byName = signals.ToFrozenDictionary(s => s.Definition.Name);
    }

    /// <summary>No signal, no setup, nothing to protect with: the state of a missing or invalid document.</summary>
    public static SignalMap Empty { get; } = new();

    /// <summary>The valid configuration the map was built from; null for <see cref="Empty"/>.</summary>
    public SwitchboardConfiguration? Configuration { get; }

    public ImmutableArray<SignalRuntime> Signals { get; }

    public ProtectionSetup? Setup { get; }

    public DispatchSetup? DispatchSetup { get; }

    public ImmutableDictionary<SignalName, ImmutableArray<DeviceName>> Dependents { get; }

    public ImmutableArray<(DeviceName Device, int Stage, SignalName Signal, bool Storage, StopOptions? Select)> DeviceStates { get; }

    public ImmutableArray<(DeviceName Device, Phase Phase, SignalName Signal)> CurrentDraws { get; }

    public ImmutableArray<(DeviceName Device, Phase Phase, SignalName Signal)> PowerDraws { get; }

    /// <summary>The power draws that stand in for a current the device does not declare (see <see cref="DeviceDefinition.DerivedCurrentPhases"/>).</summary>
    public ImmutableArray<(DeviceName Device, Phase Phase, SignalName Signal)> DerivedDraws { get; }

    public ImmutableArray<(GroupName Group, Phase Phase, SignalName? Signal)> GroupPhases { get; }

    public DirectionParameters Direction => Configuration!.Control.Signals.Direction;

    public static SignalMap Of(SwitchboardConfiguration configuration, ImmutableArray<SignalRuntime> signals) => new(configuration, signals);

    public ImmutableArray<SignalRuntime> Find(EntityId id) => _byEntity.TryGetValue(id, out var found) ? found : [];

    public SignalRuntime ByName(SignalName name) => _byName[name];

    public SignalRuntime? ByNameOrNull(SignalName name) => _byName.GetValueOrDefault(name);

    public SignalRuntime? WithRole(SignalRole role, Phase? phase = null) =>
        Signals.FirstOrDefault(s => s.Definition.Role == role && (phase is null || s.Definition.Phase == phase));

    /// <summary>
    /// SIG-05: the devices that depend on a signal. A phase current protects every device with a
    /// stage on its phase (the summed current protects all of them); a state signal protects its own
    /// device; a group sub-meter protects the group's members. A draw signal lists its device, which
    /// it does not hold off when stale (protection does not need it): a stale draw is no longer
    /// trusted for relief or increases. Direction and power signals only ever cost cautious mode.
    /// </summary>
    private static ImmutableDictionary<SignalName, ImmutableArray<DeviceName>> DependentsOf(SwitchboardConfiguration configuration)
    {
        var devices = configuration.Devices;
        var builder = ImmutableDictionary.CreateBuilder<SignalName, ImmutableArray<DeviceName>>();
        foreach (var signal in configuration.Signals)
        {
            ImmutableArray<DeviceName> dependents = signal.Role switch
            {
                SignalRole.PhaseCurrent when configuration.Connection.Topology != Topology.ThreePhaseSummed =>
                    [.. devices.Where(d => d.Stages.Any(s => s.Phases.Contains(signal.Phase!.Value))).Select(d => d.Id)],
                // Protection reads the total only in three_phase_summed; elsewhere it is display-only.
                SignalRole.TotalCurrent when configuration.Connection.Topology == Topology.ThreePhaseSummed => [.. devices.Select(d => d.Id)],
                SignalRole.DeviceState => [.. devices.Where(d => d.Stages.Any(s => s.StateSignal == signal.Name) || d.StopSignal == signal.Name).Select(d => d.Id)],
                SignalRole.DeviceCurrent or SignalRole.DevicePower =>
                    [.. devices.Where(d => d.Draw is { } draw && (draw.Current.ContainsValue(signal.Name) || draw.Power.ContainsValue(signal.Name))).Select(d => d.Id)],
                SignalRole.GroupCurrent => [.. configuration.Groups.Where(g => g.Current.ContainsValue(signal.Name)).SelectMany(g => g.Members)],
                _ => [],
            };
            builder[signal.Name] = dependents;
        }

        return builder.ToImmutable();
    }
}
