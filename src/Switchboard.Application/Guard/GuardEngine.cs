using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Guard;

/// <summary>
/// The guard loop of 04 §2, deterministic: it never reads a clock, every call is handed
/// <c>now</c>, and it is driven by exactly one caller. In order of precedence:
/// <list type="number">
/// <item>Kill switch (SAFE-03): every controlled device held off.</item>
/// <item>Safe state (SAFE-01): a phase without valid data holds every device on it off; missing or
/// invalid configuration, no data on any phase, or shutdown holds everything off. A device whose
/// own state is not fresh is unsafe and held off too (SIG-05).</item>
/// <item>Tier, band timer and budget reactions (BRK-01 … BRK-03): the most severe source wins.</item>
/// <item>Reserve threshold: dispatch is asked to reduce.</item>
/// <item>The headroom envelope is published.</item>
/// </list>
/// The guard only ever reduces: it holds stages off or at minimum and later lets go of them
/// (released after persistence_up_cycles good boundaries); it never starts anything.
/// Magnitude drives tiers, band timers and the budget (a breaker heats in either direction);
/// the signed current drives the reserve rule and the envelope (adding load to an exporting
/// phase lowers its current). A reaction due on an exporting phase takes no step.
/// </summary>
public sealed class GuardEngine
{
    private readonly ILockoutView _lockouts;
    private readonly ILogger<GuardEngine> _logger;
    private readonly Dictionary<Phase, PhaseGuard> _phases = [];
    private readonly Dictionary<StageKey, Limit> _limits = [];
    private ProtectionSetup? _setup;
    private GuardMemory? _restored;
    private DateTimeOffset _restoredAt;
    private bool _killSwitch;
    private ReasonCode? _systemSafe;
    private SafeStateCause? _systemSafeCause;
    private DateTimeOffset? _systemSafeSince;
    private ImmutableDictionary<Phase, ReasonCode> _phaseSafe = ImmutableDictionary<Phase, ReasonCode>.Empty;
    private DateTimeOffset? _lastNow;
    private long? _lastBoundary;
    private bool _shutdown;
    private bool _cleanShutdown;

    public GuardEngine(ILockoutView lockouts, ILogger<GuardEngine> logger)
    {
        _lockouts = lockouts;
        _logger = logger;
    }

    /// <summary>The last valid setup of this process; null until one arrived.</summary>
    public ProtectionSetup? Setup => _setup;

    /// <summary>
    /// What was persisted at the last stop. A budget earns recovery for the downtime only when the
    /// previous run ended cleanly and the clock did not step back since it was saved.
    /// </summary>
    public void Restore(GuardMemory memory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _restored = memory;
        _restoredAt = now;
    }

    /// <summary>What to persist now (budgets at the last evaluation); never a lockout.</summary>
    public GuardMemory Export()
    {
        var at = _lastNow ?? _restoredAt;
        var recovery = _setup?.Budget.RecoveryTime ?? TimeSpan.MaxValue;

        // A phase not yet seen in this process (no valid configuration so far) keeps what was restored.
        var budgets = (_restored?.Budgets ?? ImmutableDictionary<Phase, BudgetMemory>.Empty)
            .SetItems(_phases.Select(p => KeyValuePair.Create(p.Key, new BudgetMemory(p.Value.Budget(at, recovery), at))));
        return new GuardMemory(budgets, _cleanShutdown);
    }

    public GuardEvaluation Evaluate(GuardInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        var intents = new List<GuardIntent>();
        var events = new List<GuardEvent>();
        var protection = input.Protection;

        if (_lastNow is { } last && now < last)
        {
            _logger.LogWarning("The clock stepped back by {Step}; overload budgets hold until new samples arrive", last - now);
            foreach (var phase in _phases.Values)
            {
                phase.OnClockStepBack(last, _setup!.Budget.RecoveryTime);
            }

            _lastBoundary = null;
        }

        _lastNow = now;
        if (protection?.Setup is { } setup)
        {
            ApplySetup(setup);
        }

        var configurationValid = protection?.Setup is not null;
        _shutdown |= input.Trigger == GuardTrigger.Shutdown;

        if (input.KillSwitch.Engaged != _killSwitch)
        {
            _killSwitch = input.KillSwitch.Engaged;
            events.Add(new GuardEvent(
                DecisionScope.System,
                null,
                _killSwitch ? DecisionAction.KillSwitchEngaged : DecisionAction.KillSwitchDisengaged,
                ReasonCode.KillSwitch,
                DecisionInputs.None with { KillSwitch = input.KillSwitch.Source }));
        }

        if (_setup is not { } active)
        {
            // Nothing declared is known in this process: no device can be held, only the state recorded.
            UpdateSafeStateEvents(ReasonCode.SafeState, ConfigurationCause(protection), [], now, events, null);
            return Finish(input, now, intents, events, null, 0);
        }

        var cycleIndex = (now - DateTimeOffset.UnixEpoch).Ticks / active.Timing.Cycle.Ticks;
        var atBoundary = _lastBoundary is { } previous && cycleIndex > previous;
        _lastBoundary = cycleIndex;

        var context = new Context(this, active, protection, now);
        var timerSteps = new HashSet<Phase>();
        foreach (var phase in active.Phases)
        {
            // At shutdown the data keeps its real validity, so the shutdown records carry the last
            // phase picture; the safe state comes from the shutdown itself.
            var guard = _phases[phase];
            var entry = protection?.For(phase);
            var reading = entry?.Current ?? (protection?.Topology == Topology.ThreePhaseSummed && entry is { DataValid: true } ? protection.Total?.Current : null);
            var valid = configurationValid && entry is { DataValid: true } && reading is not null;
            var reason = valid ? null : configurationValid ? entry?.InvalidReason : ProtectionInvalidReason.ConfigurationInvalid;
            if (guard.Update(valid, reading, reason, active.CurrentMaxAge.GetValueOrDefault(phase), active.Budget.RecoveryTime, now))
            {
                timerSteps.Add(phase);
            }
        }

        var invalid = active.Phases.Where(p => !_phases[p].Valid).ToImmutableArray();
        var (systemReason, cause) = _shutdown ? (ReasonCode.SafeState, SafeStateCause.Shutdown)
            : !configurationValid ? (ReasonCode.SafeState, ConfigurationCause(protection))
            : invalid.Length == active.Phases.Length ? (SafeReason(_phases[invalid[0]].InvalidReason), MeterCause(invalid))
            : ((ReasonCode?)null, (SafeStateCause?)null);
        UpdateSafeStateEvents(systemReason, cause, invalid, now, events, context);

        HoldUnsafeDevices(context, systemReason, cause, intents);
        foreach (var phase in active.Phases.Where(p => systemReason is null && _phases[p].Valid))
        {
            React(context, phase, atBoundary, timerSteps.Contains(phase), intents, events);
        }

        if (atBoundary && !_shutdown)
        {
            foreach (var phase in active.Phases)
            {
                BoundaryEvents(context, phase, systemReason, events);
            }
        }

        RestartReleasePersistence(active, systemReason, intents);
        if (atBoundary)
        {
            Release(context, systemReason, intents);
        }

        if (_shutdown)
        {
            _cleanShutdown = true;
        }

        return Finish(input, now, intents, events, context, cycleIndex);
    }

    /// <summary>The next moment the guard must evaluate without new input: a cycle boundary, a band timer, a budget threshold.</summary>
    public DateTimeOffset? NextDeadline(DateTimeOffset now)
    {
        if (_setup is not { } setup)
        {
            return null;
        }

        var cycle = setup.Timing.Cycle.Ticks;
        var next = DateTimeOffset.UnixEpoch + TimeSpan.FromTicks((((now - DateTimeOffset.UnixEpoch).Ticks / cycle) + 1) * cycle);
        void Consider(DateTimeOffset? candidate)
        {
            if (candidate is { } at && at > now && at < next)
            {
                next = at;
            }
        }

        var budget = setup.Budget;
        foreach (var guard in _phases.Values.Where(g => g.Valid && !g.ChainBroken))
        {
            Consider(guard.NextTimerExpiry());
            var at = guard.CommittedAt!.Value;
            var b = guard.CommittedBudget;
            var m = guard.HeldMultiple;
            var profile = guard.Profile;
            var recovery = budget.RecoveryTime;
            foreach (var crossing in new[]
            {
                OverloadBudget.TimeUntilBelow(b, budget.StopIncreasesBelow, m, profile, recovery),
                OverloadBudget.TimeUntilBelow(b, budget.ShedOneBelow, m, profile, recovery),
                OverloadBudget.TimeUntilAtOrBelow(b, budget.TopTierAtOrBelow, m, profile, recovery),
                OverloadBudget.TimeUntilNotBelow(b, budget.StopIncreasesBelow, m, profile, recovery),
                OverloadBudget.TimeUntilNotBelow(b, budget.ShedOneBelow, m, profile, recovery),
                OverloadBudget.TimeUntilNotBelow(b, 1, m, profile, recovery),
            })
            {
                if (crossing is { } dt && dt <= guard.MaxAge)
                {
                    Consider(at + dt);
                }
            }
        }

        return next;
    }

    private static ReasonCode SafeReason(ProtectionInvalidReason? reason) => reason switch
    {
        ProtectionInvalidReason.CurrentStale or ProtectionInvalidReason.CurrentRejected
            or ProtectionInvalidReason.CurrentUnavailable or ProtectionInvalidReason.CurrentMissing => ReasonCode.GuardStaleSignal,
        _ => ReasonCode.SafeState,
    };

    /// <summary>Why there is no setup: nothing read yet (or no snapshot at all), a missing document, an invalid one.</summary>
    private static SafeStateCause ConfigurationCause(ProtectionInputs? protection) => protection?.SetupAbsence switch
    {
        null or SetupAbsence.NotRead => SafeStateCause.Starting,
        SetupAbsence.Missing => SafeStateCause.ConfigurationMissing,
        _ => SafeStateCause.ConfigurationInvalid,
    };

    private SafeStateCause MeterCause(ImmutableArray<Phase> invalid) =>
        invalid.All(p => _phases[p].InvalidReason == ProtectionInvalidReason.SourceDisconnected) ? SafeStateCause.SourceDisconnected : SafeStateCause.MeterInvalid;

    private void ApplySetup(ProtectionSetup setup)
    {
        if (ReferenceEquals(setup, _setup))
        {
            return;
        }

        foreach (var key in _limits.Keys.Where(k => setup.Device(k.Device) is not { } d || k.Stage > d.Stages.Length).ToList())
        {
            // The device is no longer declared: it can never be commanded again, so there is nothing to hold.
            _logger.LogInformation("Device {Device} stage {Stage} is no longer declared; protection no longer holds it", key.Device, key.Stage);
            _limits.Remove(key);
        }

        foreach (var phase in _phases.Keys.Where(p => !setup.Phases.Contains(p)).ToList())
        {
            _phases.Remove(phase);
            _phaseSafe = _phaseSafe.Remove(phase);
        }

        foreach (var phase in setup.Phases)
        {
            if (_phases.TryGetValue(phase, out var guard))
            {
                guard.Reconfigure(setup.Breaker);
            }
            else
            {
                _phases[phase] = new PhaseGuard(phase, RestoredBudget(phase, setup.Budget.RecoveryTime), setup.Breaker);
            }
        }

        _setup = setup;
    }

    private double RestoredBudget(Phase phase, TimeSpan recovery)
    {
        if (_restored is not { } memory || !memory.Budgets.TryGetValue(phase, out var saved))
        {
            return 1;
        }

        var budget = Math.Clamp(saved.Budget, 0, 1);
        return memory.CleanShutdown && _restoredAt >= saved.At
            ? Math.Min(1, budget + ((double)(_restoredAt - saved.At).Ticks / recovery.Ticks))
            : budget;
    }

    /// <summary>
    /// Safe-state records on change: system-wide when nothing is valid, per phase otherwise. A
    /// system-wide record names its cause, and a new cause is a new record (the log says the add-on
    /// was stopping even when an invalid configuration already held everything off).
    /// </summary>
    private void UpdateSafeStateEvents(ReasonCode? systemReason, SafeStateCause? cause, ImmutableArray<Phase> invalid, DateTimeOffset now, List<GuardEvent> events, Context? context)
    {
        if (systemReason != _systemSafe || cause != _systemSafeCause)
        {
            if (_systemSafe is { } ended && systemReason is null)
            {
                events.Add(new GuardEvent(DecisionScope.System, null, DecisionAction.SafeStateExited, ended, DecisionInputs.None with { SafeStateCause = _systemSafeCause }));
            }

            if (systemReason is { } started)
            {
                events.Add(new GuardEvent(DecisionScope.System, null, DecisionAction.SafeStateEntered, started, DecisionInputs.None with { SafeStateCause = cause }));
                _systemSafeSince = _systemSafe is null ? now : _systemSafeSince;
            }
            else
            {
                _systemSafeSince = null;
            }

            _systemSafe = systemReason;
            _systemSafeCause = cause;
        }

        // A phase's safe state ends only when its data is valid again, not when a system-wide one
        // covers it; while the system-wide one lasts, no phase-level record is added.
        if (context is null)
        {
            return;
        }

        foreach (var (phase, reason) in _phaseSafe.Where(p => !invalid.Contains(p.Key)))
        {
            events.Add(new GuardEvent(DecisionScope.Phase, phase, DecisionAction.SafeStateExited, reason, context.PhaseInputs(phase)));
            _phaseSafe = _phaseSafe.Remove(phase);
        }

        if (systemReason is not null)
        {
            return;
        }

        foreach (var phase in invalid)
        {
            var reason = SafeReason(_phases[phase].InvalidReason);
            if (!_phaseSafe.TryGetValue(phase, out var recorded) || recorded != reason)
            {
                events.Add(new GuardEvent(DecisionScope.Phase, phase, DecisionAction.SafeStateEntered, reason, context.PhaseInputs(phase)));
                _phaseSafe = _phaseSafe.SetItem(phase, reason);
            }
        }
    }

    /// <summary>Kill switch, then safe state, then a device's own stale state: every stage held off, whatever its type.</summary>
    private void HoldUnsafeDevices(Context context, ReasonCode? systemReason, SafeStateCause? cause, List<GuardIntent> intents)
    {
        foreach (var device in context.Setup.Devices.OrderBy(d => d.Id))
        {
            ReasonCode? reason = null;
            Phase? phase = null;
            SafeStateCause? holdCause = null;
            if (_killSwitch)
            {
                reason = ReasonCode.KillSwitch;
            }
            else if (systemReason is { } system)
            {
                reason = system;
                holdCause = cause;
            }
            else if (device.Phases.Any(p => !_phases[p].Valid))
            {
                phase = device.Phases.First(p => !_phases[p].Valid);
                reason = SafeReason(_phases[phase.Value].InvalidReason);
            }
            else if (device.Stages.Any(s => context.Sample(device.Id, s.Index) is not { Fresh: true }))
            {
                // Its own state is unknown: it may be on, so it is held off (SIG-05); being unknown,
                // the hold counts as no relief.
                reason = ReasonCode.GuardStaleSignal;
            }

            if (reason is not { } holding)
            {
                continue;
            }

            foreach (var stage in device.Stages)
            {
                Hold(context, device, stage.Index, GuardTarget.Off, holding, phase, intents, holdCause);
            }
        }
    }

    /// <summary>Tier, band timer and budget reactions on one phase with valid data; the most severe wins.</summary>
    private void React(Context context, Phase phase, bool atBoundary, bool timerStep, List<GuardIntent> intents, List<GuardEvent> events)
    {
        var setup = context.Setup;
        var guard = _phases[phase];
        var profile = setup.Breaker;
        var measured = guard.Multiple;
        var effective = context.Effective(phase);
        var effectiveBand = profile.Band(profile.BandIndex(effective.Multiple)).Reaction;
        var budget = guard.Budget(context.Now, setup.Budget.RecoveryTime);

        if (atBoundary)
        {
            guard.OrdinaryStreak = effectiveBand >= TierReaction.ReduceOrdinaryCycle ? guard.OrdinaryStreak + 1 : 0;
        }

        var demand = Demand.None;
        if (profile.Band(profile.BandIndex(measured)).Reaction == TierReaction.ShedAllOnPhase)
        {
            // From the measured current, never the effective one: relief that does not materialize
            // (a charger at 16 A with no car) must not hide the open band. Idempotent, so no cascade.
            demand = demand.Max(Demand.All(ReasonCode.GuardTierExceeded));
        }

        var tierStep = effectiveBand switch
        {
            TierReaction.ShedOneImmediately => guard.NewSample,
            TierReaction.ReduceNextCycle => atBoundary,
            TierReaction.ReduceOrdinaryCycle => atBoundary && guard.OrdinaryStreak >= setup.Timing.PersistenceDownCycles,
            _ => false,
        };
        if (tierStep || timerStep)
        {
            demand = demand.Max(Demand.One(ReasonCode.GuardTierExceeded));
        }

        if (OverloadBudget.IsAtOrBelow(budget, setup.Budget.TopTierAtOrBelow))
        {
            demand = demand.Max(Demand.All(ReasonCode.GuardBudgetLow));
        }
        else if (OverloadBudget.IsBelow(budget, setup.Budget.ShedOneBelow))
        {
            // One step on the downward crossing, then one per boundary while still consuming.
            if (!guard.BudgetShedLatched || (atBoundary && guard.Consuming))
            {
                demand = demand.Max(Demand.One(ReasonCode.GuardBudgetLow));
            }

            guard.BudgetShedLatched = true;
        }
        else
        {
            guard.BudgetShedLatched = false;
        }

        var exporting = guard.Reading!.Direction == FlowDirection.Export;
        var overloaded = profile.Band(profile.BandIndex(measured)).Reaction >= TierReaction.ReduceOrdinaryCycle
            || OverloadBudget.IsBelow(budget, setup.Budget.ShedOneBelow);
        if (!exporting || !overloaded)
        {
            guard.ExportBlocked = false;
        }

        if (demand.Kind == DemandKind.None)
        {
            return;
        }

        if (exporting)
        {
            // Shedding load on an exporting phase only raises the current through the breaker.
            // Recorded once per exporting overload, not at every reaction it blocks.
            if (!guard.ExportBlocked)
            {
                events.Add(new GuardEvent(DecisionScope.Phase, phase, DecisionAction.CannotReduceExporting, demand.Reason, context.PhaseInputs(phase)));
            }

            guard.ExportBlocked = true;
            return;
        }

        var steps = ShedOrder.For(phase, context.Candidates(phase));
        if (demand.Kind == DemandKind.One)
        {
            if (steps.FirstOrDefault() is { } step && context.Setup.Device(step.Device) is { } device)
            {
                Hold(context, device, step.Stage, step.Action == ShedAction.SwitchOff ? GuardTarget.Off : GuardTarget.AtMinimum, demand.Reason, phase, intents);
            }

            return;
        }

        // Everything controlled on the phase: straight to off, a modulating device included.
        foreach (var (name, stage) in steps.Select(s => (s.Device, s.Stage)).Distinct())
        {
            Hold(context, context.Setup.Device(name)!, stage, GuardTarget.Off, demand.Reason, phase, intents);
        }
    }

    /// <summary>
    /// Boundary-only records with persistence, so a current hovering at a threshold does not flood
    /// the log: the reserve request after persistence_down_cycles boundaries above In − reserve and
    /// its clearing after persistence_up_cycles below; the same for budget-driven increase stops.
    /// </summary>
    private void BoundaryEvents(Context context, Phase phase, ReasonCode? systemReason, List<GuardEvent> events)
    {
        var guard = _phases[phase];
        var setup = context.Setup;
        if (!guard.Valid)
        {
            guard.ReleaseStreak = 0;
            guard.OrdinaryStreak = 0;
            return;
        }

        var timing = setup.Timing;
        var threshold = setup.ReserveThreshold(phase);
        var effective = context.Effective(phase);
        var over = effective.Signed > threshold;
        guard.ReserveOverStreak = over ? guard.ReserveOverStreak + 1 : 0;
        guard.ReserveClearStreak = over ? 0 : guard.ReserveClearStreak + 1;
        if (!guard.ReductionRequested && guard.ReserveOverStreak >= timing.PersistenceDownCycles)
        {
            guard.ReductionRequested = true;
            events.Add(new GuardEvent(DecisionScope.Phase, phase, DecisionAction.RequestReduction, ReasonCode.ReserveThreshold, context.PhaseInputs(phase)));
        }
        else if (guard.ReductionRequested && guard.ReserveClearStreak >= timing.PersistenceUpCycles)
        {
            guard.ReductionRequested = false;
            events.Add(new GuardEvent(DecisionScope.Phase, phase, DecisionAction.ReductionRequestCleared, ReasonCode.HeadroomAvailable, context.PhaseInputs(phase)));
        }

        var budget = guard.Budget(context.Now, setup.Budget.RecoveryTime);
        var low = OverloadBudget.IsBelow(budget, setup.Budget.StopIncreasesBelow);
        guard.BudgetLowStreak = low ? guard.BudgetLowStreak + 1 : 0;
        guard.BudgetOkStreak = low ? 0 : guard.BudgetOkStreak + 1;
        if (!guard.IncreasesStopped && guard.BudgetLowStreak >= timing.PersistenceDownCycles)
        {
            guard.IncreasesStopped = true;
            events.Add(new GuardEvent(DecisionScope.Phase, phase, DecisionAction.StopIncreases, ReasonCode.GuardBudgetLow, context.PhaseInputs(phase)));
        }
        else if (guard.IncreasesStopped && guard.BudgetOkStreak >= timing.PersistenceUpCycles)
        {
            guard.IncreasesStopped = false;
            events.Add(new GuardEvent(DecisionScope.Phase, phase, DecisionAction.IncreasesAllowed, ReasonCode.HeadroomAvailable, context.PhaseInputs(phase)));
        }

        // A boundary counts towards release only when everything is comfortably inside: measured
        // current at or below the reserve threshold and the continuous current, budget not low.
        var good = systemReason is null
            && !_killSwitch
            && guard.Reading!.Signed <= threshold
            && !setup.Breaker.IsAboveContinuous(guard.Multiple)
            && !low;
        guard.ReleaseStreak = good ? guard.ReleaseStreak + 1 : 0;
    }

    /// <summary>
    /// Release persistence counts good boundaries after the last hold cause, wherever between two
    /// boundaries it appeared: invalid data, a safe state or the kill switch at this evaluation, or a
    /// hold recorded at it (04 §7, asymmetric safety). A one-poll outage or a kill switch flicked on
    /// and off between two boundaries thus waits persistence_up_cycles boundaries like any other.
    /// Runs after the boundary records, so the boundary of a new hold does not count as good.
    /// </summary>
    private void RestartReleasePersistence(ProtectionSetup setup, ReasonCode? systemReason, List<GuardIntent> intents)
    {
        var held = intents.SelectMany(i => setup.Device(i.Device)?.Phases ?? []).ToHashSet();
        foreach (var phase in setup.Phases)
        {
            var guard = _phases[phase];
            if (systemReason is not null || _killSwitch || !guard.Valid || held.Contains(phase))
            {
                guard.ReleaseStreak = 0;
            }
        }
    }

    /// <summary>
    /// A held stage is let go once every phase of its device has had persistence_up_cycles good
    /// boundaries and the device's own state is fresh. Leaving safe state or releasing the kill
    /// switch never lets go by itself (asymmetric safety, 04 §7).
    /// </summary>
    private void Release(Context context, ReasonCode? systemReason, List<GuardIntent> intents)
    {
        if (systemReason is not null || _killSwitch)
        {
            return;
        }

        var up = context.Setup.Timing.PersistenceUpCycles;
        foreach (var key in _limits.Keys.OrderBy(k => k.Device).ThenBy(k => k.Stage).ToList())
        {
            var device = context.Setup.Device(key.Device)!;
            if (device.Phases.All(p => _phases[p].Valid && _phases[p].ReleaseStreak >= up)
                && device.Stages.All(s => context.Sample(device.Id, s.Index) is { Fresh: true }))
            {
                _limits.Remove(key);
                intents.Add(new GuardIntent(
                    key.Device,
                    StageOf(device, key.Stage),
                    GuardTarget.Released,
                    ReasonCode.HeadroomAvailable,
                    null,
                    IntentUrgency.Protective,
                    null,
                    context.Inputs(device, key.Stage, device.Phases[0])));
            }
        }
    }

    /// <summary>Holds a stage at a target; records an intent only when the target or the reason changes, and never relaxes Off to AtMinimum.</summary>
    /// <param name="cause">The system-wide safe state's cause, for a hold that state made.</param>
    private void Hold(Context context, DeviceSpec device, int stage, GuardTarget target, ReasonCode reason, Phase? phase, List<GuardIntent> intents, SafeStateCause? cause = null)
    {
        var key = new StageKey(device.Id, stage);
        if (_limits.TryGetValue(key, out var held) && (held.Target == GuardTarget.Off && target == GuardTarget.AtMinimum || (held.Target == target && held.Reason == reason)))
        {
            return;
        }

        _limits[key] = new Limit(target, reason, phase);
        var overridden = target == GuardTarget.Off && _lockouts.MinOnRemaining(device.Id, stage, context.Now) > TimeSpan.Zero
            ? ReasonCode.LockoutMinOn
            : (ReasonCode?)null;
        intents.Add(new GuardIntent(
            device.Id,
            StageOf(device, stage),
            target,
            reason,
            phase,
            IntentUrgency.Protective,
            overridden,
            context.Inputs(device, stage, phase ?? device.Phases[0]) with { SafeStateCause = cause }));
    }

    private static int? StageOf(DeviceSpec device, int stage) => device.Stages.Length > 1 ? stage : null;

    private GuardEvaluation Finish(GuardInput input, DateTimeOffset now, List<GuardIntent> intents, List<GuardEvent> events, Context? context, long cycleIndex)
    {
        var setup = context?.Setup;
        var status = new GuardStatus(
            now,
            input.Mode,
            input.KillSwitch,
            CurrentSafeState(),
            setup is null ? null : new BreakerStatus(
                setup.Breaker.Rating,
                setup.Breaker.Characteristic,
                setup.Breaker.ContinuousMultiple,
                setup.Breaker.ContinuousCurrent,
                setup.Breaker.Tiers.Bands),
            context is null ? [] : [.. setup!.Phases.Select(context.PhaseStatus)],
            context is null ? [] : [.. setup!.Devices.Select(context.DeviceStatus)]);
        return new GuardEvaluation(now, status, [.. intents], [.. events], setup?.Timing, cycleIndex);
    }

    private SafeStateStatus CurrentSafeState()
    {
        if (_systemSafe is { } system)
        {
            return new SafeStateStatus(true, SafeStateScope.All, system, [.. _phases.Keys.Order()], _systemSafeSince);
        }

        if (_phaseSafe.IsEmpty)
        {
            return SafeStateStatus.None;
        }

        var since = _phaseSafe.Keys.Select(p => _phases[p].InvalidSince).Where(s => s is not null).Min();
        return new SafeStateStatus(true, SafeStateScope.Phases, _phaseSafe.Values.First(), [.. _phaseSafe.Keys.Order()], since);
    }

    private readonly record struct StageKey(DeviceName Device, int Stage);

    private sealed record Limit(GuardTarget Target, ReasonCode Reason, Phase? Phase);

    private enum DemandKind
    {
        None,
        One,
        All,
    }

    private readonly record struct Demand(DemandKind Kind, ReasonCode Reason)
    {
        public static Demand None => new(DemandKind.None, ReasonCode.GuardTierExceeded);

        public static Demand One(ReasonCode reason) => new(DemandKind.One, reason);

        public static Demand All(ReasonCode reason) => new(DemandKind.All, reason);

        /// <summary>The more severe demand; on a tie the tier (guard_tier_exceeded) wins over the budget.</summary>
        public Demand Max(Demand other) =>
            other.Kind > Kind || (other.Kind == Kind && other.Reason == ReasonCode.GuardTierExceeded) ? other : this;
    }

    /// <summary>The measured current with the relief of everything already held taken off.</summary>
    private readonly record struct EffectiveCurrent(Amperes Signed, double Multiple);

    /// <summary>One evaluation's view: the setup, the latest device states, the time.</summary>
    private sealed class Context
    {
        private readonly GuardEngine _engine;
        private readonly ImmutableDictionary<(DeviceName, int), DeviceStateSample> _samples;
        private readonly ImmutableDictionary<DeviceName, int> _ranks;
        private readonly DeviceStateInvalidReason _absent;

        public Context(GuardEngine engine, ProtectionSetup setup, ProtectionInputs? protection, DateTimeOffset now)
        {
            _engine = engine;
            Setup = setup;
            Now = now;
            var current = protection is { Setup: not null } && !protection.DeviceStates.IsDefault;
            _samples = current
                ? protection!.DeviceStates.ToImmutableDictionary(s => (s.Device, s.Stage))
                : ImmutableDictionary<(DeviceName, int), DeviceStateSample>.Empty;
            _absent = current ? DeviceStateInvalidReason.Missing : DeviceStateInvalidReason.ConfigurationInvalid;
            _ranks = ShedOrder.Rank(setup.Devices).Select((d, i) => (d, i)).ToImmutableDictionary(x => x.d, x => x.i + 1);
        }

        public ProtectionSetup Setup { get; }

        public DateTimeOffset Now { get; }

        public DeviceStateSample? Sample(DeviceName device, int stage) => _samples.GetValueOrDefault((device, stage));

        /// <summary>Why a stage's state is unknown; null when it is fresh.</summary>
        public DeviceStateInvalidReason? InvalidReason(DeviceName device, int stage) =>
            Sample(device, stage) is { } sample ? sample.InvalidReason : _absent;

        public AppliedState? Applied(DeviceName device, int stage) => Sample(device, stage) is { Fresh: true } s ? s.Applied : null;

        /// <summary>
        /// Relief of every stage already held, on this phase, while its fresh state still shows load:
        /// the "as if applied" estimate that keeps one overload from shedding device after device in
        /// shadow mode. A stage whose state is unknown gives none: it may already draw nothing, so
        /// holding it may relieve nothing (unknown counts as on only when choosing what to shed). A
        /// device that cannot switch off can only come down to its minimum, whatever it is held to.
        /// Shedding cannot lower an export current, so an exporting phase gets no relief in its multiple.
        /// </summary>
        public EffectiveCurrent Effective(Phase phase)
        {
            var guard = _engine._phases[phase];
            if (guard.Reading is not { } reading)
            {
                return new EffectiveCurrent(Amperes.Zero, 0);
            }

            var relief = 0.0;
            foreach (var (key, limit) in _engine._limits)
            {
                if (Setup.Device(key.Device) is not { } device || device.Stages[key.Stage - 1] is not { } stage || !stage.Phases.Contains(phase))
                {
                    continue;
                }

                var applied = Applied(key.Device, key.Stage);
                if (device.Type == DeviceType.Modulating)
                {
                    if (applied is SetpointApplied s)
                    {
                        var setpoint = s.Setpoint.Value;
                        relief += limit.Target == GuardTarget.Off && device.CanSwitchOff ? setpoint : Math.Max(0, setpoint - device.Range!.Min.Value);
                    }
                }
                else if (applied is RelayApplied { On: true })
                {
                    relief += stage.RatedCurrent.Value;
                }
            }

            var signed = reading.Signed.Value;
            return signed >= 0
                ? new EffectiveCurrent(new Amperes(Math.Max(0, signed - relief)), Math.Max(0, signed - relief) / Setup.Breaker.Rating.Value)
                : new EffectiveCurrent(new Amperes(signed - relief), Setup.Breaker.MultipleOf(reading.Magnitude));
        }

        public IReadOnlyList<ShedCandidate> Candidates(Phase phase) =>
        [
            .. Setup.Devices.Where(d => d.IsOn(phase)).Select(d => new ShedCandidate(
                d,
                [.. d.Stages.Select(s => Applied(d.Id, s.Index))],
                _engine._lockouts.UnderMinOn(d.Id, Now),
                _engine._limits
                    .Where(l => l.Key.Device == d.Id && l.Value.Target != GuardTarget.Released)
                    .ToImmutableDictionary(l => l.Key.Stage, l => l.Value.Target == GuardTarget.Off ? StageLimit.Off : StageLimit.AtMinimum))),
        ];

        public DecisionInputs PhaseInputs(Phase phase) => new(PhaseDecision(phase), null, null, null, null);

        public DecisionInputs Inputs(DeviceSpec device, int stage, Phase phase)
        {
            var sample = Sample(device.Id, stage);
            return new DecisionInputs(
                PhaseDecision(phase),
                new DeviceDecisionInputs(
                    sample is { Fresh: true } ? sample.Applied : null,
                    sample is { Fresh: true },
                    InvalidReason(device.Id, stage),
                    _ranks.TryGetValue(device.Id, out var rank) ? rank : null,
                    device.ValuePerKwh,
                    device.ShedPriority,
                    _engine._lockouts.MinOnRemaining(device.Id, stage, Now),
                    _engine._lockouts.MinOffRemaining(device.Id, stage, Now),
                    device.CanSwitchOff),
                null,
                null,
                null);
        }

        public PhaseGuardStatus PhaseStatus(Phase phase)
        {
            var guard = _engine._phases[phase];
            var profile = Setup.Breaker;
            var threshold = Setup.ReserveThreshold(phase);
            var continuous = profile.ContinuousCurrent;
            var budget = guard.Budget(Now, Setup.Budget.RecoveryTime);
            var trend = !guard.Valid || guard.ChainBroken ? BudgetTrend.Held
                : guard.Consuming ? BudgetTrend.Consuming
                : budget < 1 ? BudgetTrend.Recovering
                : BudgetTrend.Full;
            if (!guard.Valid || guard.Reading is not { } reading)
            {
                return new PhaseGuardStatus(
                    phase, false, guard.InvalidReason, null, null, null, null, null, continuous, threshold, null, null, null, null,
                    budget, trend, OverloadBudget.RecoveryRemaining(budget, Setup.Budget.RecoveryTime), false, guard.ReductionRequested,
                    guard.Seen ? PhaseGuardState.SafeState : PhaseGuardState.Unknown, null, null);
            }

            var measured = guard.Multiple;
            var band = profile.BandIndex(measured);
            var effective = Effective(phase);
            var effectiveReaction = profile.Band(profile.BandIndex(effective.Multiple)).Reaction;
            var toReserve = threshold - effective.Signed;
            var lowBudget = OverloadBudget.IsBelow(budget, Setup.Budget.StopIncreasesBelow);
            var increases = !_engine._killSwitch
                && _engine._systemSafe is null
                && !lowBudget
                && toReserve > Amperes.Zero
                && (effectiveReaction == TierReaction.MayAdd || reading.Direction == FlowDirection.Export);
            var state = profile.Band(band).Reaction == TierReaction.ShedAllOnPhase
                || effectiveReaction == TierReaction.ShedOneImmediately
                || guard.AnyTimerExpired
                || OverloadBudget.IsBelow(budget, Setup.Budget.ShedOneBelow) ? PhaseGuardState.Shedding
                : effectiveReaction is TierReaction.ReduceOrdinaryCycle or TierReaction.ReduceNextCycle || guard.ReductionRequested ? PhaseGuardState.Reducing
                : effectiveReaction == TierReaction.Hold || lowBudget ? PhaseGuardState.Hold
                : PhaseGuardState.Normal;
            return new PhaseGuardStatus(
                phase,
                true,
                null,
                reading.Signed,
                effective.Signed,
                measured,
                band,
                profile.Band(band).Reaction,
                continuous,
                threshold,
                threshold - reading.Signed,
                continuous - reading.Signed,
                toReserve,
                continuous - effective.Signed,
                budget,
                trend,
                OverloadBudget.RecoveryRemaining(budget, Setup.Budget.RecoveryTime),
                increases,
                guard.ReductionRequested,
                state,
                reading.Direction,
                reading.ReportedAt);
        }

        public DeviceGuardStatus DeviceStatus(DeviceSpec device)
        {
            var unsafePhases = device.Phases.Where(p => !_engine._phases[p].Valid).ToImmutableArray();
            var unsafeStages = device.Stages.Where(s => Sample(device.Id, s.Index) is not { Fresh: true }).Select(s => s.Index).ToImmutableArray();
            var limits = device.Stages
                .Select(s => _engine._limits.TryGetValue(new StageKey(device.Id, s.Index), out var l) ? l : null)
                .ToList();
            var state = !unsafePhases.IsEmpty || !unsafeStages.IsEmpty ? DeviceGuardState.Unsafe
                : limits.Any(l => l?.Target == GuardTarget.Off) ? DeviceGuardState.Shed
                : limits.Any(l => l?.Target == GuardTarget.AtMinimum) ? DeviceGuardState.Reduced
                : DeviceGuardState.Allowed;
            var reason = limits.FirstOrDefault(l => l?.Target == GuardTarget.Off)?.Reason
                ?? limits.FirstOrDefault(l => l is not null)?.Reason
                ?? (state == DeviceGuardState.Unsafe ? ReasonCode.GuardStaleSignal : (ReasonCode?)null);
            return new DeviceGuardStatus(
                device.Id,
                device.Type,
                device.Phases,
                state,
                reason,
                [.. device.Stages.Select((s, i) => new DeviceStageStatus(
                    s.Index,
                    Applied(device.Id, s.Index),
                    Sample(device.Id, s.Index) is { Fresh: true },
                    InvalidReason(device.Id, s.Index),
                    limits[i]?.Target))],
                _ranks.TryGetValue(device.Id, out var rank) ? rank : null,
                device.ValuePerKwh,
                device.ShedPriority,
                device.NoShed,
                device.CanSwitchOff,
                device.Stages.Select(s => _engine._lockouts.MinOnRemaining(device.Id, s.Index, Now)).Max(),
                device.Stages.Select(s => _engine._lockouts.MinOffRemaining(device.Id, s.Index, Now)).Max(),
                unsafePhases,
                unsafeStages);
        }

        private PhaseDecisionInputs PhaseDecision(Phase phase)
        {
            var guard = _engine._phases[phase];
            var profile = Setup.Breaker;
            var threshold = Setup.ReserveThreshold(phase);
            var budget = guard.Budget(Now, Setup.Budget.RecoveryTime);
            if (!guard.Valid || guard.Reading is not { } reading)
            {
                return new PhaseDecisionInputs(phase, false, guard.InvalidReason, null, null, null, null, null, budget, profile.ContinuousCurrent, threshold, null, null, null);
            }

            var band = profile.BandIndex(guard.Multiple);
            return new PhaseDecisionInputs(
                phase,
                true,
                null,
                reading.Signed,
                Effective(phase).Signed,
                guard.Multiple,
                band,
                profile.Band(band).Reaction,
                budget,
                profile.ContinuousCurrent,
                threshold,
                threshold - reading.Signed,
                reading.Direction,
                reading.ReportedAt);
        }
    }
}
