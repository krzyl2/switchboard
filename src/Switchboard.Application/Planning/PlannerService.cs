using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>
/// The planner's own loop (15-PLANNER, "The three-loop contract" and "Re-planning"): on its own timer, on the injected clock, it builds the planner's
/// input, solves, verifies and publishes the plan to the <see cref="PlanHolder"/>. Nothing here commands, reads the guard's runtime or waits on dispatch:
/// a slow, failing or absent planner changes no decision, because nothing reads the plan yet (slice 4) and, later, whatever does reads it with every
/// fallback of v2.
/// <para>
/// <b>Cadence.</b> A plan is computed at every <c>planner.cycle</c> boundary (multiples of the cycle since the Unix epoch, so :00, :15, :30 and :45) and
/// on triggers: a goal added, changed, met or removed, a device that stopped answering or answers again, goal progress that left the plan by more than
/// <c>planner.progress_tolerance_fraction</c>, a configuration reload, a clock step, the guard leaving safe state. Triggers are rate limited to one plan per
/// <c>planner.min_replan_interval</c> (the first one after a quiet time is at once); a configuration reload, a clock step and a resume are not. A changed
/// forecast or price curve is not a trigger: the next cycle boundary has it. There is no plan before <c>planner.start_delay</c> after the start.
/// </para>
/// <para>
/// <b>Failure.</b> A solver that throws, exceeds <c>planner.max_solve_time</c> or returns a plan the <see cref="PlanVerifier"/> refuses is dropped: the last
/// plan stays (it ages out by <c>planner.plan_max_age</c>), the failure is logged and shown, and the loop goes on. Nothing the planner does can stop the host.
/// </para>
/// <para>
/// <b>Hysteresis.</b> The current slot's entries are the commitment: a changed entry is adopted only after <c>planner.adopt_cycles</c> consecutive plans
/// propose the same change and it moves the energy by at least <c>planner.adopt_min_change_fraction</c> or flips the want; a device running under a run
/// entry keeps it to the slot's end; triggers and urgent entries bypass all this. Future slots take every new plan as it comes.
/// </para>
/// </summary>
public sealed class PlannerService : IPlanTrigger
{
    private readonly IPlannerSource _source;
    private readonly IPlannerSuspension _suspension;
    private readonly PlanHolder _holder;
    private readonly TimeProvider _time;
    private readonly ILogger<PlannerService> _logger;
    private readonly ReplanTriggers _triggers = new();
    private readonly SolverRunner _runner;
    private readonly PlanAdoption _adoption;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private DateTimeOffset? _bootAt;
    private DateTimeOffset? _lastSeen;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _nextCadence;
    private PlanSnapshot? _plan;
    private bool _wasSuspended;
    private bool _idle = true;
    private bool _loopFailing;
    private bool _startDelayDone;
    private bool _captureFailing;
    private TimeSpan? _cycle;
    private TimeSpan _planMaxAge;
    private long _minGapTicks;
    private long _lastLookTicks;
    private SolveReport? _lastSolve;

    /// <param name="solver">The pure planner by default; tests pass a slow or throwing one.</param>
    public PlannerService(
        IPlannerSource source,
        IPlannerSuspension suspension,
        PlanHolder holder,
        TimeProvider time,
        ILogger<PlannerService> logger,
        Func<PlanInput, CancellationToken, PlanSnapshot>? solver = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(suspension);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(time);
        _source = source;
        _suspension = suspension;
        _holder = holder;
        _time = time;
        _logger = logger;
        _runner = new SolverRunner(solver ?? Planner.Plan, time, logger);
        _adoption = new PlanAdoption(logger);
        _suspension.Changed += (_, _) => Wake();
    }

    /// <summary>Asks the loop to look again (a new store cycle, a configuration, a goal); cheap, never blocks, safe from any thread.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    /// <summary>
    /// A new store cycle or configuration: looks again, but at most once per <c>planner.min_replan_interval</c> (the inputs change every few seconds and building a
    /// plan's input is not free; the first look after a quiet time is at once). The cadence and a suspension waking the loop are not throttled.
    /// </summary>
    public void Notify()
    {
        var gap = Interlocked.Read(ref _minGapTicks);
        var last = Interlocked.Read(ref _lastLookTicks);
        if (gap > 0 && last > 0 && _time.GetUtcNow().UtcTicks - last < gap)
        {
            return;
        }

        Wake();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = _time.CreateTimer(_ => Wake(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            _bootAt = _time.GetUtcNow();
            while (true)
            {
                TimeSpan wait;
                try
                {
                    wait = await StepAsync(cancellationToken).ConfigureAwait(false);
                    _loopFailing = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // The planner must never take the host down: log (once per streak) and try again at the next wake-up.
                    if (!_loopFailing)
                    {
                        _logger.LogError(ex, "plan_failed: the planner loop failed unexpectedly; protection and dispatch are unaffected");
                        _loopFailing = true;
                    }

                    wait = Timeout.InfiniteTimeSpan;
                }

                timer.Change(wait == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
                if (!await _wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                while (_wake.Reader.TryRead(out _))
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the plan is not persisted (a stale plan is a hazard), nothing is pending.
        }
    }

    /// <summary>One look at the world: plan if something calls for it, return how long until the next thing could (infinite: only a wake-up can). Public for the tests.</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        _bootAt ??= now;
        var previous = _lastSeen;
        _lastSeen = now;
        Interlocked.Exchange(ref _lastLookTicks, now.UtcTicks);

        PlannerCapture? capture;
        try
        {
            capture = _source.Capture(now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The slot grid or a source refused the inputs: no plan this time, the last one stays (logged once per streak, not at every wake-up).
            if (!_captureFailing)
            {
                _captureFailing = true;
                Fail(SolveOutcome.Failure(now, TimeSpan.Zero, SolveResult.Failed, "inputs", [], ex));
            }

            return _cycle is { } cycle ? ToNextBoundary(now, cycle) : Timeout.InfiniteTimeSpan;
        }

        _captureFailing = false;

        if (capture is null)
        {
            if (!_idle)
            {
                _idle = true;
                Forget();
                _holder.Publish(PlanState.Empty);
                _logger.LogInformation("plan_idle: there is no planner section or no valid configuration; the plan is dropped and dispatch is as v2.");
            }

            return Timeout.InfiniteTimeSpan;
        }

        _idle = false;
        var parameters = capture.Input.Parameters;
        _cycle = parameters.Cycle;
        _planMaxAge = parameters.PlanMaxAge;
        Interlocked.Exchange(ref _minGapTicks, parameters.MinReplanInterval.Ticks);
        if (_suspension.Suspended)
        {
            if (!_wasSuspended)
            {
                _wasSuspended = true;
                _logger.LogWarning("plan_suspended: the guard is in safe state, the kill switch is engaged or Home Assistant is disconnected; the plan is ignored and none is made until it is over.");
                _holder.Publish(new PlanState(_plan, true, _lastSolve, [], parameters.PlanMaxAge));
            }

            return Timeout.InfiniteTimeSpan;
        }

        var resumed = _wasSuspended;
        if (resumed)
        {
            _wasSuspended = false;
            _logger.LogInformation("plan_resumed: the guard is out of safe state; replanning at once.");
        }

        if (!_startDelayDone)
        {
            // The delay is counted from the start; a clock stepped back inside it restarts the count from the new time rather than waiting for the old one.
            if (now < _bootAt.Value)
            {
                _bootAt = now;
            }

            var startDue = _bootAt.Value + parameters.StartDelay;
            if (now < startDue)
            {
                return startDue - now;
            }

            _startDelayDone = true;
        }

        var (reason, immediate, everyone, devices) = _triggers.Evaluate(_plan, capture, now, previous, resumed, parameters);
        _nextCadence ??= NextBoundary(now, parameters.Cycle);
        var cadence = now >= _nextCadence;
        if (reason is null && cadence)
        {
            reason = "cadence";
        }

        if (reason is null)
        {
            return _nextCadence.Value - now;
        }

        if (!immediate && !cadence && _lastAttempt is { } last && now - last < parameters.MinReplanInterval)
        {
            // Rate limited: one plan per min_replan_interval, the trigger waits for its turn.
            return _lastAttempt.Value + parameters.MinReplanInterval - now;
        }

        _lastAttempt = now;
        var outcome = await _runner.SolveAsync(capture.Input, now, reason, cancellationToken).ConfigureAwait(false);
        if (outcome.Plan is null)
        {
            Fail(outcome);
        }
        else
        {
            _lastSolve = outcome.Report;
            _runner.LogComputed(outcome);
        }

        _nextCadence = NextBoundary(now, parameters.Cycle);
        if (outcome.Plan is { } plan)
        {
            Publish(plan, capture, now, reason, everyone || _plan is null, devices);
        }

        return _nextCadence.Value - now;
    }

    // Solving and adoption ------------------------------------------------------------------------------------------------------------------

    /// <summary>A solve that produced no plan: the last plan stays, the failure is shown and logged.</summary>
    private void Fail(SolveOutcome outcome)
    {
        _lastSolve = outcome.Report;
        var note = outcome.Report.Result == SolveResult.Timeout ? PlanNote.SolverTimeout : PlanNote.PlanInvalid;
        _holder.Publish(new PlanState(_plan, _wasSuspended, _lastSolve, [note], _planMaxAge));
        _runner.LogFailure(outcome, _plan is not null);
    }

    private void Publish(PlanSnapshot proposal, PlannerCapture capture, DateTimeOffset now, string reason, bool everyone, HashSet<DeviceName> devices)
    {
        var parameters = capture.Input.Parameters;
        var adopted = _adoption.Adopt(_plan, proposal, capture, now, everyone, devices, out var held);
        _plan = adopted;
        _triggers.Remember(capture);
        _holder.Publish(new PlanState(adopted, false, _lastSolve, [], parameters.PlanMaxAge));
        _logger.LogInformation(
            "plan_adopted: plan {PlanId} is in force; the current slot kept {Held} device entries by hysteresis ({Reason}).",
            adopted.Id,
            held,
            reason);
    }

    // Time ----------------------------------------------------------------------------------------------------------------------------------

    private static DateTimeOffset NextBoundary(DateTimeOffset now, TimeSpan cycle) =>
        new(((now.UtcTicks / cycle.Ticks) + 1) * cycle.Ticks, TimeSpan.Zero);

    private static TimeSpan ToNextBoundary(DateTimeOffset now, TimeSpan cycle) => NextBoundary(now, cycle) - now;

    private void Forget()
    {
        _plan = null;
        _triggers.Forget();
        _adoption.Reset();
        _lastSolve = null;
        _nextCadence = null;
        _lastAttempt = null;
        _wasSuspended = false;
    }
}
