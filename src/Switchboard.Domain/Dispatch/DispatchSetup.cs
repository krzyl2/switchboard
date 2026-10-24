using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Dispatch;

/// <summary>
/// A declared device as dispatch sees it: the protection spec plus what only dispatch uses (rated
/// power per stage for verification and foreign load, the rotation group, which draws exist).
/// </summary>
/// <param name="StagePower">Rated power of each relay stage (index = stage number − 1); empty for a setpoint device.</param>
/// <param name="RotationGroup">Identical elements on different phases rotated by phase ranking (DISP-10); null when none.</param>
/// <param name="PowerDrawPhases">The phases on which a draw-power signal measures the device.</param>
/// <param name="CurrentDrawSigma">Noise of the draw-current signal (A), for verification tolerance.</param>
/// <param name="PowerDrawSigma">Noise of the draw-power signal (W), for verification tolerance.</param>
public sealed record DispatchDevice(
    DeviceSpec Spec,
    ImmutableArray<Watts> StagePower,
    GroupName? RotationGroup,
    ImmutableArray<Phase> PowerDrawPhases,
    Amperes CurrentDrawSigma,
    Watts PowerDrawSigma)
{
    public DeviceName Id => Spec.Id;

    public bool HasPowerDraw(Phase phase) => !PowerDrawPhases.IsDefault && PowerDrawPhases.Contains(phase);

    /// <summary>Rated power a level draws on a phase while its relays are on (W): the model value, never a measurement.</summary>
    public Watts RatedPower(DeviceLevel level, Phase phase)
    {
        ArgumentNullException.ThrowIfNull(level);
        var total = Watts.Zero;
        foreach (var stage in Spec.Stages)
        {
            if (!Spec.IsSetpoint && stage.Phases.Contains(phase) && !level.Relays.IsDefault && stage.Index - 1 < level.Relays.Length && level.Relays[stage.Index - 1])
            {
                total += StagePower[stage.Index - 1] / stage.Phases.Length;
            }
        }

        return total;
    }
}

/// <summary>Dispatch loop timing (04 §3, §5, §7), from the control defaults.</summary>
/// <param name="MaxSimultaneousIncreases">Increases with a settling window open at once, system-wide (DISP-08).</param>
public sealed record DispatchTiming(
    TimeSpan Cycle,
    int PersistenceUpCycles,
    int PersistenceDownCycles,
    int MaxStepChangeStages,
    int PhaseRankingStabilityCycles,
    int MaxSimultaneousIncreases);

/// <summary>Settling and effector verification (04 §3, DISP-05 … DISP-07).</summary>
/// <param name="VerificationTolerance">A change is a match within this fraction of the expected change.</param>
/// <param name="VerificationSigmaMultiplier">… or within this many standard deviations of the measurement, whichever is larger.</param>
/// <param name="MismatchLimit">Consecutive mismatches that mark a device unresponsive.</param>
/// <param name="UnresponsiveRetryAfter">How long an unresponsive device waits for its probe increase.</param>
public sealed record SettlingSetup(
    TimeSpan HardTimeout,
    double VerificationTolerance,
    double VerificationSigmaMultiplier,
    int MismatchLimit,
    TimeSpan UnresponsiveRetryAfter);

/// <summary>Foreign load detection (04 §4).</summary>
/// <param name="BaselineTimeConstant">Time constant of the slow baseline (exponential moving average).</param>
/// <param name="StepThreshold">A residual above this is one foreign load event.</param>
/// <param name="RearmFraction">The detector re-arms once the residual is back below this fraction of the threshold.</param>
public sealed record ForeignLoadSetup(TimeSpan BaselineTimeConstant, Watts StepThreshold, double RearmFraction);

/// <summary>
/// Everything dispatch needs from the configuration. It travels beside the dispatch inputs, never
/// inside them: hysteresis is in amperes here (converted once at the nominal voltage by the signal
/// layer), and the dispatch port carries watts only (SIG-04).
/// </summary>
/// <param name="StartHysteresis">A start or increase needs this much headroom left after it (A).</param>
/// <param name="StopHysteresis">A running device keeps its level until the headroom falls below this (A, negative).</param>
/// <param name="LoadSigma">Noise of the per-phase house load (phase power plus generation share), W.</param>
public sealed record DispatchSetup(
    ImmutableArray<Phase> Phases,
    ImmutableArray<DispatchDevice> Devices,
    Amperes StartHysteresis,
    Amperes StopHysteresis,
    DispatchTiming Timing,
    SettlingSetup Settling,
    ForeignLoadSetup ForeignLoad,
    ImmutableDictionary<Phase, Watts> LoadSigma)
{
    public DispatchDevice? Device(DeviceName name) => Devices.FirstOrDefault(d => d.Id == name);
}
