using Microsoft.Extensions.Logging;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>
/// One valid plan entry as dispatch may use it (15-PLANNER, "How dispatch consumes it"): the entry of the slot that contains <c>now</c>, from a plan that is
/// fresh, made for the configuration, the device's spec and the device's goal in force. Nothing else of the plan reaches dispatch.
/// </summary>
/// <param name="PlanId">The plan's content fingerprint.</param>
/// <param name="ComputedAt">When the plan was made.</param>
/// <param name="Class">pv or zone:&lt;id&gt;; null for a want that carries none.</param>
/// <param name="EnergyKwh">The slot's budget for the device.</param>
/// <param name="MaxPowerW">The slot's ceiling; zero unless the want is run or urgent.</param>
/// <param name="GoalAllowsZone">The entry's grid class is a zone its goal declares in <c>goal.sources</c> (true for a pv or class-less entry); the class <c>plan</c> opens only then.</param>
public sealed record PlanEntryView(
    string PlanId,
    DateTimeOffset ComputedAt,
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    PlanWant Want,
    string? Class,
    double EnergyKwh,
    double MaxPowerW,
    bool GoalAllowsZone);

/// <summary>
/// What dispatch asks of the plan: the mode, and the entry for a device now (15-PLANNER). A null entry is the v2 path, untouched. The one thing dispatch
/// knows about the plan is this interface; it reads, it never publishes.
/// </summary>
public interface IPlanView
{
    /// <summary><c>planner.dispatch</c> in force; <see cref="PlannerDispatchMode.Off"/> without a <c>planner:</c> section.</summary>
    PlannerDispatchMode Mode { get; }

    /// <summary>
    /// The entry of the slot containing <paramref name="now"/> for the device, or null: the plan is absent, stale, from the future, suspended, does not cover
    /// <paramref name="now"/>, was made for another configuration, spec or goal, or holds no run, defer, done or urgent entry for the device.
    /// </summary>
    PlanEntryView? For(DeviceName device, DateTimeOffset now);

    /// <summary>Called once per dispatch step: notes the plan entering or leaving its fallback (one log line each); a no-op while the mode is off.</summary>
    void Observe(DateTimeOffset now);
}

/// <summary>The goal of a device as it stands now, for the plan view to judge an entry by.</summary>
/// <param name="Sources">The classes the goal declares it may be scheduled on; null when it declares none (the device's own sources then apply).</param>
/// <param name="OnMetStops">The device stays off once the goal is met (<c>on_met: stop</c>).</param>
public sealed record PlanGoalState(SourceSet? Sources, bool OnMetStops);

/// <summary>
/// What the plan was made for and what is in force now (the host answers from the configuration, the signal layer and the goal model): an entry is used only
/// where the two agree. The plan view never sees the configuration or the goal types themselves.
/// </summary>
public interface IPlanCurrency
{
    /// <summary><c>planner.dispatch</c> in force; off without a <c>planner:</c> section or a valid configuration.</summary>
    PlannerDispatchMode Mode { get; }

    /// <summary>The revision of the configuration in force (as the planner stamps its plans); null without a valid configuration.</summary>
    long? ConfigRevision { get; }

    /// <summary>The device's spec fingerprint now (as the planner stamps entries); null for a device the configuration does not declare.</summary>
    long? SpecIdentity(DeviceName device);

    /// <summary>The goal with this id and revision on the device at <paramref name="now"/>; null when there is none (it ended, was removed, or its deadline moved).</summary>
    PlanGoalState? Goal(DeviceName device, string id, long revision, DateTimeOffset now);
}

/// <summary>
/// <c>PlanView.For(device, now)</c> of 15-PLANNER: judges the last published plan on every use and returns an entry only for a plan that is fresh (not older
/// than <c>planner.plan_max_age</c>, not from the future), not suspended, made for the configuration in force, covering <c>now</c>, with an entry whose spec
/// and goal are the ones in force. Everything else is null, and a null leaves the v2 code path untouched: absence is never "defer everything". It also notes,
/// once per change, the plan entering or leaving its fallback (<c>plan_fallback</c>, a log line, not a decision).
/// </summary>
public sealed class PlanView(PlanHolder holder, IPlanCurrency currency, ILogger<PlanView> logger) : IPlanView
{
    private string? _fallback;

    public PlannerDispatchMode Mode => currency.Mode;

    public PlanEntryView? For(DeviceName device, DateTimeOffset now)
    {
        if (currency.Mode == PlannerDispatchMode.Off || FallbackReason(now) is not null || holder.Current.Plan is not { } plan)
        {
            return null;
        }

        if (plan.EntryAt(device, now) is not { Want: not PlanWant.None } entry || entry.Goal is not { } reference)
        {
            return null;
        }

        // An entry made for another spec or another goal is not this device's: that device is v2, the others keep theirs.
        if (currency.SpecIdentity(device) != reference.SpecIdentity
            || currency.Goal(device, reference.Id, reference.Revision, now) is not { } goal)
        {
            return null;
        }

        // Done is a promise about on_met: stop; an entry that says it for a goal that no longer stops is not applied.
        if (entry.Want == PlanWant.Done && !goal.OnMetStops)
        {
            return null;
        }

        var zone = PlanClasses.ZoneId(entry.Class);
        // The class plan opens only for a zone the goal itself declares in goal.sources; a goal that declares none leaves the device's own sources in charge.
        var allowsZone = zone is null || (goal.Sources is not null && goal.Sources.AllowsZone(zone));
        return new PlanEntryView(plan.Id, plan.ComputedAt, entry.Start, entry.End, entry.Want, entry.Class, entry.EnergyKwh, entry.MaxPowerW, allowsZone);
    }

    public void Observe(DateTimeOffset now)
    {
        if (currency.Mode == PlannerDispatchMode.Off)
        {
            _fallback = null;
            return;
        }

        var reason = FallbackReason(now);
        if (reason == _fallback)
        {
            return;
        }

        var was = _fallback;
        _fallback = reason;
        if (reason is not null)
        {
            logger.LogWarning("plan_fallback entered: {Reason}; dispatch decides as without a plan", reason);
        }
        else
        {
            logger.LogInformation("plan_fallback left: the plan is usable again (was {Reason})", was);
        }
    }

    /// <summary>Why the plan as a whole cannot be used now (no entity, no device); null while it can.</summary>
    private string? FallbackReason(DateTimeOffset now)
    {
        var state = holder.Current;
        switch (state.StatusAt(now))
        {
            case PlanStatus.Suspended:
                return "suspended";
            case PlanStatus.Absent:
                return "no_plan";
            case PlanStatus.Error:
                return "solver_failed";
            case PlanStatus.Stale:
                return state.Plan!.ComputedAt > now ? "plan_from_the_future" : "plan_too_old";
        }

        var plan = state.Plan!;
        if (now >= plan.HorizonEnd || plan.Slots.IsDefaultOrEmpty || now < plan.Slots[0].Slot.Start)
        {
            return "outside_the_horizon";
        }

        return currency.ConfigRevision != plan.ConfigRevision ? "configuration_changed" : null;
    }
}
