using System.Collections.Immutable;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
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
    private readonly Func<PlanInput, CancellationToken, PlanSnapshot> _solver;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<DeviceName, (string Signature, int Count)> _pending = [];

    private DateTimeOffset? _bootAt;
    private DateTimeOffset? _lastSeen;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _nextCadence;
    private PlanSnapshot? _plan;
    private Dictionary<DeviceName, string> _goalSignatures = [];
    private string? _deviceFingerprint;
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
        _solver = solver ?? Planner.Plan;
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
                Fail(now, TimeSpan.Zero, SolveResult.Failed, "inputs", [], ex);
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

        var (reason, immediate, everyone, devices) = Triggers(capture, now, previous, resumed, parameters);
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
        var plan = await SolveAsync(capture, now, reason, cancellationToken).ConfigureAwait(false);
        _nextCadence = NextBoundary(now, parameters.Cycle);
        if (plan is not null)
        {
            Publish(plan, capture, now, reason, everyone || _plan is null, devices);
        }

        return _nextCadence.Value - now;
    }

    // Triggers ------------------------------------------------------------------------------------------------------------------------------

    /// <returns>Why to plan (null: nothing calls for it), whether the rate limit does not apply, whether the new plan replaces the current slot's entries at once, and for which devices.</returns>
    private (string? Reason, bool Immediate, bool Everyone, HashSet<DeviceName> Devices) Triggers(PlannerCapture capture, DateTimeOffset now, DateTimeOffset? previous, bool resumed, PlannerParameters parameters)
    {
        var devices = new HashSet<DeviceName>();
        if (_plan is null)
        {
            return ("first", false, true, devices);
        }

        string? reason = null;
        var immediate = false;
        var everyone = false;
        if (_plan.ComputedAt > now || (previous is { } before && now < before))
        {
            reason = "clock_step_back";
            immediate = everyone = true;
        }
        else if (previous is { } earlier && now - earlier > parameters.PlanMaxAge)
        {
            reason = "clock_step";
            immediate = everyone = true;
        }

        if (_plan.ConfigRevision != capture.Input.ConfigRevision)
        {
            reason ??= "config";
            immediate = everyone = true;
        }

        if (resumed)
        {
            reason ??= "safe_state_exit";
            immediate = everyone = true;
        }

        var signatures = GoalSignatures(capture);
        foreach (var device in signatures.Keys.Union(_goalSignatures.Keys))
        {
            if (signatures.GetValueOrDefault(device) != _goalSignatures.GetValueOrDefault(device))
            {
                devices.Add(device);
                reason ??= "goal";
            }
        }

        if (_deviceFingerprint is not null && _deviceFingerprint != capture.DeviceFingerprint)
        {
            reason ??= "device_state";
            everyone = true;
        }

        foreach (var device in Deviating(capture, now, parameters))
        {
            devices.Add(device);
            reason ??= "progress";
        }

        return (reason, immediate, everyone, devices);
    }

    private static Dictionary<DeviceName, string> GoalSignatures(PlannerCapture capture)
    {
        var result = new Dictionary<DeviceName, string>();
        foreach (var group in capture.Input.Goals.GroupBy(g => g.Device))
        {
            result[group.Key] = string.Join(
                '|',
                group.OrderBy(g => g.Deadline is null).ThenBy(g => g.Deadline).ThenBy(g => g.Revision).Select(g =>
                    string.Create(CultureInfo.InvariantCulture, $"{g.Revision}/{g.Deadline?.UtcTicks}/{g.Hard}/{g.OnMet}/{GoalMath.IsMet(g.RemainingKwh)}")));
        }

        foreach (var inactive in capture.Inactive)
        {
            result[inactive.Device] = result.GetValueOrDefault(inactive.Device) + $"#{inactive.Input}:{inactive.Reason}";
        }

        return result;
    }

    /// <summary>
    /// Devices whose goal progress left the plan: the energy the goals still need against what the plan said would still be needed by now. A device that drew
    /// half the planned surplus under a cloud, or one charged by hand, is replanned without waiting for the cycle boundary.
    /// </summary>
    private IEnumerable<DeviceName> Deviating(PlannerCapture capture, DateTimeOffset now, PlannerParameters parameters)
    {
        if (_plan is null)
        {
            yield break;
        }

        foreach (var device in _plan.Devices)
        {
            var planned = device.Goals.Where(g => g.Status is GoalStatus.OnTrack or GoalStatus.AtRisk or GoalStatus.Infeasible or GoalStatus.Missed).ToList();
            var start = planned.Sum(g => g.RemainingKwh);
            if (start <= Planner.Tolerance)
            {
                continue;
            }

            var done = 0.0;
            foreach (var entry in device.Entries.Where(e => e.Start < now && e.Want is PlanWant.Run or PlanWant.Urgent))
            {
                var share = entry.End <= now ? 1 : (now - entry.Start) / (entry.End - entry.Start);
                done += entry.EnergyKwh * share;
            }

            var expected = Math.Max(0, start - done);
            var actual = capture.Input.Goals
                .Where(g => g.Device == device.Device && planned.Any(p => p.Ref.Revision == g.Revision && p.Deadline == g.Deadline))
                .Sum(g => g.RemainingKwh);
            if (Math.Abs(actual - expected) > parameters.ProgressToleranceFraction * start)
            {
                yield return device.Device;
            }
        }
    }

    // Solving -------------------------------------------------------------------------------------------------------------------------------

    private async Task<PlanSnapshot?> SolveAsync(PlannerCapture capture, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        using var solving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var input = capture.Input;
        var task = Task.Run(() => _solver(input, solving.Token), CancellationToken.None);
        var timeout = Task.Delay(input.Parameters.MaxSolveTime, _time, waiting.Token);
        Task finished;
        try
        {
            finished = await Task.WhenAny(task, timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await solving.CancelAsync().ConfigureAwait(false);
            throw;
        }

        if (finished != task)
        {
            // Over the time allowed: the cooperative solver stops at its next check, an uncooperative one is abandoned and its result never read.
            await solving.CancelAsync().ConfigureAwait(false);
            _ = task.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            Fail(now, _time.GetElapsedTime(started), SolveResult.Timeout, reason, [], null, input.Parameters.MaxSolveTime);
            return null;
        }

        await waiting.CancelAsync().ConfigureAwait(false);
        PlanSnapshot plan;
        try
        {
            plan = await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(now, _time.GetElapsedTime(started), SolveResult.Failed, reason, [], ex);
            return null;
        }

        ImmutableArray<PlanViolation> violations;
        try
        {
            violations = PlanVerifier.Verify(input, plan);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(now, _time.GetElapsedTime(started), SolveResult.Failed, reason, [], ex);
            return null;
        }

        if (!violations.IsEmpty)
        {
            Fail(now, _time.GetElapsedTime(started), SolveResult.Invalid, reason, violations, null);
            return null;
        }

        _lastSolve = new SolveReport(now, _time.GetElapsedTime(started), SolveResult.Computed, reason, []);
        _logger.LogInformation(
            "plan_computed: plan {PlanId} for {Slots} slots and {Devices} devices, planned cost {Cost:0.##}, solved in {Milliseconds:0.0} ms ({Reason}).",
            plan.Id,
            plan.Slots.Length,
            plan.Devices.Length,
            plan.Cost.Planned,
            _lastSolve.Duration.TotalMilliseconds,
            reason);
        return plan;
    }

    private void Fail(DateTimeOffset now, TimeSpan duration, SolveResult result, string reason, ImmutableArray<PlanViolation> violations, Exception? exception, TimeSpan? limit = null)
    {
        _lastSolve = new SolveReport(now, duration, result, reason, violations);
        var note = result == SolveResult.Timeout ? PlanNote.SolverTimeout : PlanNote.PlanInvalid;
        _holder.Publish(new PlanState(_plan, _wasSuspended, _lastSolve, [note], _planMaxAge));
        var kept = _plan is null ? "no plan is held" : "the last plan stays until it ages out";
        switch (result)
        {
            case SolveResult.Timeout:
                _logger.LogWarning("plan_failed: the solver did not finish within {Limit} ({Reason}); {Kept}.", limit, reason, kept);
                break;
            case SolveResult.Invalid:
                _logger.LogWarning(
                    "plan_failed: the verifier refused the plan ({Violations}) ({Reason}); {Kept}.",
                    string.Join(", ", violations.Select(v => v.Code).Distinct()),
                    reason,
                    kept);
                break;
            default:
                _logger.LogWarning(exception, "plan_failed: the plan could not be computed ({Reason}); {Kept}.", reason, kept);
                break;
        }
    }

    // Adoption ------------------------------------------------------------------------------------------------------------------------------

    private void Publish(PlanSnapshot proposal, PlannerCapture capture, DateTimeOffset now, string reason, bool everyone, HashSet<DeviceName> devices)
    {
        var parameters = capture.Input.Parameters;
        var adopted = Adopt(proposal, capture, now, everyone, devices, out var held);
        _plan = adopted;
        _goalSignatures = GoalSignatures(capture);
        _deviceFingerprint = capture.DeviceFingerprint;
        _holder.Publish(new PlanState(adopted, false, _lastSolve, [], parameters.PlanMaxAge));
        _logger.LogInformation(
            "plan_adopted: plan {PlanId} is in force; the current slot kept {Held} device entries by hysteresis ({Reason}).",
            adopted.Id,
            held,
            reason);
    }

    /// <summary>
    /// Takes the new plan for every future slot and, for the current slot, keeps the entry in force unless the change has been proposed
    /// <c>adopt_cycles</c> times running and moves the energy by <c>adopt_min_change_fraction</c> or flips the want. Triggers, urgent entries and a plan that
    /// is not comparable (another configuration, nothing before it) replace the entry at once; a device running under a run entry keeps it when the plan
    /// would defer it.
    /// </summary>
    private PlanSnapshot Adopt(PlanSnapshot proposal, PlannerCapture capture, DateTimeOffset now, bool everyone, HashSet<DeviceName> triggered, out int held)
    {
        held = 0;
        var parameters = capture.Input.Parameters;
        if (_plan is null || everyone || _plan.ConfigRevision != proposal.ConfigRevision || _plan.ComputedAt > now)
        {
            _pending.Clear();
            return proposal;
        }

        var heldSurplus = 0.0;
        var devices = ImmutableArray.CreateBuilder<DevicePlan>(proposal.Devices.Length);
        foreach (var device in proposal.Devices)
        {
            var next = device.Entries.FirstOrDefault(e => now >= e.Start && now < e.End);
            var current = _plan.EntryAt(device.Device, now);
            if (next is null || current is null || triggered.Contains(device.Device) || next.Want == PlanWant.Urgent || next.Goal != current.Goal)
            {
                _pending.Remove(device.Device);
                devices.Add(device);
                continue;
            }

            if (!KeepCurrent(device.Device, current, next, parameters))
            {
                devices.Add(device);
                continue;
            }

            held++;
            var scale = (next.End - next.Start) / (current.End - current.Start);
            var kept = current with { Start = next.Start, End = next.End, EnergyKwh = current.EnergyKwh * scale };
            var holding = Hold(device, next, kept);
            if (PlanClasses.IsPv(kept.Class))
            {
                heldSurplus += holding.Entries.First(e => e.Start == next.Start).EnergyKwh;
            }

            devices.Add(holding);
        }

        var merged = proposal.WithDevices(devices.ToImmutable());

        // The kept entries were valid when they were made: the surplus they counted on is theirs to the end of the slot even if the forecast has since taken it away,
        // so the check of the merged plan is made against an input that still has it. Everything else (limits, need, sources) is checked as it stands.
        var checkedInput = heldSurplus <= 0 || capture.Input.Inputs.Slots.IsEmpty
            ? capture.Input
            : capture.Input with { Inputs = capture.Input.Inputs with { Slots = capture.Input.Inputs.Slots.SetItem(0, capture.Input.Inputs.Slots[0] with { PvKwh = Math.Max(capture.Input.Inputs.Slots[0].PvKwh, capture.Input.Inputs.Slots[0].BaseKwh) + heldSurplus }) } };
        var broken = held > 0 ? PlanVerifier.Verify(checkedInput, merged) : [];
        if (!broken.IsEmpty)
        {
            // Keeping an entry would have broken a limit the new plan respects (the energy it freed went to another device): the new plan stands as it is.
            _logger.LogDebug("plan_hold_dropped: keeping the current slot's entries would have broken {Violations}; the new plan stands as it is.", string.Join(", ", broken.Select(v => v.Code).Distinct()));
            _pending.Clear();
            held = 0;
            return proposal;
        }

        return merged;
    }

    /// <summary>
    /// The device's plan with the current slot's entry kept as it was. Whatever the kept entry has more than the new plan's, the new plan's later slots have
    /// less (taken from the latest first), so the device's total is still the goals' need and the plan adds up; what it has less, the goal shows as unplaced.
    /// If the later slots cannot give it back the kept energy is cut to what the need allows: the commitment never raises the total.
    /// </summary>
    private static DevicePlan Hold(DevicePlan device, PlanEntry next, PlanEntry kept)
    {
        var entries = device.Entries.ToArray();
        var index = Array.IndexOf(entries, next);
        var goals = device.Goals;
        var delta = kept.EnergyKwh - next.EnergyKwh;
        var energy = kept.EnergyKwh;
        if (delta > Planner.Tolerance)
        {
            for (var i = entries.Length - 1; i > index && delta > Planner.Tolerance; i--)
            {
                var later = entries[i];
                if (later.Want is not (PlanWant.Run or PlanWant.Urgent) || later.EnergyKwh <= Planner.Tolerance)
                {
                    continue;
                }

                var take = Math.Min(later.EnergyKwh, delta);
                delta -= take;
                entries[i] = take >= later.EnergyKwh - Planner.Tolerance
                    ? later with { Want = PlanWant.Defer, Class = null, EnergyKwh = 0, MaxPowerW = 0 }
                    : later with { EnergyKwh = later.EnergyKwh - take };
            }

            energy -= Math.Max(0, delta);
        }
        else if (delta < -Planner.Tolerance && kept.Goal is { } reference)
        {
            goals = [.. goals.Select(g => g.Ref == reference ? g with { PlannedKwh = Math.Max(0, g.PlannedKwh + delta), ShortfallKwh = g.ShortfallKwh - delta } : g)];
        }

        entries[index] = kept with { EnergyKwh = energy };
        return device with { Entries = [.. entries], Goals = goals };
    }

    private bool KeepCurrent(DeviceName device, PlanEntry current, PlanEntry next, PlannerParameters parameters)
    {
        var currentPower = current.EnergyKwh / (current.End - current.Start).TotalHours;
        var nextPower = next.EnergyKwh / (next.End - next.Start).TotalHours;
        var larger = Math.Max(currentPower, nextPower);
        var change = larger <= Planner.Tolerance ? 0 : Math.Abs(nextPower - currentPower) / larger;
        var flips = current.Want != next.Want || current.Class != next.Class;
        if (!flips && change < parameters.AdoptMinChangeFraction)
        {
            _pending.Remove(device);
            return true;
        }

        if (current.Want is PlanWant.Run or PlanWant.Urgent && next.Want == PlanWant.Defer)
        {
            // A device that is running under a run entry keeps it to the slot's end: a forecast wobble must not produce a stop and a start inside an hour.
            _pending.Remove(device);
            return true;
        }

        var signature = string.Create(CultureInfo.InvariantCulture, $"{next.Want}|{next.Class}|{nextPower:0.###}");
        var count = _pending.TryGetValue(device, out var known) && known.Signature == signature ? known.Count + 1 : 1;
        if (count >= parameters.AdoptCycles)
        {
            _pending.Remove(device);
            return false;
        }

        _pending[device] = (signature, count);
        return true;
    }

    // Time ----------------------------------------------------------------------------------------------------------------------------------

    private static DateTimeOffset NextBoundary(DateTimeOffset now, TimeSpan cycle) =>
        new(((now.UtcTicks / cycle.Ticks) + 1) * cycle.Ticks, TimeSpan.Zero);

    private static TimeSpan ToNextBoundary(DateTimeOffset now, TimeSpan cycle) => NextBoundary(now, cycle) - now;

    private void Forget()
    {
        _plan = null;
        _goalSignatures = [];
        _deviceFingerprint = null;
        _pending.Clear();
        _lastSolve = null;
        _nextCadence = null;
        _lastAttempt = null;
        _wasSuspended = false;
    }
}
