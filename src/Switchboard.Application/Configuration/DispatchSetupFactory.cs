using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
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
            loadSigma)
        {
            Balance = new HourlyBalanceSetup(control.HourlyBalance, voltage),
            Economics = Economics(configuration),
        };
    }

    /// <summary>
    /// The economics dispatch reads (slice 3): null without an <c>economics:</c> section, and then nothing here changes a decision.
    /// The settlement model is the file's (hourly netting, the 0.9 balance rule, when it names none); net billing takes the
    /// dispatch hysteresis and the economics window from the control parameters, so no number of it is written here.
    /// </summary>
    private static EconomicsSetup? Economics(SwitchboardConfiguration configuration)
    {
        if (configuration.Economics is not { } economics)
        {
            return null;
        }

        var control = configuration.Control;
        ISettlementModel model = (economics.Settlement?.Model ?? SettlementModelId.HourlyNetting) switch
        {
            SettlementModelId.NetBilling => new NetBillingModel(control.Dispatch.StartHysteresis, control.Dispatch.StopHysteresis, control.Economics.NetBillingWindow),
            _ => new HourlyNettingModel(control.HourlyBalance),
        };
        return new EconomicsSetup(
            economics.Tariff,
            economics.Settlement?.ExportPrice,
            economics.Holidays,
            model,
            control.Economics,
            [.. configuration.Devices.Select(d => new DeviceEconomics(d.Id, d.Value, d.Sources))]);
    }
}
