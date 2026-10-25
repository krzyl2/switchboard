using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Turns a valid configuration into what dispatch reads beside its own port: devices with their
/// rated power per stage and rotation groups, hysteresis converted from W to A once here (at the
/// nominal voltage, through the signal layer, which is the only place units change), and the
/// timing, settling and foreign-load parameters.
/// </summary>
public static class DispatchSetupFactory
{
    public static DispatchSetup Build(SwitchboardConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var control = configuration.Control;
        var voltage = configuration.Connection.Voltage;
        var sigma = configuration.Signals.ToDictionary(s => s.Name, s => s.Sigma ?? 0);
        var inverterSigma = configuration.Signals.FirstOrDefault(s => s.Role == SignalRole.InverterPower)?.Sigma ?? 0;
        var phases = configuration.Connection.Phases;

        var devices = configuration.Devices.Select(d => new DispatchDevice(
            d.ToSpec(),
            [.. d.Stages.Select(s => s.RatedPower ?? Watts.Zero)],
            d.RotationGroup,
            d.Draw is null ? [] : [.. d.Draw.Power.Keys.Order()],
            new Amperes(d.Draw?.Current.Values.Select(n => sigma[n]).DefaultIfEmpty(0).Max() ?? 0),
            new Watts(d.Draw?.Power.Values.Select(n => sigma[n]).DefaultIfEmpty(0).Max() ?? 0)));

        // House load = phase power + generation share: both noises count.
        var loadSigma = phases.ToImmutableDictionary(
            p => p,
            p => new Watts((configuration.Signals.FirstOrDefault(s => s.Role == SignalRole.PhasePower && s.Phase == p)?.Sigma ?? 0) + inverterSigma));

        var dispatch = control.Dispatch;
        var settling = control.Settling;
        var foreign = control.ForeignLoad;
        return new DispatchSetup(
            phases,
            [.. devices],
            SignalUnits.CurrentAt(dispatch.StartHysteresis, voltage),
            SignalUnits.CurrentAt(dispatch.StopHysteresis, voltage),
            new DispatchTiming(
                dispatch.Cycle,
                dispatch.PersistenceUpCycles,
                dispatch.PersistenceDownCycles,
                dispatch.MaxStepChangeStages,
                dispatch.PhaseRankingStabilityCycles,
                dispatch.MaxSimultaneousIncreases),
            new SettlingSetup(settling.HardTimeout, settling.VerificationTolerance, settling.VerificationSigmaMultiplier, settling.MismatchLimit, settling.UnresponsiveRetryAfter, control.Effector.ConfirmTimeout),
            new ForeignLoadSetup(foreign.BaselineTimeConstant, foreign.StepThreshold, foreign.RearmFraction),
            loadSigma);
    }
}
