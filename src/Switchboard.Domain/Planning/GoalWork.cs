using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;

namespace Switchboard.Domain.Planning;

/// <summary>
/// What is known about one goal in one run. A set of facts and not one of several states: a missed goal can also be infeasible, and a missed goal of a device
/// the plan may not start is missed all the same (the plan says so in a note) without ever being planned.
/// </summary>
[Flags]
internal enum GoalState
{
    None = 0,

    /// <summary>Nothing remains.</summary>
    Met = 1,

    /// <summary>A hard goal whose deadline passed with the need unmet.</summary>
    Missed = 2,

    /// <summary>A soft goal whose deadline passed with the need unmet: it lapses.</summary>
    Expired = 4,

    /// <summary>Placed at all: a live goal of a device the plan may start.</summary>
    Planned = 8,

    /// <summary>Hard, and even at the device's ceiling with nothing else competing it cannot be met before its cutoff.</summary>
    Infeasible = 16,

    /// <summary>Hard, and at its latest grid start (less the escalation margin): it runs from now.</summary>
    UrgentLatest = 32,

    /// <summary>The deadline is past the plan's horizon: what the horizon cannot hold is not a shortfall yet (a later plan will see the rest of the time).</summary>
    BeyondHorizon = 64,

    /// <summary>The goal's sources could not meet it and it was given every zone.</summary>
    Escalated = 128,
}

/// <summary>One goal of one device as the planner works it: the request, what was found out about it (<see cref="GoalState"/>), where it ends and what is placed for it.</summary>
internal sealed class GoalWork
{
    private GoalState _state;

    private GoalWork(GoalRequest request, PlanDevice device, GoalState state, DateTimeOffset cutoff, SourceSet sources, double effectiveValue)
    {
        Request = request;
        Device = device;
        _state = state;
        Cutoff = cutoff;
        Sources = sources;
        EffectiveValue = effectiveValue;
    }

    public GoalRequest Request { get; }

    public PlanDevice Device { get; }

    public DateTimeOffset Cutoff { get; }

    public DateTimeOffset? LatestGridStart { get; private set; }

    public SourceSet Sources { get; }

    /// <summary>The device's value of a kWh in the first slot; an ordering key only.</summary>
    public double EffectiveValue { get; }

    public List<Alloc> Allocs { get; set; } = [];

    public bool Hard => Request.Hard;

    public double Need => Request.RemainingKwh;

    public bool Met => Has(GoalState.Met);

    public bool Missed => Has(GoalState.Missed);

    public bool Expired => Has(GoalState.Expired);

    public bool Planned => Has(GoalState.Planned);

    public bool Infeasible => Has(GoalState.Infeasible);

    public bool UrgentLatest => Has(GoalState.UrgentLatest);

    public bool BeyondHorizon => Has(GoalState.BeyondHorizon);

    public bool Escalated => Has(GoalState.Escalated);

    public bool Critical => Missed || Infeasible || UrgentLatest;

    public double Placed => Allocs.Sum(a => a.Kwh);

    public double Shortfall => Planned ? Math.Max(0, Need - Placed) : 0;

    public double Cost => Allocs.Sum(a => a.Kwh * a.Cost);

    /// <summary>What the request and the input say about the goal, before anything is placed.</summary>
    public static GoalWork Describe(PlanContext ctx, GoalRequest request, PlanDevice device)
    {
        var now = ctx.Input.Now;
        var horizon = ctx.Input.Inputs.Grid.To;
        var met = GoalMath.IsMet(request.RemainingKwh);
        var passed = request.Deadline is { } deadline && now >= deadline;
        var missed = !met && request.Hard && passed;
        var expired = !met && !request.Hard && passed;
        var cutoff = request.Deadline is null || missed
            ? horizon
            : request.UsableDeadline!.Value > now ? request.UsableDeadline.Value : request.Deadline.Value;
        var planned = !met && !expired && device.CanStart && device.Kind != PlanDeviceKind.Storage;

        var state = GoalState.None;
        state |= met ? GoalState.Met : GoalState.None;
        state |= missed ? GoalState.Missed : GoalState.None;
        state |= expired ? GoalState.Expired : GoalState.None;
        state |= planned ? GoalState.Planned : GoalState.None;
        var w = new GoalWork(request, device, state, cutoff, request.Sources ?? device.Sources ?? SourceSet.Any, ctx.EffectiveValue(device, 0));
        if (!planned)
        {
            return w;
        }

        var ceilingW = ctx.CeilingW(device);
        var beyondHorizon = cutoff > horizon;
        w._state |= beyondHorizon ? GoalState.BeyondHorizon : GoalState.None;
        w._state |= request.Hard && !beyondHorizon && ctx.Deliverable(device, cutoff, ceilingW) < w.Need - Planner.Tolerance ? GoalState.Infeasible : GoalState.None;
        if (request.Hard && request.Deadline is not null && !missed)
        {
            // Grid-only delivery at full power: the last start that still makes the deadline (15-PLANNER step 6).
            w.LatestGridStart = ceilingW > 0 ? cutoff - TimeSpan.FromHours(w.Need / (ceilingW / 1000)) : null;
            // A spread goal runs below the ceiling on purpose, so the escalation margin would turn it urgent in the last hours of its window (it trades a buffer
            // it holds in the ceiling's headroom for an early finish). It goes urgent when delivery at the ceiling only just fits: spread power rises as the goal
            // falls behind (every replan), so that moment is reached only when it is truly late. The cutoff already excludes the goal's own ready margin.
            var margin = request.Spread ? TimeSpan.Zero : ctx.Input.Parameters.HardGoal.EscalationMargin;
            w._state |= ceilingW <= 0 || now >= w.LatestGridStart - margin ? GoalState.UrgentLatest : GoalState.None;
        }

        return w;
    }

    /// <summary>
    /// Earliest deadline first among hard goals, then the soft ones: an urgent or infeasible goal does not jump the queue (it would starve a feasible goal
    /// with an earlier deadline of the very energy it needs); it simply fills earliest-first when its turn comes.
    /// </summary>
    public static int CompareUrgency(GoalWork a, GoalWork b)
    {
        var c = b.Hard.CompareTo(a.Hard);
        if (c != 0)
        {
            return c;
        }

        c = a.Cutoff.CompareTo(b.Cutoff);
        if (c != 0)
        {
            return c;
        }

        // Goals pursued past their deadline all end at the horizon: the one that was due first goes first.
        c = (a.Request.Deadline ?? DateTimeOffset.MaxValue).CompareTo(b.Request.Deadline ?? DateTimeOffset.MaxValue);
        if (c != 0)
        {
            return c;
        }

        c = b.EffectiveValue.CompareTo(a.EffectiveValue);
        if (c != 0)
        {
            return c;
        }

        c = a.Device.Id.CompareTo(b.Device.Id);
        return c != 0 ? c : a.Request.Revision.CompareTo(b.Request.Revision);
    }

    /// <summary>Marks the goal as given every zone (it could not be met on its own sources).</summary>
    public void MarkEscalated(bool escalated) => _state = escalated ? _state | GoalState.Escalated : _state & ~GoalState.Escalated;

    /// <summary>Gives back everything placed for the goal and forgets that it escalated.</summary>
    public void Release(CapacityLedger ledger)
    {
        ledger.Release(Device, Allocs);
        Allocs = [];
        MarkEscalated(false);
    }

    private bool Has(GoalState flag) => (_state & flag) != 0;
}
