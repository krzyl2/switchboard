using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Dispatch;

/// <summary>
/// One load model, in amperes, for the guard and for dispatch (04 §3 "estimate", DISP-06). It
/// answers three questions per device and phase:
/// <list type="bullet">
/// <item><see cref="Contribution"/>: how much of the measured current is ours. The device's own
/// fresh draw when it declares one; otherwise a relay's rated current while it is reported on
/// (a stated assumption: no draw signal, no better knowledge); otherwise nothing. A setpoint is a
/// ceiling, not a draw: a charger at 20 A with no car draws 0 A.</item>
/// <item><see cref="DeviceLevels.Ceiling"/>: how much a level allows.</item>
/// <item><see cref="InFlightDelta"/>: how a target that is not (yet) applied changes the current.
/// An increase adds up to its ceiling; a decrease relieves at most what the device really draws.
/// So the estimate can neither invent headroom (a would-start is added) nor hide an overload
/// (relief never exceeds a measured draw).</item>
/// </list>
/// A storage device's discharge lowers the phase current, so it is counted as a negative
/// contribution (its own draw when it declares one, else a negative reported setpoint): leaving a
/// discharge is then an increase of |discharge| plus the new charge, never hidden. A charge is
/// still a ceiling and never counted as relief without a draw.
/// </summary>
public static class LoadModel
{
    /// <summary>The share of the measured phase current that is this device's.</summary>
    /// <param name="reported">The applied state; null when unknown (it then relieves nothing).</param>
    /// <param name="draw">The device's draw-current sample on this phase, if it declares one.</param>
    public static Amperes Contribution(DeviceSpec spec, Phase phase, DeviceLevel? reported, DeviceDrawSample? draw)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!spec.IsOn(phase))
        {
            return Amperes.Zero;
        }

        var storage = spec.Type == DeviceType.Storage;
        if (spec.HasCurrentDraw(phase))
        {
            // A stale or missing draw is no knowledge: no relief, and that device's increases wait.
            return draw is { Fresh: true, Current: { } current } ? storage ? current : Amperes.Max(current, Amperes.Zero) : Amperes.Zero;
        }

        if (storage)
        {
            return Discharge(spec, reported, phase);
        }

        return !spec.IsSetpoint && reported is not null ? DeviceLevels.Ceiling(spec, reported, phase) : Amperes.Zero;
    }

    /// <summary>
    /// What a member contributes to a circuit group's estimated current when the group has no
    /// sub-meter (CONN-03): its fresh draw; otherwise the worst case of its state: a relay's rated
    /// current when on or unknown, a setpoint when known (its maximum when unknown).
    /// </summary>
    public static Amperes GroupTerm(DeviceSpec spec, Phase phase, DeviceLevel? reported, DeviceDrawSample? draw)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!spec.IsOn(phase))
        {
            return Amperes.Zero;
        }

        if (spec.HasCurrentDraw(phase) && draw is { Fresh: true, Current: { } current })
        {
            return Amperes.Max(current, Amperes.Zero);
        }

        return DeviceLevels.Ceiling(spec, reported ?? DeviceLevels.Max(spec), phase);
    }

    /// <summary>
    /// The change a target makes to the current on a phase, against the device's applied state
    /// (unknown counts as its full rating, so no target relieves an unknown device):
    /// <code>
    /// increase:  +(Ceiling(target) − basis), never negative
    /// decrease:  −max(0, basis − Ceiling(target))   (relief never exceeds what it draws)
    /// </code>
    /// <paramref name="basis"/> is the contribution the target acts on: the current one, or the one
    /// captured at the command while its settling window is open (the meter has not seen the change).
    /// </summary>
    public static Amperes InFlightDelta(DeviceSpec spec, Phase phase, DeviceLevel target, DeviceLevel? reported, Amperes basis)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(target);
        var to = DeviceLevels.Ceiling(spec, target, phase);
        var from = DeviceLevels.Ceiling(spec, reported ?? DeviceLevels.Max(spec), phase) + Discharge(spec, reported, phase);
        if (to > from)
        {
            return Amperes.Max(Amperes.Zero, to - basis);
        }

        if (to < from)
        {
            return -Amperes.Max(Amperes.Zero, basis - to);
        }

        return Amperes.Zero;
    }

    /// <summary>A storage device's reported discharge on a phase as a negative current (zero when charging, idle, unknown or not storage).</summary>
    private static Amperes Discharge(DeviceSpec spec, DeviceLevel? reported, Phase phase) =>
        spec.Type == DeviceType.Storage && spec.IsOn(phase) && reported is { Enabled: true, Setpoint: { } setpoint } && setpoint < Amperes.Zero
            ? setpoint
            : Amperes.Zero;
}
