using System.Globalization;
using System.Text;
using Switchboard.Domain.Devices;

namespace Switchboard.Domain.Dispatch;

/// <summary>
/// Whether two declarations describe the same device (a configuration reload that edited only a
/// comment or another device): its target, deferred intent and dispatch streaks are kept then,
/// and start again when anything about it changed. The start policy is left out: it changes no
/// level. The actuator revision is in: a device re-pointed at another entity is another device.
/// </summary>
public static class SpecIdentity
{
    public static bool Same(DeviceSpec a, DeviceSpec b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Fingerprint(a) == Fingerprint(b);
    }

    /// <summary>
    /// Whether the levels a device can be at mean the same for both declarations: type, stages and
    /// their phases, setpoint range, stop actuator and the entities behind them. A restore baseline
    /// survives a changed declaration only then (a new value per kWh or reaction time moves no level).
    /// </summary>
    public static bool SameLevelSpace(DeviceSpec a, DeviceSpec b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Type == b.Type
            && a.ActuatorRevision == b.ActuatorRevision
            && a.HasStopActuator == b.HasStopActuator
            && a.Range == b.Range
            && a.Stages.Length == b.Stages.Length
            && a.Stages.Zip(b.Stages).All(s => s.First.Index == s.Second.Index && s.First.Phases.SequenceEqual(s.Second.Phases));
    }

    public static string Fingerprint(DeviceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{spec.Id}|{spec.Type}|{spec.ValuePerKwh}|{spec.ShedPriority}|{spec.ReactionTime.Ticks}|{spec.NoShed}|{spec.MinOn.Ticks}|{spec.MinOff.Ticks}|{spec.HasStopActuator}|{spec.CircuitGroup}|a{spec.ActuatorRevision}");
        foreach (var stage in spec.Stages)
        {
            text.Append(CultureInfo.InvariantCulture, $"|s{stage.Index}:{string.Join(',', stage.Phases)}:{stage.RatedCurrent.Value}");
        }

        if (spec.Range is { } range)
        {
            text.Append(CultureInfo.InvariantCulture, $"|r{range.Min.Value}:{range.Max.Value}:{range.Step.Value}");
        }

        text.Append(CultureInfo.InvariantCulture, $"|d{string.Join(',', spec.CurrentDrawPhases.IsDefault ? [] : spec.CurrentDrawPhases)}");
        return text.ToString();
    }
}
