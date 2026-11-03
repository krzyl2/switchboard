using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Guard;

/// <summary>
/// The guard loop of 04 §2, deterministic: it never reads a clock, every call is handed
/// <c>now</c>, and it is driven by exactly one caller. In order of precedence:
/// <list type="number">
/// <item>Kill switch (SAFE-03): every controlled device held off.</item>
/// <item>Safe state (SAFE-01): a phase without valid data holds every device on it off; missing or
/// invalid configuration, no data on any phase, or shutdown holds everything off. A device whose
/// own state is not fresh is unsafe and held off too (SIG-05), and so is a member of a circuit
/// group whose sub-meter is not valid.</item>
/// <item>Tier, band timer and budget reactions (BRK-01 … BRK-03): the most severe source wins, on
/// every main phase and every circuit-group phase (CONN-03), each with its own budget.</item>
/// <item>Reserve threshold: dispatch is asked to reduce.</item>
/// <item>The headroom envelope is published for dispatch.</item>
/// </list>
/// The guard only ever reduces: it holds stages off or at minimum and later lets go of them
/// (released after persistence_up_cycles good boundaries); it never starts anything.
/// Magnitude drives tiers, band timers and the budget (a breaker heats in either direction);
/// the signed current drives the reserve rule and the envelope (adding load to an exporting
/// phase lowers its current). A reaction due on an exporting phase takes no step.
/// Tier steps, the reserve request, increases and the envelope use the estimate of the load model
/// (measured current plus every counted in-flight change, <see cref="LoadModel"/>); the open band,
/// the budget, the band timers and everything displayed as measured use the measured current.
/// </summary>
public sealed class GuardEngine
{
    private readonly ILockoutView _lockouts;
    private readonly IInFlightView _inFlight;
    private readonly ILogger<GuardEngine> _logger;
    private readonly PhaseGuards _phases = new();
    private readonly HoldLedger _holds = new();
    private ProtectionSetup? _setup;
    private bool _killSwitch;
    private ReasonCode? _systemSafe;
    private SafeStateCause? _systemSafeCause;
    private DateTimeOffset? _systemSafeSince;
    private ImmutableDictionary<ConstraintKey, ReasonCode> _phaseSafe = ImmutableDictionary<ConstraintKey, ReasonCode>.Empty;
    private DateTimeOffset? _lastNow;
    private long? _lastBoundary;
    private bool _shutdown;
    private bool _cleanShutdown;

    public GuardEngine(ILockoutView lockouts, IInFlightView inFlight, ILogger<GuardEngine> logger)
    {
        _lockouts = lockouts;
        _inFlight = inFlight;
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
        _phases.Restore(memory, now);
    }

    /// <summary>What to persist now (budgets at the last evaluation); never a lockout.</summary>
    public GuardMemory Export() =>
        _phases.Export(_lastNow ?? _phases.RestoredAt, _setup?.Budget.RecoveryTime ?? TimeSpan.MaxValue, _cleanShutdown);

    public GuardEvaluation Evaluate(GuardInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        var intents = new List<GuardIntent>();
        var events = new List<GuardEvent>();
        var protection = input.Protection;

        if (_lastNow is { } last && now < last)
        {
            _logger.LogWarning("The clock stepped back by {Step}; overload budgets hold until new samples arrive", last - now);
            if (_setup is { } stepped)
            {
                _phases.OnClockStepBack(last, stepped.Budget.RecoveryTime);
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
            return Finish(input, now, intents, events, null, 0, false, configurationValid);
        }

        var cycleIndex = (now - DateTimeOffset.UnixEpoch).Ticks / active.Timing.Cycle.Ticks;
        var atBoundary = _lastBoundary is { } previous && cycleIndex > previous;
        _lastBoundary = cycleIndex;

        var load = new LoadEstimator(_phases, _holds, _inFlight, _lockouts, active, protection, now, input.Mode);
        var context = new Context(load, new GuardStatusProjector(load, _phases, _holds, _lockouts));
        var timerSteps = new HashSet<ConstraintKey>();
        foreach (var phase in active.Phases)
        {
            // At shutdown the data keeps its real validity, so the shutdown records carry the last
            // phase picture; the safe state comes from the shutdown itself.
            var key = ConstraintKey.Main(phase);
            var entry = protection?.For(phase);
            var reading = entry?.Current ?? (protection?.Topology == Topology.ThreePhaseSummed && entry is { DataValid: true } ? protection.Total?.Current : null);
            var valid = configurationValid && entry is { DataValid: true } && reading is not null;
            var reason = valid ? null : configurationValid ? entry?.InvalidReason : ProtectionInvalidReason.ConfigurationInvalid;
            if (_phases[key].Update(valid, reading, reason, active.CurrentMaxAge.GetValueOrDefault(phase), active.Budget.RecoveryTime, now))
            {
                timerSteps.Add(key);
            }
        }

        foreach (var group in active.Groups)
        {
            foreach (var phase in group.Phases)
            {
                var key = new ConstraintKey(group.Id, phase);
                var (valid, reading, reason) = context.Load.GroupReading(group, phase, configurationValid);
                if (_phases[key].Update(valid, reading, reason, group.CurrentMaxAge.GetValueOrDefault(phase), active.Budget.RecoveryTime, now))
                {
                    timerSteps.Add(key);
                }
            }
        }

        var invalid = active.Phases.Where(p => !_phases[ConstraintKey.Main(p)].Valid).ToImmutableArray();
        var (systemReason, cause) = _shutdown ? (ReasonCode.SafeState, SafeStateCause.Shutdown)
            : !configurationValid ? (ReasonCode.SafeState, ConfigurationCause(protection))
            : invalid.Length == active.Phases.Length ? (SafeReason(_phases[ConstraintKey.Main(invalid[0])].InvalidReason), MeterCause(invalid))
            : ((ReasonCode?)null, (SafeStateCause?)null);
        var invalidKeys = PhaseGuards.Keys(active).Where(k => !_phases[k].Valid).ToImmutableArray();
        UpdateSafeStateEvents(systemReason, cause, invalidKeys, now, events, context);

        HoldUnsafeDevices(context, systemReason, cause, intents);
        foreach (var key in PhaseGuards.Keys(active).Where(k => systemReason is null && _phases[k].Valid))
        {
            React(context, key, atBoundary, timerSteps.Contains(key), intents, events);
        }

        if (atBoundary && !_shutdown)
        {
            foreach (var key in PhaseGuards.Keys(active))
            {
                BoundaryEvents(context, key, systemReason, events);
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

        return Finish(input, now, intents, events, context, cycleIndex, atBoundary, configurationValid);
    }

    /// <summary>The next moment the guard must evaluate without new input: a cycle boundary, a band timer, a budget threshold.</summary>
    public DateTimeOffset? NextDeadline(DateTimeOffset now) =>
        _setup is not { } setup ? null : _phases.NextDeadline(setup, now);

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
        invalid.All(p => _phases[ConstraintKey.Main(p)].InvalidReason == ProtectionInvalidReason.SourceDisconnected) ? SafeStateCause.SourceDisconnected : SafeStateCause.MeterInvalid;

    private void ApplySetup(ProtectionSetup setup)
    {
        if (ReferenceEquals(setup, _setup))
        {
            return;
        }

        foreach (var key in _holds.DropUndeclared(setup))
        {
            // The device is no longer declared: it can never be commanded again, so there is nothing to hold.
            _logger.LogInformation("Device {Device} stage {Stage} is no longer declared; protection no longer holds it", key.Device, key.Stage);
        }

        foreach (var key in _phases.Apply(setup))
        {
            _phaseSafe = _phaseSafe.Remove(key);
        }

        _setup = setup;
    }

    /// <summary>
    /// Safe-state records on change: system-wide when nothing is valid, per phase (or circuit-group
    /// phase) otherwise. A system-wide record names its cause, and a new cause is a new record (the
    /// log says the add-on was stopping even when an invalid configuration already held everything off).
    /// </summary>
    private void UpdateSafeStateEvents(ReasonCode? systemReason, SafeStateCause? cause, ImmutableArray<ConstraintKey> invalid, DateTimeOffset now, List<GuardEvent> events, Context? context)
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

        foreach (var (key, reason) in _phaseSafe.Where(p => !invalid.Contains(p.Key)))
        {
            events.Add(KeyEvent(key, DecisionAction.SafeStateExited, reason, context.Project.PhaseInputs(key)));
            _phaseSafe = _phaseSafe.Remove(key);
        }

        if (systemReason is not null)
        {
            return;
        }

        foreach (var key in invalid)
        {
            var reason = SafeReason(_phases[key].InvalidReason);
            if (!_phaseSafe.TryGetValue(key, out var recorded) || recorded != reason)
            {
                events.Add(KeyEvent(key, DecisionAction.SafeStateEntered, reason, context.Project.PhaseInputs(key)));
                _phaseSafe = _phaseSafe.SetItem(key, reason);
            }
        }
    }

    private static GuardEvent KeyEvent(ConstraintKey key, DecisionAction action, ReasonCode reason, DecisionInputs inputs) =>
        new(key.Group is null ? DecisionScope.Phase : DecisionScope.Group, key.Phase, action, reason, inputs) { Group = key.Group };

    /// <summary>
    /// Kill switch, then safe state, then a phase or circuit-group phase without valid data, then a
    /// device's own stale state: every stage held off, whatever its type.
    /// </summary>
    private void HoldUnsafeDevices(Context context, ReasonCode? systemReason, SafeStateCause? cause, List<GuardIntent> intents)
    {
        foreach (var device in context.Setup.Devices.OrderBy(d => d.Id))
        {
            ReasonCode? reason = null;
            ConstraintKey? key = null;
            SafeStateCause? holdCause = null;
            var invalidKey = context.Setup.ConstraintsOf(device).Cast<ConstraintKey?>().FirstOrDefault(k => !_phases[k!.Value].Valid);
            if (_killSwitch)
            {
                reason = ReasonCode.KillSwitch;
            }
            else if (systemReason is { } system)
            {
                reason = system;
                holdCause = cause;
            }
            else if (invalidKey is { } bad)
            {
                key = bad;
                reason = SafeReason(_phases[bad].InvalidReason);
            }
            else if (!context.Load.StateFresh(device))
            {
                // Its own state is unknown: it may be on, so it is held off (SIG-05); being unknown,
                // the hold counts as no relief beyond a measured draw.
                reason = ReasonCode.GuardStaleSignal;
            }

            if (reason is not { } holding)
            {
                continue;
            }

            foreach (var stage in device.Stages)
            {
                Hold(context, device, stage.Index, GuardTarget.Off, holding, key, intents, holdCause);
            }
        }
    }

    /// <summary>Tier, band timer and budget reactions on one phase (or circuit-group phase) with valid data; the most severe wins.</summary>
    private void React(Context context, ConstraintKey key, bool atBoundary, bool timerStep, List<GuardIntent> intents, List<GuardEvent> events)
    {
        var setup = context.Setup;
        var guard = _phases[key];
        var profile = guard.Profile;
        var measured = guard.Multiple;
        var effective = context.Load.Effective(key);
        var effectiveBand = profile.ReactionAt(effective.Multiple);
        var budget = guard.Budget(context.Now, setup.Budget.RecoveryTime);

        if (atBoundary)
        {
            guard.OrdinaryStreak = effectiveBand >= TierReaction.ReduceOrdinaryCycle ? guard.OrdinaryStreak + 1 : 0;
        }

        var demand = Demand.None;
        if (profile.ReactionAt(measured) == TierReaction.ShedAllOnPhase)
        {
            // From the measured current, never the estimate: relief that does not materialize must
            // not hide the open band. Idempotent, so no cascade.
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

        var direction = guard.Reading!.Direction;
        var exporting = direction == FlowDirection.Export;
        var overloaded = profile.ReactionAt(measured) >= TierReaction.ReduceOrdinaryCycle
            || OverloadBudget.IsBelow(budget, setup.Budget.ShedOneBelow);

        // One record per exporting excursion: only a confirmed import or the end of the overload
        // clears the latch, so a poll whose direction is still pending does not count as the end.
        if (direction == FlowDirection.Import || !overloaded)
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
                events.Add(KeyEvent(key, DecisionAction.CannotReduceExporting, demand.Reason, context.Project.PhaseInputs(key)));
            }

            guard.ExportBlocked = true;
            return;
        }

        var steps = ShedOrder.For(key.Phase, context.Load.Candidates(key));
        if (demand.Kind == DemandKind.One)
        {
            if (steps.FirstOrDefault() is { } step && setup.Device(step.Device) is { } device)
            {
                Hold(context, device, step.Stage, step.Action == ShedAction.SwitchOff ? GuardTarget.Off : GuardTarget.AtMinimum, demand.Reason, key, intents, trigger: new ReactionTrigger(effective.Signed, effectiveBand));
            }

            return;
        }

        // Everything controlled on the phase: straight to off; a device that cannot switch off to its floor.
        foreach (var (name, stage) in steps.Select(s => (s.Device, s.Stage)).Distinct())
        {
            var device = setup.Device(name)!;
            Hold(context, device, stage, device.CanSwitchOff ? GuardTarget.Off : GuardTarget.AtMinimum, demand.Reason, key, intents, trigger: new ReactionTrigger(effective.Signed, effectiveBand));
        }
    }

    /// <summary>
    /// Boundary-only records with persistence, so a current hovering at a threshold does not flood
    /// the log: the reserve request after persistence_down_cycles boundaries above In − reserve and
    /// its clearing after persistence_up_cycles below; the same for budget-driven increase stops.
    /// </summary>
    private void BoundaryEvents(Context context, ConstraintKey key, ReasonCode? systemReason, List<GuardEvent> events)
    {
        var guard = _phases[key];
        var setup = context.Setup;
        if (!guard.Valid)
        {
            guard.ReleaseStreak = 0;
            guard.OrdinaryStreak = 0;
            return;
        }

        var timing = setup.Timing;
        var threshold = context.Load.ReserveThreshold(key);
        var effective = context.Load.Effective(key);
        var over = effective.Signed > threshold;
        guard.ReserveOverStreak = over ? guard.ReserveOverStreak + 1 : 0;
        guard.ReserveClearStreak = over ? 0 : guard.ReserveClearStreak + 1;
        if (!guard.ReductionRequested && guard.ReserveOverStreak >= timing.PersistenceDownCycles)
        {
            guard.ReductionRequested = true;
            events.Add(KeyEvent(key, DecisionAction.RequestReduction, ReasonCode.ReserveThreshold, context.Project.PhaseInputs(key)));
        }
        else if (guard.ReductionRequested && guard.ReserveClearStreak >= timing.PersistenceUpCycles)
        {
            guard.ReductionRequested = false;
            events.Add(KeyEvent(key, DecisionAction.ReductionRequestCleared, ReasonCode.HeadroomAvailable, context.Project.PhaseInputs(key)));
        }

        var budget = guard.Budget(context.Now, setup.Budget.RecoveryTime);
        var low = OverloadBudget.IsBelow(budget, setup.Budget.StopIncreasesBelow);
        guard.BudgetLowStreak = low ? guard.BudgetLowStreak + 1 : 0;
        guard.BudgetOkStreak = low ? 0 : guard.BudgetOkStreak + 1;
        if (!guard.IncreasesStopped && guard.BudgetLowStreak >= timing.PersistenceDownCycles)
        {
            guard.IncreasesStopped = true;
            events.Add(KeyEvent(key, DecisionAction.StopIncreases, ReasonCode.GuardBudgetLow, context.Project.PhaseInputs(key)));
        }
        else if (guard.IncreasesStopped && guard.BudgetOkStreak >= timing.PersistenceUpCycles)
        {
            guard.IncreasesStopped = false;
            events.Add(KeyEvent(key, DecisionAction.IncreasesAllowed, ReasonCode.HeadroomAvailable, context.Project.PhaseInputs(key)));
        }

        // A boundary counts towards release only when everything is comfortably inside: measured
        // current at or below the reserve threshold and the continuous current, budget not low.
        var good = systemReason is null
            && !_killSwitch
            && guard.Reading!.Signed <= threshold
            && !guard.Profile.IsAboveContinuous(guard.Multiple)
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
        var held = intents
            .Where(i => setup.Device(i.Device) is not null)
            .SelectMany(i => setup.ConstraintsOf(setup.Device(i.Device)!))
            .ToHashSet();
        foreach (var key in PhaseGuards.Keys(setup))
        {
            var guard = _phases[key];
            if (systemReason is not null || _killSwitch || !guard.Valid || held.Contains(key))
            {
                guard.ReleaseStreak = 0;
            }
        }
    }

    /// <summary>
    /// A held stage is let go once every phase of its device (and of its circuit group) has had
    /// persistence_up_cycles good boundaries and the device's own state is fresh. Leaving safe state
    /// or releasing the kill switch never lets go by itself (asymmetric safety, 04 §7).
    /// </summary>
    private void Release(Context context, ReasonCode? systemReason, List<GuardIntent> intents)
    {
        if (systemReason is not null || _killSwitch)
        {
            return;
        }

        var up = context.Setup.Timing.PersistenceUpCycles;
        foreach (var key in _holds.Held())
        {
            var device = context.Setup.Device(key.Device)!;
            if (context.Setup.ConstraintsOf(device).All(k => _phases[k].Valid && _phases[k].ReleaseStreak >= up) && context.Load.StateFresh(device))
            {
                var group = _holds.GroupOf(key);
                _holds.Release(key);
                intents.Add(new GuardIntent(
                    key.Device,
                    StageOf(device, key.Stage),
                    GuardTarget.Released,
                    ReasonCode.HeadroomAvailable,
                    null,
                    IntentUrgency.Protective,
                    null,
                    context.Project.Inputs(device, key.Stage, ConstraintKey.Main(device.Phases[0])))
                {
                    Group = group,
                });
            }
        }
    }

    /// <summary>Holds a stage at a target; records an intent only when the target or the reason changes, and never relaxes Off to AtMinimum.</summary>
    /// <param name="key">The phase (or circuit-group phase) whose condition produced it; null for kill switch and system-wide safe state.</param>
    /// <param name="cause">The system-wide safe state's cause, for a hold that state made.</param>
    /// <param name="trigger">For a tier or budget reaction, the estimate it was decided on (before any of its holds relieved it) and that estimate's band reaction.</param>
    private void Hold(Context context, DeviceSpec device, int stage, GuardTarget target, ReasonCode reason, ConstraintKey? key, List<GuardIntent> intents, SafeStateCause? cause = null, ReactionTrigger? trigger = null)
    {
        var stageKey = new StageKey(device.Id, stage);
        if (!_holds.Hold(stageKey, target, reason, key))
        {
            return;
        }

        var overridden = target == GuardTarget.Off && _lockouts.MinOnRemaining(device.Id, stage, context.Now) > TimeSpan.Zero
            ? ReasonCode.LockoutMinOn
            : (ReasonCode?)null;
        intents.Add(new GuardIntent(
            device.Id,
            StageOf(device, stage),
            target,
            reason,
            key?.Phase,
            IntentUrgency.Protective,
            overridden,
            context.Project.Inputs(device, stage, key ?? ConstraintKey.Main(device.Phases[0]), trigger) with { SafeStateCause = cause })
        {
            Group = key?.Group,
        });
    }

    private static int? StageOf(DeviceSpec device, int stage) => device.Stages.Length > 1 ? stage : null;

    private GuardEvaluation Finish(GuardInput input, DateTimeOffset now, List<GuardIntent> intents, List<GuardEvent> events, Context? context, long cycleIndex, bool atBoundary, bool configurationValid)
    {
        var setup = context?.Setup;
        var holdsAll = _killSwitch || _systemSafe is not null;
        var status = GuardStatusProjector.Status(context?.Project, input, now, GuardStatusProjector.SafeState(_systemSafe, _systemSafeSince, _phases, _phaseSafe), holdsAll);
        return new GuardEvaluation(now, status, [.. intents], [.. events], setup?.Timing, cycleIndex)
        {
            Envelope = context?.Project.Envelope(holdsAll) ?? DispatchEnvelope.Empty,
            Setup = _setup,
            ConfigurationValid = configurationValid,
            AtBoundary = atBoundary,
            Limits = _holds.Current(),
        };
    }

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

    /// <summary>One evaluation's views: what the load is (the estimator) and what is shown of it (the projector).</summary>
    private sealed record Context(LoadEstimator Load, GuardStatusProjector Project)
    {
        public ProtectionSetup Setup => Load.Setup;

        public DateTimeOffset Now => Load.Now;
    }
}
