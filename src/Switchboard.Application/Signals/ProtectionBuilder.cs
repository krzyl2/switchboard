using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Signals;

/// <summary>
/// The protection inputs of one snapshot: currents only (SIG-04), the devices' applied states, their
/// declared current draws and the circuit groups' sub-meters, all as of <c>now</c> and the source's
/// status. Reads the map and nothing else; built per snapshot.
/// </summary>
internal sealed class ProtectionBuilder(SignalMap map, SignalSourceStatus source, DateTimeOffset now)
{
    public ProtectionInputs Build(SwitchboardConfiguration configuration, long sequence)
    {
        var topology = configuration.Connection.Topology;
        TotalProtection? total = null;
        ProtectionInvalidReason? totalReason = null;
        if (topology == Topology.ThreePhaseSummed)
        {
            var totalSignal = map.WithRole(SignalRole.TotalCurrent)!;
            totalReason = ProtectionReason(totalSignal);
            total = new TotalProtection(totalReason is null, totalReason, totalReason is null ? Reading(totalSignal) : null);
        }

        var phases = ImmutableArray.CreateBuilder<PhaseProtection>();
        foreach (var phase in configuration.Connection.Phases)
        {
            var current = map.WithRole(SignalRole.PhaseCurrent, phase);
            var currentReason = current is null ? null : ProtectionReason(current);
            var reason = topology == Topology.ThreePhaseSummed ? totalReason ?? currentReason : currentReason;
            var reading = current is not null && currentReason is null ? Reading(current) : null;
            phases.Add(new PhaseProtection(phase, reason is null, reason, reading));
        }

        return new ProtectionInputs(sequence, now, topology, phases.ToImmutable(), total, map.Setup, DeviceStates(), null)
        {
            DeviceDraws = DeviceDraws(),
            Groups = Groups(),
        };
    }

    /// <summary>The applied state of every declared device stage and stop actuator, from its state signal (SIG-05).</summary>
    private ImmutableArray<DeviceStateSample> DeviceStates()
    {
        var samples = ImmutableArray.CreateBuilder<DeviceStateSample>(map.DeviceStates.Length);
        foreach (var (device, stage, name, storage, select) in map.DeviceStates)
        {
            var signal = map.ByName(name);
            var (state, detail) = SignalFreshness.Classify(signal, now);
            samples.Add(DeviceStateInterpreter.Interpret(
                device,
                stage,
                source,
                state,
                detail,
                state == SignalState.Fresh ? signal.Accepted?.Value : null,
                signal.Accepted?.Effective ?? signal.Raw?.Reported,
                allowNegativeSetpoint: storage,
                select: select));
        }

        return samples.MoveToImmutable();
    }

    /// <summary>The declared current draws (A), for protection; a draw that is not trusted carries no value and is never relief.</summary>
    private ImmutableArray<DeviceDrawSample> DeviceDraws() =>
    [
        .. map.CurrentDraws.Select(d =>
        {
            var signal = map.ByName(d.Signal);
            var trusted = SignalFreshness.DrawTrusted(source, signal, now);
            return new DeviceDrawSample(d.Device, d.Phase, trusted, trusted ? new Amperes(signal.Magnitude()) : null, signal.Accepted?.Effective);
        }),
        .. map.DerivedDraws.Select(d => DerivedDraw(d.Device, d.Phase, map.ByName(d.Signal))),
    ];

    /// <summary>
    /// A device's current from its own power and the phase's voltage: I = P / U, the only place this
    /// conversion happens. Both must be trusted now (the power by the draw rule, the voltage fresh),
    /// else the sample carries no value and is no relief and no basis for an increase, exactly like a
    /// stale draw current. The real current is P / (U·cos φ) and never smaller, so the derived one
    /// understates and every use of it errs on the safe side (see LoadModel).
    /// </summary>
    private DeviceDrawSample DerivedDraw(DeviceName device, Phase phase, SignalRuntime power)
    {
        var voltageSignal = map.WithRole(SignalRole.PhaseVoltage, phase);
        var voltage = voltageSignal is not null && SignalFreshness.IsFresh(voltageSignal, now) ? voltageSignal.Magnitude() : 0;
        var current = SignalFreshness.DrawTrusted(source, power, now) && voltage > 0 ? new Amperes(power.Magnitude() / voltage) : (Amperes?)null;
        return new DeviceDrawSample(device, phase, current is not null, current, power.Accepted?.Effective);
    }

    /// <summary>
    /// Circuit group phases: a sub-meter is read like a phase current (stale, unavailable or missing
    /// means no valid data, and the guard holds the members off); a group without one is estimated by
    /// the guard from its members, and is valid while the source is connected.
    /// </summary>
    private ImmutableArray<GroupProtection> Groups() =>
    [
        .. map.GroupPhases.Select(g =>
        {
            if (g.Signal is not { } name)
            {
                return new GroupProtection(g.Group, g.Phase, source == SignalSourceStatus.Connected, source == SignalSourceStatus.Connected ? null : ProtectionInvalidReason.SourceDisconnected, null, true);
            }

            var signal = map.ByName(name);
            var reason = ProtectionReason(signal);
            return new GroupProtection(g.Group, g.Phase, reason is null, reason, reason is null ? Reading(signal) : null, false);
        }),
    ];

    private ProtectionInvalidReason? ProtectionReason(SignalRuntime signal)
    {
        if (source != SignalSourceStatus.Connected)
        {
            return ProtectionInvalidReason.SourceDisconnected;
        }

        return SignalFreshness.Classify(signal, now) switch
        {
            (SignalState.Fresh, _) => null,
            (SignalState.Stale, _) => ProtectionInvalidReason.CurrentStale,
            (SignalState.Rejected, _) => ProtectionInvalidReason.CurrentRejected,
            (_, SignalDetail.AwaitingFirstSample) => ProtectionInvalidReason.CurrentAwaiting,
            (_, SignalDetail.EntityMissing or SignalDetail.EntityRemoved) => ProtectionInvalidReason.CurrentMissing,
            _ => ProtectionInvalidReason.CurrentUnavailable,
        };
    }

    private CurrentReading Reading(SignalRuntime signal)
    {
        var direction = DirectionResolver.Resolve(map, signal, now);
        var signed = DirectionResolver.SignedValue(signal, direction);
        return new CurrentReading(new Amperes(signed), new Amperes(Math.Abs(signed)), direction.Direction, direction.Source, signal.Accepted!.Effective);
    }
}
