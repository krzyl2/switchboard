using Microsoft.Extensions.Logging;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Ev;

/// <summary>What a manual request to the EV module came to; the web API and the MQTT entities each word it their way.</summary>
public enum EvControlResult
{
    /// <summary>The request was applied.</summary>
    Done,

    /// <summary>Nothing changed: the same mode, or the same charge-now already running.</summary>
    Unchanged,

    /// <summary>There is no valid configuration with an <c>ev:</c> section.</summary>
    NotConfigured,

    /// <summary>Charge now is a manual hard goal the plan places; only <c>planner.dispatch: full</c> places goals (use the device's <c>force_on</c> override otherwise).</summary>
    NeedsFullDispatch,

    /// <summary>The car is not plugged in.</summary>
    NotPlugged,

    /// <summary>There is no usable SoC estimate, so there is nothing to compute the energy from.</summary>
    SocUnknown,

    /// <summary>The estimate is already at or above the target.</summary>
    AlreadyAtTarget,

    /// <summary>The target is not above 0 and at most 100 percent.</summary>
    TargetOutOfRange,

    /// <summary>The duration is not above zero.</summary>
    DurationOutOfRange,

    /// <summary>The end of the charge is beyond <c>control.planner.horizon</c>.</summary>
    DeadlineBeyondHorizon,

    /// <summary>There is no charge-now to cancel.</summary>
    NoChargeNow,
}

/// <summary>The manual side of the EV module (EV-09): the mode and "charge now".</summary>
public interface IEvControl
{
    /// <summary>The mode and the running charge-now as the user last set them (the engine persists them to <c>ev.json</c> on its next computation).</summary>
    EvManual Manual { get; }

    /// <summary>Null while there is no valid configuration; false when there is one without an <c>ev:</c> section; true when the module is configured.</summary>
    bool? Configured { get; }

    /// <summary>The configured default target of a charge-now (<c>control.ev.charge_now.target</c>); null without a valid configuration.</summary>
    double? DefaultChargeNowTargetPercent { get; }

    /// <summary>Raised after the mode or the charge-now changed, on any thread; must only be noted.</summary>
    event EventHandler? Changed;

    EvControlResult SetMode(EvMode mode);

    /// <param name="targetPercent">Null takes <c>control.ev.charge_now.target</c>.</param>
    /// <param name="duration">Null takes <c>control.ev.charge_now.duration</c>.</param>
    EvControlResult StartChargeNow(double? targetPercent, TimeSpan? duration);

    EvControlResult CancelChargeNow();
}

/// <summary>
/// The user's controls of the module (docs/EV.md section 6 S6, point 3.10). It changes only the add-on's own state: the mode lives in <see cref="EvManualState"/>
/// (the engine persists it to <c>ev.json</c>) and "charge now" is a manual hard SoC goal in the <see cref="IManualGoalControl"/> store, which the plan places and
/// the guard still bounds. It writes no entity and commands nothing. Charge now needs <c>planner.dispatch: full</c> (a plan that does not reach dispatch cannot
/// charge anything), a plugged car and a SoC estimate; it ends at its target, on unplug or at its deadline.
/// </summary>
public sealed class EvControl(
    EvManualState manual,
    IEvInputSource inputs,
    IEvSnapshotSource snapshots,
    IManualGoalControl goals,
    IPlanTrigger planner,
    SignalClock clock,
    ILogger<EvControl> logger) : IEvControl
{
    public event EventHandler? Changed
    {
        add => manual.Changed += value;
        remove => manual.Changed -= value;
    }

    public EvManual Manual => manual.Current;

    public bool? Configured => inputs.Current.Configuration?.Configuration is { } configuration ? configuration.Ev is not null : null;

    public double? DefaultChargeNowTargetPercent => inputs.Current.Configuration?.Configuration?.Control.Ev.ChargeNow.TargetPercent;

    public EvControlResult SetMode(EvMode mode)
    {
        if (Configured != true)
        {
            return EvControlResult.NotConfigured;
        }

        var changed = manual.Update(m => m with { Mode = mode });
        if (changed)
        {
            logger.LogInformation("EV mode set to {Mode}", mode);
            planner.Wake();
        }

        return changed ? EvControlResult.Done : EvControlResult.Unchanged;
    }

    public EvControlResult StartChargeNow(double? targetPercent, TimeSpan? duration)
    {
        if (inputs.Current.Configuration?.Configuration is not { Ev: { } ev } configuration)
        {
            return EvControlResult.NotConfigured;
        }

        if (configuration.Planner is not { Dispatch: PlannerDispatchMode.Full })
        {
            return EvControlResult.NeedsFullDispatch;
        }

        var p = configuration.Control.Ev;
        var target = targetPercent ?? p.ChargeNow.TargetPercent;
        if (!ManualGoal.TargetInRange(GoalKind.Soc, target))
        {
            return EvControlResult.TargetOutOfRange;
        }

        var length = duration ?? p.ChargeNow.Duration;
        if (length <= TimeSpan.Zero)
        {
            return EvControlResult.DurationOutOfRange;
        }

        if (length > configuration.Control.Planner.Horizon)
        {
            return EvControlResult.DeadlineBeyondHorizon;
        }

        var view = inputs.Current.Inputs;
        if (view.Plugged.Configured && view.IsPlugged == false)
        {
            return EvControlResult.NotPlugged;
        }

        var now = clock.Time.GetUtcNow();
        if (snapshots.Current.Snapshot is not { SocPercent: { } soc } offer || now > offer.ValidUntil)
        {
            return EvControlResult.SocUnknown;
        }

        if (soc >= target)
        {
            return EvControlResult.AlreadyAtTarget;
        }

        var expires = now + length;
        switch (goals.Begin(ev.Device, GoalKind.Soc, target, expires, hard: true))
        {
            case ManualGoalResult.TargetOutOfRange:
                return EvControlResult.TargetOutOfRange;
            case ManualGoalResult.DeadlinePassed:
                return EvControlResult.DurationOutOfRange;
            case ManualGoalResult.DeadlineBeyondHorizon:
                return EvControlResult.DeadlineBeyondHorizon;
            case ManualGoalResult.Begun or ManualGoalResult.Unchanged:
                break;
            default:
                return EvControlResult.NotConfigured;
        }

        manual.Update(m => m with { ChargeNow = new EvChargeNow(now, target, expires) });
        logger.LogInformation("EV charge now: target {Target} % by {Expires}", target, expires);
        planner.Wake();
        return EvControlResult.Done;
    }

    public EvControlResult CancelChargeNow()
    {
        if (inputs.Current.Configuration?.Configuration is not { Ev: { } ev })
        {
            return EvControlResult.NotConfigured;
        }

        // The goal is ended whether or not the state remembers a charge-now: the state can lag (a request racing the engine, ev.json lost) and a manual goal left
        // behind would charge to its target with nothing in the UI to cancel it. Only when neither exists is there nothing to cancel.
        var ended = goals.End(ev.Device) == ManualGoalResult.Ended;
        var cleared = manual.Update(m => m.ChargeNow is null ? m : m with { ChargeNow = null });
        if (!ended && !cleared)
        {
            return EvControlResult.NoChargeNow;
        }

        logger.LogInformation("EV charge now cancelled");
        planner.Wake();
        return EvControlResult.Done;
    }
}
