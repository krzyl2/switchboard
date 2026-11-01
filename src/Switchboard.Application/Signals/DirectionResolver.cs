using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Signals;

/// <summary>
/// Which way a current or power flows (SIG-03), and the phase power derived from the meter's own
/// current, voltage and power factor. Reads the held samples of the map it is handed and nothing else.
/// </summary>
internal static class DirectionResolver
{
    /// <summary>
    /// SIG-03. A signed value carries its own direction. An unsigned one takes it from its signed
    /// direction power, which must be fresh and reported no more than max_skew before the value
    /// itself (a newer direction power is fine: the value simply has not changed since). Within the
    /// deadband it counts as import. A changed value whose direction power from the same meter poll
    /// has not arrived yet is pending for the pairing wait; after that, or with the direction power
    /// not fresh, the direction is unknown: cautious mode, counted as import. Protection data stays
    /// valid either way.
    /// </summary>
    public static DirectionInfo Resolve(SignalMap map, SignalRuntime signal, DateTimeOffset now)
    {
        var definition = signal.Definition;
        if (definition.Sign != SignConvention.Unsigned)
        {
            return new DirectionInfo(DirectionSource.SignedSignal, signal.Magnitude() < 0 ? FlowDirection.Export : FlowDirection.Import, null);
        }

        if (definition.DirectionDeclaredUnavailable || DirectionSignalOf(map, signal) is not { } direction)
        {
            return new DirectionInfo(DirectionSource.NotConfigured, FlowDirection.Unknown, null);
        }

        var from = direction.Definition.Name;
        if (!SignalFreshness.IsFresh(direction, now))
        {
            return new DirectionInfo(DirectionSource.Unavailable, FlowDirection.Unknown, from);
        }

        if (Aligned(map, signal, direction))
        {
            if (direction.Definition.Role == SignalRole.PhasePowerFactor)
            {
                return PowerFactorDirection(map, direction, now);
            }

            var power = direction.Magnitude();
            var deadband = map.Direction.Deadband.Value;
            return power > deadband ? new DirectionInfo(DirectionSource.SignedSignal, FlowDirection.Import, from)
                : power < -deadband ? new DirectionInfo(DirectionSource.SignedSignal, FlowDirection.Export, from)
                : new DirectionInfo(DirectionSource.Deadband, FlowDirection.Import, from);
        }

        return now - signal.Accepted!.ArrivedAt < map.Direction.PairingWait
            ? new DirectionInfo(DirectionSource.Pending, FlowDirection.Unknown, from)
            : new DirectionInfo(DirectionSource.Unavailable, FlowDirection.Unknown, from);
    }

    /// <summary>
    /// Import until a negative factor has persisted: a one-poll glitch must never read as export, because export blocks
    /// shedding (and, in <see cref="DerivedPower"/>, would read as a phase giving back what it draws). The signal must be fresh.
    /// </summary>
    public static DirectionInfo PowerFactorDirection(SignalMap map, SignalRuntime factor, DateTimeOffset now)
    {
        var direction = map.Direction;
        var value = factor.Magnitude();
        var from = factor.Definition.Name;
        return value > -direction.PowerFactorDeadband || factor.NegativeSince is not { } since || now - since < direction.ExportHold
            ? new DirectionInfo(value > direction.PowerFactorDeadband ? DirectionSource.SignedSignal : DirectionSource.Deadband, FlowDirection.Import, from)
            : new DirectionInfo(DirectionSource.SignedSignal, FlowDirection.Export, from);
    }

    /// <summary>
    /// A phase's net import from the meter's own current, voltage and power factor: P = U · I · |PF|, signed by the held power
    /// factor direction. The meter's phase power register is int16 at 0.1 W: past 3.3 kW of import on one phase it wraps to a
    /// positive value (true = reading − 6553.6 W), and near a zero net balance it reads about 0 while the current does not
    /// (live, 2026-10-27/28: 433 and 376 samples). The three inputs do neither. Null unless all are mapped and fresh, then the
    /// phase power is used as before. The reported time is the oldest of the three, so a settling window never closes on a
    /// value older than its command.
    /// </summary>
    public static (Watts Power, DateTimeOffset ReportedAt)? DerivedPower(SignalMap map, Phase phase, DateTimeOffset now)
    {
        if (map.WithRole(SignalRole.PhasePowerFactor, phase) is not { } factor
            || map.WithRole(SignalRole.PhaseVoltage, phase) is not { } voltage
            || map.WithRole(SignalRole.PhaseCurrent, phase) is not { } current
            || !SignalFreshness.IsFresh(factor, now)
            || !SignalFreshness.IsFresh(voltage, now)
            || !SignalFreshness.IsFresh(current, now))
        {
            return null;
        }

        var magnitude = voltage.Magnitude() * current.Magnitude() * Math.Abs(factor.Magnitude()) / 100;
        var power = PowerFactorDirection(map, factor, now).Direction == FlowDirection.Export ? -magnitude : magnitude;
        var reportedAt = new[] { factor.Accepted!.Effective, voltage.Accepted!.Effective, current.Accepted!.Effective }.Min();
        return (new Watts(power), reportedAt);
    }

    public static SignalRuntime? DirectionSignalOf(SignalMap map, SignalRuntime signal) =>
        signal.Definition is { Sign: SignConvention.Unsigned, DirectionFrom: { } from } ? map.ByNameOrNull(from) : null;

    public static bool Aligned(SignalMap map, SignalRuntime signal, SignalRuntime direction) =>
        signal.Accepted is { } value
        && direction.Accepted is { } power
        && power.Effective >= value.SampleReported - map.Direction.MaxSkew;

    public static double SignedValue(SignalRuntime signal, DirectionInfo direction)
    {
        var value = signal.Magnitude();
        return signal.Definition.Sign == SignConvention.Unsigned && direction.Direction == FlowDirection.Export ? -value : value;
    }
}
