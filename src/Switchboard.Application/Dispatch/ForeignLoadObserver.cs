using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// Foreign load of the dispatch loop (04 §4): owns the per-phase detector and counts what the declared devices draw, which
/// the detector needs to tell the house's load from ours. It reads the guard's envelope only for a device's reported level
/// and the decision inputs of an event; it commands nothing.
/// </summary>
internal sealed class ForeignLoadObserver
{
    private readonly ForeignLoadTracker _tracker = new();

    /// <summary>
    /// Foreign load per phase (04 §4): house load minus our devices (their power draw, else a
    /// relay's rated power while reported on; unknown while a device on the phase is a setpoint
    /// device without a power draw). In active mode a commanded change the reported state does not
    /// show yet counts as ours (the meter may see the load before the relay reports), so our own
    /// command is never foreign, until its verification refuted it: a relay that measurably did not
    /// follow is not counted as on, so a real kettle is not hidden behind it and the end of its
    /// command is no "foreign" rise that would excuse its next mismatch. What it counted (ours per
    /// phase, each device's power and where it came from) is handed back for read-only consumers.
    /// </summary>
    public void Observe(DispatchInput input, DispatchSetup setup, List<DispatchEvent> events, Dictionary<Phase, Watts?> oursByPhase, ImmutableArray<DevicePhasePower>.Builder devicePowers)
    {
        var active = input.Mode.Effective == OperatingMode.Active;
        foreach (var phase in setup.Phases)
        {
            var dispatch = input.Inputs.For(phase);
            Watts? ours = Watts.Zero;
            foreach (var device in setup.Devices.Where(d => d.Spec.IsOn(phase)))
            {
                var load = input.Guard.Envelope.Device(device.Id);
                var power = input.Inputs.DevicePowers.IsDefault ? null : input.Inputs.DevicePowers.FirstOrDefault(p => p.Device == device.Id && p.Phase == phase);
                Watts? own = device.HasPowerDraw(phase) ? power is { Fresh: true, Power: { } w } ? w : null
                    : device.Spec is SetpointSpec ? null
                    : load?.Reported is { } reported ? device.RatedPower(reported, phase)
                    : null;
                // A commanded change counts as ours until the reported state shows it (the meter may
                // see the load first): only in active mode, where something was really applied.
                if (active && input.Ledger.Entry(device.Id) is { TargetCommanded: true, CommandRefuted: false, Target: { } target }
                    && device.Spec is RelaySpec && !device.HasPowerDraw(phase) && load?.Reported is { } shown && !shown.Equals(target))
                {
                    own = own + device.RatedPower(target, phase) - device.RatedPower(shown, phase);
                }

                ours = ours is { } sum && own is { } add ? sum + add : null;
                devicePowers.Add(new DevicePhasePower(
                    device.Id,
                    phase,
                    own,
                    own is null ? PowerSource.Unknown : device.HasPowerDraw(phase) ? PowerSource.Measured : PowerSource.RatedModel));
            }

            oursByPhase[phase] = ours;
            Watts? foreign = dispatch is { DataValid: true, Load: { } houseLoad } && ours is { } controlled ? houseLoad - controlled : null;
            if (_tracker.Observe(phase, foreign, dispatch?.PowerReportedAt, setup.ForeignLoad))
            {
                var (_, baseline) = _tracker.Last(phase);
                events.Add(new DispatchEvent(
                    DecisionScope.Phase,
                    phase,
                    DecisionAction.ForeignLoadDetected,
                    ReasonCode.ForeignLoad,
                    DecisionInputs.None with
                    {
                        Dispatch = new DispatchDecisionInputs(
                            [new ConstraintHeadroom(ConstraintKey.Main(phase), input.Guard.Envelope.For(ConstraintKey.Main(phase))?.Available, null)],
                            setup.StartHysteresis,
                            setup.StopHysteresis,
                            null,
                            ImmutableDictionary<Phase, Amperes>.Empty,
                            ImmutableDictionary<ConstraintKey, Amperes>.Empty,
                            null,
                            new VerificationInputs(baseline?.Value ?? 0, foreign!.Value.Value, MeasureUnit.Watts, Verdict.Inconclusive),
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            null,
                            OverrideMode.Auto),
                    }));
            }
        }
    }

    /// <summary>The latest foreign load and baseline of a phase (null while unknown).</summary>
    public (Watts? Foreign, Watts? Baseline) Last(Phase phase) => _tracker.Last(phase);

    /// <summary>A foreign step is active on the phase (04 §6 v3).</summary>
    public bool IsActive(Phase phase) => _tracker.IsActive(phase);

    /// <summary>A new configuration or a clock that went back: baselines start again.</summary>
    public void Reset() => _tracker.Reset();
}
