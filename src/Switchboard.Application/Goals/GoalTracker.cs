using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Guard;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Goals;

/// <summary>
/// Every goal at one moment: the applied ones with their status (ordered per device by deadline), the ones that cannot be evaluated
/// with why, and the requests the precedence merge set aside.
/// </summary>
/// <param name="Degraded">A source failed (threw or reported <see cref="GoalOffer.Failed"/>): the board is partial, so nothing may be concluded from a goal's absence (the tracker does not let the ledger follow it).</param>
public sealed record GoalBoard(DateTimeOffset At, ImmutableArray<Goal> Goals, ImmutableArray<InactiveGoal> Inactive, ImmutableArray<GoalRequest> Superseded, bool Degraded = false)
{
    public static GoalBoard Empty(DateTimeOffset at) => new(at, [], [], []);
}

/// <summary>
/// What the goals look like now, without side effects (the API reads it; the tracker uses it per step). Each source is isolated: one that throws offers
/// nothing this time (the others are unaffected) and is logged once per streak, because this runs inside the guard step through the plan view and a
/// source must never be able to stop it. Cancellation is not a failure and passes through.
/// </summary>
public sealed class GoalEvaluator(IEnumerable<IGoalSource> sources, ILogger<GoalEvaluator>? logger = null)
{
    private readonly IGoalSource[] _sources = [.. sources];
    private readonly HashSet<IGoalSource> _failing = [];
    private readonly object _gate = new();

    public GoalBoard Evaluate(GoalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var requests = new List<GoalRequest>();
        var inactive = new List<InactiveGoal>();
        var degraded = false;
        foreach (var source in _sources)
        {
            var offer = Offer(source, context);
            requests.AddRange(offer.Requests);
            inactive.AddRange(offer.Inactive);
            degraded |= offer.Failed;
        }

        var merged = GoalMerge.Merge(requests);
        return new GoalBoard(
            context.Now,
            [.. merged.Applied.Select(r => Goal.Of(r, context.Now))],
            [.. inactive.OrderBy(i => i.Device).ThenBy(i => i.Input)],
            merged.Superseded,
            degraded);
    }

    private GoalOffer Offer(IGoalSource source, GoalContext context)
    {
        try
        {
            var offer = source.Goals(context);
            lock (_gate)
            {
                if (offer.Failed)
                {
                    _failing.Add(source);
                }
                else
                {
                    _failing.Remove(source);
                }
            }

            return offer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            bool first;
            lock (_gate)
            {
                first = _failing.Add(source);
            }

            if (first)
            {
                logger?.LogError(ex, "The goal source {Source} failed and offers no goals; the other sources and protection are unaffected, this source's goals are missing until it recovers", source.GetType().Name);
            }

            return GoalOffer.FailedOffer;
        }
    }
}

/// <summary>
/// Follows the control loop read-only (PLAN-03): at every step it counts the energy each energy goal's device drew (the step's
/// measured, else rated-model, power held over the time since the previous step, as the statistics integrate) into the
/// <see cref="GoalLedger"/> and begins, moves and ends ledger entries as the goals do. It holds no command port, no kill switch and no
/// override control, and nothing it does reaches protection, dispatch or the effector gate. A failure here is logged once per streak and
/// never reaches the guard.
/// </summary>
public sealed class GoalTracker
{
    private readonly IGoalInputSource _inputs;
    private readonly GoalEvaluator _evaluator;
    private readonly GoalLedger _ledger;
    private readonly SignalClock _clock;
    private readonly StatisticsOptions _options;
    private readonly ILogger<GoalTracker> _logger;
    private readonly ManualGoalStore? _manual;
    private readonly object _gate = new();
    private (DateTimeOffset At, ImmutableDictionary<DeviceName, double> Watts)? _held;
    private bool _failing;

    public GoalTracker(
        IGoalInputSource inputs,
        GoalEvaluator evaluator,
        GoalLedger ledger,
        SignalClock clock,
        IOptions<StatisticsOptions> options,
        ILogger<GoalTracker> logger,
        ManualGoalStore? manual = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inputs = inputs;
        _evaluator = evaluator;
        _ledger = ledger;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _manual = manual;
    }

    /// <summary>Follows the control loop; the presentation hub (attached first) has published this step's power by the time this runs. Call once, before the first step.</summary>
    public void Attach(GuardRunner runner, IPresentationSource presentation)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(presentation);
        runner.Stepped += (_, _) => Observe(presentation.Power);
    }

    /// <summary>The goals as they stand at <paramref name="now"/> on the configuration in force; empty without a valid one. No side effects.</summary>
    public GoalBoard Evaluate(DateTimeOffset now)
    {
        var current = _inputs.Current;
        return current.Configuration?.Configuration is { } configuration
            ? _evaluator.Evaluate(new GoalContext(now, configuration, current.Inputs, _clock.Time.LocalTimeZone, _ledger))
            : GoalBoard.Empty(now);
    }

    /// <summary>One control step: count the energy, then let the ledger follow the goals. Isolated: it never throws to the caller.</summary>
    public void Observe(PowerView? view)
    {
        if (view is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                var at = view.EvaluatedAt;
                _manual?.Expire(at);
                Integrate(at);
                _held = (at, view.Devices.ToImmutableDictionary(d => d.Device, d => Math.Max(0, d.Total?.Value ?? 0)));
                Follow(at);
                _failing = false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_failing)
            {
                _logger.LogError(ex, "The goal ledger could not count a control step; protection and dispatch are unaffected");
                _failing = true;
            }
        }
    }

    /// <summary>
    /// The energy of the stretch since the previous step, at the power that step held. A stretch longer than the statistics' integration
    /// gap, or one that runs backwards, counts nothing: a gap is never filled with a guess, and the ledger can only run behind.
    /// </summary>
    private void Integrate(DateTimeOffset at)
    {
        if (_held is not { } held || at <= held.At || at - held.At > _options.MaxIntegrationGap)
        {
            return;
        }

        var duration = at - held.At;
        foreach (var device in _ledger.Current.Entries.Keys)
        {
            if (held.Watts.TryGetValue(device, out var watts) && watts > 0)
            {
                _ledger.AddDelivered(device, SignalUnits.EnergyOf(new Watts(watts), duration).Value, at);
            }
        }

        // A manual goal counts its own progress: it is not the ledger's (a device may have a goal in the file as well).
        foreach (var device in _manual?.Current.Goals.Keys ?? [])
        {
            if (held.Watts.TryGetValue(device, out var watts) && watts > 0)
            {
                _manual!.AddDelivered(device, SignalUnits.EnergyOf(new Watts(watts), duration).Value, at);
            }
        }
    }

    private void Follow(DateTimeOffset at)
    {
        var current = _inputs.Current;
        if (current.Configuration?.Configuration is not { } configuration)
        {
            // Without a valid configuration nothing says a goal ended: the ledger keeps what it has.
            return;
        }

        var energyGoals = configuration.Devices.Where(d => d.Goal is { Kind: GoalKind.Energy }).Select(d => d.Id).ToHashSet();
        if (energyGoals.Count == 0)
        {
            // The common case, a file with no energy goal: nothing to evaluate, and a ledger entry of a goal the file no longer has ends.
            foreach (var device in _ledger.Current.Entries.Keys.ToList())
            {
                _ledger.Remove(device);
            }

            return;
        }

        var board = Evaluate(at);
        if (board.Degraded)
        {
            // A failed source leaves the board partial: a goal it would have stated looks absent, and the ledger must not read that as "ended".
            return;
        }

        // A manual goal keeps its own progress (ManualGoalStore): it must not begin, move or end the file goal's ledger entry.
        foreach (var goal in board.Goals.Where(g => g.Request.Kind == GoalKind.Energy && g.Request.Origin != GoalOrigin.Manual))
        {
            var request = goal.Request;
            if (_ledger.Entry(request.Device) is { } entry && entry.Continues(request.Kind, request.Deadline, at))
            {
                _ledger.Retarget(request.Device, request.Deadline);
            }
            else
            {
                _ledger.Begin(request.Device, request.Kind, at, request.Deadline);
            }
        }

        foreach (var inactive in board.Inactive.Where(i => i.Reason == GoalInactiveReason.WantOff))
        {
            _ledger.Remove(inactive.Device);
        }

        foreach (var device in _ledger.Current.Entries.Keys.Where(d => !energyGoals.Contains(d)).ToList())
        {
            _ledger.Remove(device);
        }
    }
}
