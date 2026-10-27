using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;

namespace Switchboard.Application.Goals;

/// <summary>Every manual goal at one moment; <see cref="Version"/> grows with each change, so persistence applies a change once and in order.</summary>
public sealed record ManualGoalSnapshot(long Version, ImmutableDictionary<DeviceName, ManualGoal> Goals)
{
    public static ManualGoalSnapshot None { get; } = new(0, ImmutableDictionary<DeviceName, ManualGoal>.Empty);
}

/// <summary>What asking for or cancelling a manual goal came to.</summary>
public enum ManualGoalResult
{
    /// <summary>A goal was stored (or replaced by a different one).</summary>
    Begun,

    /// <summary>The same goal was already stored; nothing changed.</summary>
    Unchanged,

    /// <summary>The goal was cancelled.</summary>
    Ended,

    /// <summary>Nothing to cancel.</summary>
    NotFound,

    /// <summary>No declared device has this id: only declared devices are ever addressed.</summary>
    UnknownDevice,

    /// <summary>There is no valid configuration to check the device against.</summary>
    NoConfiguration,

    /// <summary>A state-of-charge goal needs the device's <c>goal:</c> to be of kind soc (it says where the SoC is read and the capacity).</summary>
    SocNotConfigured,

    /// <summary>The target is not above zero, not finite, or above what a goal of this kind can ask.</summary>
    TargetOutOfRange,

    /// <summary>The deadline is not in the future.</summary>
    DeadlinePassed,

    /// <summary>The deadline is beyond <c>control.planner.horizon</c>: the plan does not see so far.</summary>
    DeadlineBeyondHorizon,
}

/// <summary>Stores and cancels manual goals ("want now", PLAN-19). Only the add-on's own state changes; no entity is ever written.</summary>
public interface IManualGoalControl
{
    ManualGoalSnapshot Current { get; }

    ManualGoalResult Begin(DeviceName device, GoalKind kind, double target, DateTimeOffset deadline, bool hard);

    ManualGoalResult End(DeviceName device);
}

/// <summary>
/// The manual goals (15-PLANNER, Goal sources): a goal the user asked for on a device card, held in the add-on's own state (state.json,
/// like the overrides). It is refused for a device the valid configuration does not declare, expires at its deadline (the goal tracker calls
/// <see cref="Expire"/> each control step), and never writes an entity: it is only an <see cref="IGoalSource"/> whose requests have origin
/// <see cref="GoalOrigin.Manual"/>, so for one device and one deadline it outranks a module, an entity and the file, and the loser is shown, not applied.
/// A goal that begins or ends is handed to state.json at once, its progress with the next interval save.
/// </summary>
public sealed class ManualGoalStore : IManualGoalControl, IGoalSource
{
    private readonly IGoalInputSource _inputs;
    private readonly TimeProvider _time;
    private readonly ILogger<ManualGoalStore> _logger;
    private readonly object _gate = new();
    private ManualGoalSnapshot _current = ManualGoalSnapshot.None;

    public ManualGoalStore(IGoalInputSource inputs, SignalClock clock, ILogger<ManualGoalStore> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _inputs = inputs;
        _time = clock.Time;
        _logger = logger;
    }

    /// <summary>Raised after every change, outside the lock; for persistence and for waking the planner. Handlers must not block.</summary>
    public event EventHandler<ManualGoalSnapshot>? Changed;

    public ManualGoalSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Takes over what state.json held; call before the first step. Raises no event: it is what was already persisted.</summary>
    public void Restore(ImmutableDictionary<DeviceName, ManualGoal> goals)
    {
        ArgumentNullException.ThrowIfNull(goals);
        lock (_gate)
        {
            _current = new ManualGoalSnapshot(_current.Version + 1, goals);
        }
    }

    public ManualGoalResult Begin(DeviceName device, GoalKind kind, double target, DateTimeOffset deadline, bool hard)
    {
        if (_inputs.Current.Configuration?.Configuration is not { } configuration)
        {
            return ManualGoalResult.NoConfiguration;
        }

        if (configuration.Devices.FirstOrDefault(d => d.Id == device) is not { } declared)
        {
            return ManualGoalResult.UnknownDevice;
        }

        if (kind == GoalKind.Soc && declared.Goal is not { Kind: GoalKind.Soc })
        {
            return ManualGoalResult.SocNotConfigured;
        }

        if (!ManualGoal.TargetInRange(kind, target))
        {
            return ManualGoalResult.TargetOutOfRange;
        }

        var now = _time.GetUtcNow();
        if (deadline <= now)
        {
            return ManualGoalResult.DeadlinePassed;
        }

        if (deadline - now > configuration.Control.Planner.Horizon)
        {
            return ManualGoalResult.DeadlineBeyondHorizon;
        }

        var goal = new ManualGoal(kind, target, deadline, hard, now, 0, now);
        ManualGoalSnapshot snapshot;
        lock (_gate)
        {
            if (_current.Goals.TryGetValue(device, out var existing) && existing.SameWish(goal))
            {
                return ManualGoalResult.Unchanged;
            }

            snapshot = new ManualGoalSnapshot(_current.Version + 1, _current.Goals.SetItem(device, goal));
            _current = snapshot;
        }

        _logger.LogInformation("Manual goal: {Device} wants a {Kind} goal ({Target}) by {Deadline}, {Strength}", device, kind, target, deadline, hard ? "hard" : "soft");
        Raise(snapshot);
        return ManualGoalResult.Begun;
    }

    public ManualGoalResult End(DeviceName device)
    {
        ManualGoalSnapshot snapshot;
        lock (_gate)
        {
            if (!_current.Goals.ContainsKey(device))
            {
                return ManualGoalResult.NotFound;
            }

            snapshot = new ManualGoalSnapshot(_current.Version + 1, _current.Goals.Remove(device));
            _current = snapshot;
        }

        _logger.LogInformation("Manual goal: {Device} no longer has a manual goal (cancelled)", device);
        Raise(snapshot);
        return ManualGoalResult.Ended;
    }

    /// <summary>
    /// Ends the goals whose deadline has come, and those of a device the valid configuration no longer declares (a goal for an undeclared
    /// device is never kept). With no valid configuration nothing says a device is gone, so only the deadline counts.
    /// </summary>
    public void Expire(DateTimeOffset now)
    {
        var declared = _inputs.Current.Configuration?.Configuration?.Devices.Select(d => d.Id).ToHashSet();
        ManualGoalSnapshot? snapshot = null;
        List<(DeviceName Device, string Why)> ended = [];
        lock (_gate)
        {
            var goals = _current.Goals;
            foreach (var (device, goal) in _current.Goals)
            {
                if (goal.Deadline <= now)
                {
                    ended.Add((device, "its deadline passed"));
                    goals = goals.Remove(device);
                }
                else if (declared is not null && !declared.Contains(device))
                {
                    ended.Add((device, "the device is no longer declared"));
                    goals = goals.Remove(device);
                }
            }

            if (ended.Count > 0)
            {
                snapshot = new ManualGoalSnapshot(_current.Version + 1, goals);
                _current = snapshot;
            }
        }

        if (snapshot is null)
        {
            return;
        }

        foreach (var (device, why) in ended)
        {
            _logger.LogInformation("Manual goal: {Device} no longer has a manual goal ({Why})", device, why);
        }

        Raise(snapshot);
    }

    /// <summary>Adds energy a device drew over a step to its energy goal; a device with no energy goal counts nothing. Progress alone is not written at once.</summary>
    public void AddDelivered(DeviceName device, double kwh, DateTimeOffset at)
    {
        if (!double.IsFinite(kwh) || kwh < 0)
        {
            return;
        }

        ManualGoalSnapshot snapshot;
        lock (_gate)
        {
            if (!_current.Goals.TryGetValue(device, out var goal) || goal.Kind != GoalKind.Energy)
            {
                return;
            }

            snapshot = new ManualGoalSnapshot(_current.Version + 1, _current.Goals.SetItem(device, goal with { DeliveredKwh = goal.DeliveredKwh + kwh, UpdatedAt = at }));
            _current = snapshot;
        }

        Raise(snapshot);
    }

    /// <summary>The goals of devices the valid configuration declares and whose deadline has not come, as requests; a SoC goal whose SoC cannot be read is inactive with why.</summary>
    public GoalOffer Goals(GoalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stored = Current.Goals;
        if (stored.IsEmpty)
        {
            return GoalOffer.Empty;
        }

        var requests = ImmutableArray.CreateBuilder<GoalRequest>();
        var inactive = ImmutableArray.CreateBuilder<InactiveGoal>();
        foreach (var (device, goal) in stored.OrderBy(g => g.Key))
        {
            if (goal.Deadline <= context.Now || context.Configuration.Devices.FirstOrDefault(d => d.Id == device) is not { } declared)
            {
                continue;
            }

            var spec = declared.Goal;
            var loss = spec?.LossFraction ?? context.Configuration.Control.Planner.Goal.DefaultLossFraction;
            var revision = goal.Revision(device);
            if (goal.Kind == GoalKind.Energy)
            {
                var left = GoalMath.RemainingEnergyKwh(goal.Target, goal.DeliveredKwh, loss);
                requests.Add(new GoalRequest(device, GoalKind.Energy, left, goal.Target, null, null, goal.DeliveredKwh, goal.Deadline, TimeSpan.Zero, goal.Hard, spec?.Sources, goal.Hard, GoalOnMet.Opportunistic, goal.Since, GoalOrigin.Manual, revision));
                continue;
            }

            if (spec is not { Kind: GoalKind.Soc, Soc: { } socInput, CapacityKwh: { } capacity })
            {
                // The device's goal: no longer says where the SoC is read (edited away since): there is nothing to compute the need from.
                inactive.Add(new InactiveGoal(device, GoalInactiveReason.InputMissing, GoalInputKind.Soc, GoalOrigin.Manual, GoalKind.Soc, goal.Hard));
                continue;
            }

            var read = ConfigGoalSource.Number(socInput, context.Inputs.Samples);
            if (read.Failure is { } failure)
            {
                inactive.Add(new InactiveGoal(device, failure, GoalInputKind.Soc, GoalOrigin.Manual, GoalKind.Soc, goal.Hard));
                continue;
            }

            if (read.Value is not (>= 0 and <= 100))
            {
                inactive.Add(new InactiveGoal(device, GoalInactiveReason.InputOutOfRange, GoalInputKind.Soc, GoalOrigin.Manual, GoalKind.Soc, goal.Hard));
                continue;
            }

            var remaining = GoalMath.RemainingSocKwh(goal.Target, read.Value.Value, capacity, loss);
            requests.Add(new GoalRequest(device, GoalKind.Soc, remaining, null, goal.Target, read.Value, null, goal.Deadline, TimeSpan.Zero, goal.Hard, spec.Sources, goal.Hard, GoalOnMet.Opportunistic, null, GoalOrigin.Manual, revision));
        }

        return new GoalOffer(requests.ToImmutable(), inactive.ToImmutable());
    }

    private void Raise(ManualGoalSnapshot snapshot)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<ManualGoalSnapshot>)handler)(this, snapshot);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "A listener of the manual goals failed");
            }
        }
    }
}
