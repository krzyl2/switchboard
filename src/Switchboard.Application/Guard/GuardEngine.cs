using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
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
    private readonly Dictionary<ConstraintKey, PhaseGuard> _phases = [];
    private readonly Dictionary<StageKey, Limit> _limits = [];
    private ProtectionSetup? _setup;
    private GuardMemory? _restored;
    private DateTimeOffset _restoredAt;
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
            .SetItems(_phases.Where(p => p.Key.Group is null).Select(p => KeyValuePair.Create(p.Key.Phase, new BudgetMemory(p.Value.Budget(at, recovery), at))));
        var groups = _restored?.GroupBudgets ?? ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>>.Empty;
        foreach (var group in _phases.Where(p => p.Key.Group is not null).GroupBy(p => p.Key.Group!.Value))
        {
            groups = groups.SetItem(
                group.Key,
                (groups.GetValueOrDefault(group.Key) ?? ImmutableDictionary<Phase, BudgetMemory>.Empty)
                    .SetItems(group.Select(p => KeyValuePair.Create(p.Key.Phase, new BudgetMemory(p.Value.Budget(at, recovery), at)))));
        }

        return new GuardMemory(budgets, _cleanShutdown) { GroupBudgets = groups };
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
            return Finish(input, now, intents, events, null, 0, false, configurationValid);
        }

        var cycleIndex = (now - DateTimeOffset.UnixEpoch).Ticks / active.Timing.Cycle.Ticks;
        var atBoundary = _lastBoundary is { } previous && cycleIndex > previous;
        _lastBoundary = cycleIndex;

        var context = new Context(this, active, protection, now, input.Mode);
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
                var (valid, reading, reason) = context.GroupReading(group, phase, configurationValid);
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
        var invalidKeys = Keys(active).Where(k => !_phases[k].Valid).ToImmutableArray();
        UpdateSafeStateEvents(systemReason, cause, invalidKeys, now, events, context);

        HoldUnsafeDevices(context, systemReason, cause, intents);
        foreach (var key in Keys(active).Where(k => systemReason is null && _phases[k].Valid))
        {
            React(context, key, atBoundary, timerSteps.Contains(key), intents, events);
        }

        if (atBoundary && !_shutdown)
        {
            foreach (var key in Keys(active))
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

    private static IEnumerable<ConstraintKey> Keys(ProtectionSetup setup) =>
        setup.Phases.Select(ConstraintKey.Main).Concat(setup.Groups.SelectMany(g => g.Phases.Select(p => new ConstraintKey(g.Id, p))));

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

        foreach (var key in _limits.Keys.Where(k => setup.Device(k.Device) is not { } d || k.Stage > d.Stages.Length).ToList())
        {
            // The device is no longer declared: it can never be commanded again, so there is nothing to hold.
            _logger.LogInformation("Device {Device} stage {Stage} is no longer declared; protection no longer holds it", key.Device, key.Stage);
            _limits.Remove(key);
        }

        var keys = Keys(setup).ToHashSet();
        foreach (var key in _phases.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _phases.Remove(key);
            _phaseSafe = _phaseSafe.Remove(key);
        }

        foreach (var key in keys)
        {
            var breaker = key.Group is { } id ? setup.Group(id)!.Breaker : setup.Breaker;
            if (_phases.TryGetValue(key, out var guard))
            {
                guard.Reconfigure(breaker);
            }
            else
            {
                _phases[key] = new PhaseGuard(key.Phase, RestoredBudget(key, setup.Budget.RecoveryTime), breaker);
            }
        }

        _setup = setup;
    }

    private double RestoredBudget(ConstraintKey key, TimeSpan recovery)
    {
        var saved = _restored is not { } memory ? null
            : key.Group is { } group ? memory.GroupBudgets.GetValueOrDefault(group)?.GetValueOrDefault(key.Phase)
            : memory.Budgets.GetValueOrDefault(key.Phase);
        if (saved is null)
        {
            return 1;
        }

        var budget = Math.Clamp(saved.Budget, 0, 1);
        return _restored!.CleanShutdown && _restoredAt >= saved.At
            ? Math.Min(1, budget + ((double)(_restoredAt - saved.At).Ticks / recovery.Ticks))
            : budget;
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
            events.Add(KeyEvent(key, DecisionAction.SafeStateExited, reason, context.PhaseInputs(key)));
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
                events.Add(KeyEvent(key, DecisionAction.SafeStateEntered, reason, context.PhaseInputs(key)));
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
            var invalidKey = context.KeysOf(device).Cast<ConstraintKey?>().FirstOrDefault(k => !_phases[k!.Value].Valid);
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
            else if (!context.StateFresh(device))
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
        var effective = context.Effective(key);
        var effectiveBand = profile.Band(profile.BandIndex(effective.Multiple)).Reaction;
        var budget = guard.Budget(context.Now, setup.Budget.RecoveryTime);

        if (atBoundary)
        {
            guard.OrdinaryStreak = effectiveBand >= TierReaction.ReduceOrdinaryCycle ? guard.OrdinaryStreak + 1 : 0;
        }

        var demand = Demand.None;
        if (profile.Band(profile.BandIndex(measured)).Reaction == TierReaction.ShedAllOnPhase)
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
        var overloaded = profile.Band(profile.BandIndex(measured)).Reaction >= TierReaction.ReduceOrdinaryCycle
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
                events.Add(KeyEvent(key, DecisionAction.CannotReduceExporting, demand.Reason, context.PhaseInputs(key)));
            }

            guard.ExportBlocked = true;
            return;
        }

        var steps = ShedOrder.For(key.Phase, context.Candidates(key));
        if (demand.Kind == DemandKind.One)
        {
            if (steps.FirstOrDefault() is { } step && setup.Device(step.Device) is { } device)
            {
                Hold(context, device, step.Stage, step.Action == ShedAction.SwitchOff ? GuardTarget.Off : GuardTarget.AtMinimum, demand.Reason, key, intents);
            }

            return;
        }

        // Everything controlled on the phase: straight to off; a device that cannot switch off to its floor.
        foreach (var (name, stage) in steps.Select(s => (s.Device, s.Stage)).Distinct())
        {
            var device = setup.Device(name)!;
            Hold(context, device, stage, device.CanSwitchOff ? GuardTarget.Off : GuardTarget.AtMinimum, demand.Reason, key, intents);
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
        var threshold = context.ReserveThreshold(key);
        var effective = context.Effective(key);
        var over = effective.Signed > threshold;
        guard.ReserveOverStreak = over ? guard.ReserveOverStreak + 1 : 0;
        guard.ReserveClearStreak = over ? 0 : guard.ReserveClearStreak + 1;
        if (!guard.ReductionRequested && guard.ReserveOverStreak >= timing.PersistenceDownCycles)
        {
            guard.ReductionRequested = true;
            events.Add(KeyEvent(key, DecisionAction.RequestReduction, ReasonCode.ReserveThreshold, context.PhaseInputs(key)));
        }
        else if (guard.ReductionRequested && guard.ReserveClearStreak >= timing.PersistenceUpCycles)
        {
            guard.ReductionRequested = false;
            events.Add(KeyEvent(key, DecisionAction.ReductionRequestCleared, ReasonCode.HeadroomAvailable, context.PhaseInputs(key)));
        }

        var budget = guard.Budget(context.Now, setup.Budget.RecoveryTime);
        var low = OverloadBudget.IsBelow(budget, setup.Budget.StopIncreasesBelow);
        guard.BudgetLowStreak = low ? guard.BudgetLowStreak + 1 : 0;
        guard.BudgetOkStreak = low ? 0 : guard.BudgetOkStreak + 1;
        if (!guard.IncreasesStopped && guard.BudgetLowStreak >= timing.PersistenceDownCycles)
        {
            guard.IncreasesStopped = true;
            events.Add(KeyEvent(key, DecisionAction.StopIncreases, ReasonCode.GuardBudgetLow, context.PhaseInputs(key)));
        }
        else if (guard.IncreasesStopped && guard.BudgetOkStreak >= timing.PersistenceUpCycles)
        {
            guard.IncreasesStopped = false;
            events.Add(KeyEvent(key, DecisionAction.IncreasesAllowed, ReasonCode.HeadroomAvailable, context.PhaseInputs(key)));
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
            .SelectMany(i => KeysOf(setup, setup.Device(i.Device)!))
            .ToHashSet();
        foreach (var key in Keys(setup))
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
        foreach (var key in _limits.Keys.OrderBy(k => k.Device).ThenBy(k => k.Stage).ToList())
        {
            var device = context.Setup.Device(key.Device)!;
            if (KeysOf(context.Setup, device).All(k => _phases[k].Valid && _phases[k].ReleaseStreak >= up) && context.StateFresh(device))
            {
                var group = _limits[key].Group;
                _limits.Remove(key);
                intents.Add(new GuardIntent(
                    key.Device,
                    StageOf(device, key.Stage),
                    GuardTarget.Released,
                    ReasonCode.HeadroomAvailable,
                    null,
                    IntentUrgency.Protective,
                    null,
                    context.Inputs(device, key.Stage, ConstraintKey.Main(device.Phases[0])))
                {
                    Group = group,
                });
            }
        }
    }

    /// <summary>Holds a stage at a target; records an intent only when the target or the reason changes, and never relaxes Off to AtMinimum.</summary>
    /// <param name="key">The phase (or circuit-group phase) whose condition produced it; null for kill switch and system-wide safe state.</param>
    /// <param name="cause">The system-wide safe state's cause, for a hold that state made.</param>
    private void Hold(Context context, DeviceSpec device, int stage, GuardTarget target, ReasonCode reason, ConstraintKey? key, List<GuardIntent> intents, SafeStateCause? cause = null)
    {
        var stageKey = new StageKey(device.Id, stage);
        if (_limits.TryGetValue(stageKey, out var held) && (held.Target == GuardTarget.Off && target == GuardTarget.AtMinimum || (held.Target == target && held.Reason == reason)))
        {
            return;
        }

        _limits[stageKey] = new Limit(target, reason, key?.Phase, key?.Group);
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
            context.Inputs(device, stage, key ?? ConstraintKey.Main(device.Phases[0])) with { SafeStateCause = cause })
        {
            Group = key?.Group,
        });
    }

    private static int? StageOf(DeviceSpec device, int stage) => device.Stages.Length > 1 ? stage : null;

    /// <summary>Every constraint a device draws on: its main phases and, as a group member, its group's phases.</summary>
    private static IEnumerable<ConstraintKey> KeysOf(ProtectionSetup setup, DeviceSpec device)
    {
        foreach (var phase in device.Phases)
        {
            yield return ConstraintKey.Main(phase);
        }

        if (device.CircuitGroup is { } group && setup.Group(group) is { } spec)
        {
            foreach (var phase in device.Phases.Where(spec.Phases.Contains))
            {
                yield return new ConstraintKey(group, phase);
            }
        }
    }

    private GuardEvaluation Finish(GuardInput input, DateTimeOffset now, List<GuardIntent> intents, List<GuardEvent> events, Context? context, long cycleIndex, bool atBoundary, bool configurationValid)
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
            context is null ? [] : [.. setup!.Phases.Select(p => context.PhaseStatus(ConstraintKey.Main(p)))],
            context is null ? [] : [.. setup!.Devices.Select(context.DeviceStatus)])
        {
            Groups = context is null ? [] : [.. setup!.Groups.SelectMany(g => g.Phases.Select(p => new GroupGuardStatus(g.Id, context.PhaseStatus(new ConstraintKey(g.Id, p)))))],
        };
        return new GuardEvaluation(now, status, [.. intents], [.. events], setup?.Timing, cycleIndex)
        {
            Envelope = context?.Envelope() ?? DispatchEnvelope.Empty,
            Setup = _setup,
            ConfigurationValid = configurationValid,
            AtBoundary = atBoundary,
            Limits = CurrentLimits(),
        };
    }

    private ImmutableDictionary<DeviceName, ImmutableDictionary<int, StageLimit>> CurrentLimits() =>
        _limits
            .Where(l => l.Value.Target != GuardTarget.Released)
            .GroupBy(l => l.Key.Device)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableDictionary(l => l.Key.Stage, l => l.Value.Target == GuardTarget.Off ? StageLimit.Off : StageLimit.AtMinimum));

    private SafeStateStatus CurrentSafeState()
    {
        if (_systemSafe is { } system)
        {
            return new SafeStateStatus(true, SafeStateScope.All, system, [.. _phases.Keys.Where(k => k.Group is null).Select(k => k.Phase).Order()], _systemSafeSince);
        }

        var phases = _phaseSafe.Where(p => p.Key.Group is null).ToList();
        if (phases.Count == 0)
        {
            return SafeStateStatus.None;
        }

        var since = phases.Select(p => _phases[p.Key].InvalidSince).Where(s => s is not null).Min();
        return new SafeStateStatus(true, SafeStateScope.Phases, phases[0].Value, [.. phases.Select(p => p.Key.Phase).Order()], since);
    }

    private readonly record struct StageKey(DeviceName Device, int Stage);

    private sealed record Limit(GuardTarget Target, ReasonCode Reason, Phase? Phase, GroupName? Group);

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

    /// <summary>The estimated current: measured plus the counted in-flight changes, with its multiple of In.</summary>
    private readonly record struct EffectiveCurrent(Amperes Signed, double Multiple, Amperes InFlight);

    /// <summary>How the load model sees one device right now: applied state, resolved target, and whether its change counts.</summary>
    private readonly record struct DeviceView(DeviceSpec Spec, DeviceLevel? Reported, DeviceLevel Target, bool Counts, InFlightEntry? Ledger);

    /// <summary>One evaluation's view: the setup, the latest device states and draws, the ledger, the time.</summary>
    private sealed class Context
    {
        private readonly GuardEngine _engine;
        private readonly ProtectionInputs? _protection;
        private readonly ImmutableDictionary<(DeviceName, int), DeviceStateSample> _samples;
        private readonly ImmutableDictionary<(DeviceName, Phase), DeviceDrawSample> _draws;
        private readonly ImmutableDictionary<DeviceName, int> _ranks;
        private readonly DeviceStateInvalidReason _absent;
        private readonly bool _active;

        public Context(GuardEngine engine, ProtectionSetup setup, ProtectionInputs? protection, DateTimeOffset now, ModeStatus mode)
        {
            _engine = engine;
            _protection = protection;
            Setup = setup;
            Now = now;
            _active = mode.Effective == OperatingMode.Active;
            var current = protection is { Setup: not null } && !protection.DeviceStates.IsDefault;
            _samples = current
                ? protection!.DeviceStates.ToImmutableDictionary(s => (s.Device, s.Stage))
                : ImmutableDictionary<(DeviceName, int), DeviceStateSample>.Empty;
            _draws = current && !protection!.DeviceDraws.IsDefault
                ? protection.DeviceDraws.ToImmutableDictionary(d => (d.Device, d.Phase))
                : ImmutableDictionary<(DeviceName, Phase), DeviceDrawSample>.Empty;
            _absent = current ? DeviceStateInvalidReason.Missing : DeviceStateInvalidReason.ConfigurationInvalid;
            _ranks = ShedOrder.Rank(setup.Devices).Select((d, i) => (d, i)).ToImmutableDictionary(x => x.d, x => x.i + 1);
        }

        public ProtectionSetup Setup { get; }

        public DateTimeOffset Now { get; }

        public DeviceStateSample? Sample(DeviceName device, int stage) => _samples.GetValueOrDefault((device, stage));

        public DeviceDrawSample? Draw(DeviceName device, Phase phase) => _draws.GetValueOrDefault((device, phase));

        /// <summary>Why a stage's state is unknown; null when it is fresh.</summary>
        public DeviceStateInvalidReason? InvalidReason(DeviceName device, int stage) =>
            Sample(device, stage) is { } sample ? sample.InvalidReason : _absent;

        public AppliedState? Applied(DeviceName device, int stage) => Sample(device, stage) is { Fresh: true } s ? s.Applied : null;

        /// <summary>Every stage state (and a stop actuator's) is fresh.</summary>
        public bool StateFresh(DeviceSpec device) =>
            device.Stages.All(s => Sample(device.Id, s.Index) is { Fresh: true })
            && (!device.HasStopActuator || Sample(device.Id, DeviceStateSample.StopStage) is { Fresh: true });

        public IEnumerable<ConstraintKey> KeysOf(DeviceSpec device) => GuardEngine.KeysOf(Setup, device);

        public Amperes ReserveThreshold(ConstraintKey key) =>
            key.Group is { } group ? Setup.Group(group)!.ReserveThreshold(key.Phase) : Setup.ReserveThreshold(key.Phase);

        /// <summary>
        /// A device as the load model sees it: its applied state, its target (the ledger's, else the
        /// applied state, with protection's holds applied, including those made earlier in this
        /// evaluation) and whether the change counts: in shadow mode while target and applied state
        /// differ, in active mode while its command window is open (then measurement rules).
        /// </summary>
        public DeviceView View(DeviceSpec device)
        {
            var reported = DeviceLevels.Reported(device, stage => Sample(device.Id, stage));
            var ledger = _engine._inFlight.For(device.Id);
            var target = DeviceLevels.Cap(device, ledger?.Target ?? reported ?? DeviceLevels.Max(device), LimitsOf(device.Id));
            var differs = !target.Equals(reported ?? DeviceLevels.Max(device));
            var counts = !_active ? differs : ledger is null || !target.Equals(ledger.Target) ? differs : ledger.WindowOpen;
            return new DeviceView(device, reported, target, counts, ledger);
        }

        /// <summary>The device's share of the measured current on a phase, or (an estimated group) its worst-case term.</summary>
        public Amperes Basis(DeviceView view, Phase phase, bool groupTerm)
        {
            if (_active && view.Ledger is { WindowOpen: true } ledger && view.Target.Equals(ledger.Target))
            {
                var captured = groupTerm ? ledger.GroupTermAtCommand : ledger.ContributionAtCommand;
                if (captured.TryGetValue(phase, out var atCommand))
                {
                    return atCommand;
                }
            }

            var draw = Draw(view.Spec.Id, phase);
            return groupTerm ? LoadModel.GroupTerm(view.Spec, phase, view.Reported, draw) : LoadModel.Contribution(view.Spec, phase, view.Reported, draw);
        }

        /// <summary>
        /// The counted change on a phase. While an active command's window is open it is the change
        /// from the level the command started at, against the contribution captured then: the relay
        /// may report "on" before the meter sees its load, and the estimate must not drop it early.
        /// </summary>
        public Amperes Delta(DeviceView view, Phase phase, bool groupTerm)
        {
            if (!view.Counts)
            {
                return Amperes.Zero;
            }

            var from = _active && view.Ledger is { WindowOpen: true, From: { } started } ledger && view.Target.Equals(ledger.Target) ? started : view.Reported;
            return LoadModel.InFlightDelta(view.Spec, phase, view.Target, from, Basis(view, phase, groupTerm));
        }

        private bool Estimated(ConstraintKey key) => key.Group is { } group && Setup.Group(group) is { SubMetered: false };

        private IEnumerable<DeviceSpec> DevicesOn(ConstraintKey key) =>
            Setup.Devices.Where(d => d.IsOn(key.Phase) && (key.Group is not { } group || Setup.Group(group)!.Members.Contains(d.Id)));

        /// <summary>
        /// A circuit group phase's reading: the sub-meter when there is one; otherwise the worst-case
        /// sum of its members (fresh draw, relay rated when on or unknown, setpoint or its maximum
        /// when unknown), a new sample whenever the main meter or a member input reports.
        /// </summary>
        public (bool Valid, CurrentReading? Reading, ProtectionInvalidReason? Reason) GroupReading(CircuitGroupSpec group, Phase phase, bool configurationValid)
        {
            if (!configurationValid)
            {
                return (false, null, ProtectionInvalidReason.ConfigurationInvalid);
            }

            var entry = _protection?.Groups.IsDefault == false ? _protection.Groups.FirstOrDefault(g => g.Group == group.Id && g.Phase == phase) : null;
            if (group.SubMetered)
            {
                return entry is { DataValid: true, Current: { } current } ? (true, current, null) : (false, null, entry?.InvalidReason ?? ProtectionInvalidReason.CurrentMissing);
            }

            var main = _protection?.For(phase);
            if (entry is not { DataValid: true } || main is not { DataValid: true, Current: { } mainReading })
            {
                return (false, null, entry?.InvalidReason ?? main?.InvalidReason ?? ProtectionInvalidReason.CurrentAwaiting);
            }

            var total = Amperes.Zero;
            var reportedAt = mainReading.ReportedAt;
            foreach (var device in DevicesOn(new ConstraintKey(group.Id, phase)))
            {
                var reported = DeviceLevels.Reported(device, stage => Sample(device.Id, stage));
                total += LoadModel.GroupTerm(device, phase, reported, Draw(device.Id, phase));
                foreach (var at in device.Stages.Select(s => Sample(device.Id, s.Index)?.ReportedAt).Append(Draw(device.Id, phase)?.ReportedAt))
                {
                    if (at is { } time && time > reportedAt && time <= Now)
                    {
                        reportedAt = time;
                    }
                }
            }

            return (true, new CurrentReading(total, total, FlowDirection.Import, DirectionSource.SignedSignal, reportedAt), null);
        }

        /// <summary>
        /// The estimate I_est on a constraint: measured plus every counted in-flight change of the
        /// devices on it. A would-start adds its ceiling, a would-stop relieves at most what the
        /// device really draws (a charger at 20 A with no car relieves nothing), and the holds made
        /// earlier in this evaluation count at once. On an exporting phase the multiple is the larger
        /// of measured and estimated magnitude: shedding there raises the current.
        /// </summary>
        public EffectiveCurrent Effective(ConstraintKey key)
        {
            var guard = _engine._phases[key];
            if (guard.Reading is not { } reading)
            {
                return new EffectiveCurrent(Amperes.Zero, 0, Amperes.Zero);
            }

            var estimated = Estimated(key);
            var delta = Amperes.Zero;
            foreach (var device in DevicesOn(key))
            {
                delta += Delta(View(device), key.Phase, estimated);
            }

            var rating = guard.Profile.Rating.Value;
            var signed = reading.Signed.Value + delta.Value;
            return reading.Signed.Value >= 0
                ? new EffectiveCurrent(new Amperes(Math.Max(0, signed)), Math.Max(0, signed) / rating, delta)
                : new EffectiveCurrent(new Amperes(signed), Math.Max(reading.Magnitude.Value, Math.Abs(signed)) / rating, delta);
        }

        /// <summary>
        /// Shed candidates from the same view as the estimate: a device whose change counts is seen at
        /// its target (a would-started heater is shed before a running device worth more), any other
        /// at its applied state (unknown counts as on at full rating).
        /// </summary>
        public IReadOnlyList<ShedCandidate> Candidates(ConstraintKey key) =>
        [
            .. DevicesOn(key).Select(d =>
            {
                var view = View(d);
                ImmutableArray<AppliedState?> applied = view.Counts
                    ? d.IsSetpoint
                        ? [new SetpointApplied(view.Target.Enabled ? view.Target.Setpoint ?? Amperes.Zero : Amperes.Zero)]
                        : [.. view.Target.Relays.Select(on => (AppliedState?)new RelayApplied(on))]
                    : d.IsSetpoint && view.Reported is { Enabled: false }
                        ? [new SetpointApplied(Amperes.Zero)]
                        : [.. d.Stages.Select(s => Applied(d.Id, s.Index))];
                // A fresh draw tells a step that relieves nothing (an idle charger at a high
                // setpoint); a device seen at its target is relieved by its in-flight change instead.
                var draw = !view.Counts && d.HasCurrentDraw(key.Phase) && Draw(d.Id, key.Phase) is { Fresh: true, Current: { } current } ? current : (Amperes?)null;
                return new ShedCandidate(d, applied, _engine._lockouts.UnderMinOn(d.Id, Now), LimitsOf(d.Id)) { Draw = draw };
            }),
        ];

        public ImmutableDictionary<int, StageLimit> LimitsOf(DeviceName device) =>
            _engine._limits
                .Where(l => l.Key.Device == device && l.Value.Target != GuardTarget.Released)
                .ToImmutableDictionary(l => l.Key.Stage, l => l.Value.Target == GuardTarget.Off ? StageLimit.Off : StageLimit.AtMinimum);

        public DecisionInputs PhaseInputs(ConstraintKey key) => new(PhaseDecision(key), null, null, null, null);

        public DecisionInputs Inputs(DeviceSpec device, int stage, ConstraintKey key)
        {
            var sample = Sample(device.Id, stage);
            return new DecisionInputs(
                PhaseDecision(key),
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

        public PhaseGuardStatus PhaseStatus(ConstraintKey key)
        {
            var guard = _engine._phases[key];
            var profile = guard.Profile;
            var threshold = ReserveThreshold(key);
            var continuous = profile.ContinuousCurrent;
            var budget = guard.Budget(Now, Setup.Budget.RecoveryTime);
            var trend = !guard.Valid || guard.ChainBroken ? BudgetTrend.Held
                : guard.Consuming ? BudgetTrend.Consuming
                : budget < 1 ? BudgetTrend.Recovering
                : BudgetTrend.Full;
            if (!guard.Valid || guard.Reading is not { } reading)
            {
                return new PhaseGuardStatus(
                    key.Phase, false, guard.InvalidReason, null, null, null, null, null, continuous, threshold, null, null, null, null,
                    budget, trend, OverloadBudget.RecoveryRemaining(budget, Setup.Budget.RecoveryTime), false, guard.ReductionRequested,
                    guard.Seen ? PhaseGuardState.SafeState : PhaseGuardState.Unknown, null, null);
            }

            var measured = guard.Multiple;
            var band = profile.BandIndex(measured);
            var effective = Effective(key);
            var effectiveReaction = profile.Band(profile.BandIndex(effective.Multiple)).Reaction;
            var toReserve = threshold - effective.Signed;
            var lowBudget = OverloadBudget.IsBelow(budget, Setup.Budget.StopIncreasesBelow);
            var state = profile.Band(band).Reaction == TierReaction.ShedAllOnPhase
                || effectiveReaction == TierReaction.ShedOneImmediately
                || guard.AnyTimerExpired
                || OverloadBudget.IsBelow(budget, Setup.Budget.ShedOneBelow) ? PhaseGuardState.Shedding
                : effectiveReaction is TierReaction.ReduceOrdinaryCycle or TierReaction.ReduceNextCycle || guard.ReductionRequested ? PhaseGuardState.Reducing
                : effectiveReaction == TierReaction.Hold || lowBudget ? PhaseGuardState.Hold
                : PhaseGuardState.Normal;
            return new PhaseGuardStatus(
                key.Phase,
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
                IncreasesAllowed(key, effective, toReserve, lowBudget),
                guard.ReductionRequested,
                state,
                reading.Direction,
                reading.ReportedAt);
        }

        private bool IncreasesAllowed(ConstraintKey key, EffectiveCurrent effective, Amperes toReserve, bool lowBudget) =>
            toReserve > Amperes.Zero && BreakerAllows(key, effective, lowBudget);

        /// <summary>Everything an increase needs except the reserve threshold: what a manual override is held to.</summary>
        private bool BreakerAllows(ConstraintKey key, EffectiveCurrent effective, bool lowBudget)
        {
            var guard = _engine._phases[key];
            var profile = guard.Profile;
            var effectiveReaction = profile.Band(profile.BandIndex(effective.Multiple)).Reaction;
            return !_engine._killSwitch
                && _engine._systemSafe is null
                && guard.Valid
                && !lowBudget
                && (effectiveReaction == TierReaction.MayAdd || guard.Reading!.Direction == FlowDirection.Export);
        }

        public DeviceGuardStatus DeviceStatus(DeviceSpec device)
        {
            var unsafePhases = device.Phases.Where(p => !_engine._phases[ConstraintKey.Main(p)].Valid).ToImmutableArray();
            var unsafeStages = device.Stages.Where(s => Sample(device.Id, s.Index) is not { Fresh: true }).Select(s => s.Index).ToImmutableArray();
            var groupUnsafe = KeysOf(device).Any(k => k.Group is not null && !_engine._phases[k].Valid);
            var limits = device.Stages
                .Select(s => _engine._limits.TryGetValue(new StageKey(device.Id, s.Index), out var l) ? l : null)
                .ToList();
            var state = !unsafePhases.IsEmpty || !unsafeStages.IsEmpty || groupUnsafe || !StateFresh(device) ? DeviceGuardState.Unsafe
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

        /// <summary>
        /// The envelope for dispatch (03): per main and group phase E = min(In − reserve, may_add
        /// top × In) − I_est and the guard's permissions; per device its contribution, estimate and
        /// in-flight change, so dispatch never reads a current of its own (SIG-04).
        /// </summary>
        public DispatchEnvelope Envelope()
        {
            var entries = ImmutableArray.CreateBuilder<EnvelopeEntry>();
            foreach (var key in GuardEngine.Keys(Setup))
            {
                var guard = _engine._phases[key];
                var budget = guard.Budget(Now, Setup.Budget.RecoveryTime);
                var lowBudget = OverloadBudget.IsBelow(budget, Setup.Budget.StopIncreasesBelow);
                if (!guard.Valid || guard.Reading is null)
                {
                    entries.Add(new EnvelopeEntry(key, false, null, null, null, Amperes.Zero, false, guard.ReductionRequested, lowBudget, null));
                    continue;
                }

                var effective = Effective(key);
                var threshold = ReserveThreshold(key);
                var top = guard.Profile.Rating * guard.Profile.MayAddUpTo;
                entries.Add(new EnvelopeEntry(
                    key,
                    true,
                    Amperes.Min(threshold, top) - effective.Signed,
                    top - effective.Signed,
                    effective.Signed,
                    effective.InFlight,
                    IncreasesAllowed(key, effective, threshold - effective.Signed, lowBudget),
                    guard.ReductionRequested,
                    lowBudget,
                    guard.Reading.ReportedAt)
                {
                    OverrideAllowed = BreakerAllows(key, effective, lowBudget),
                });
            }

            var devices = ImmutableArray.CreateBuilder<DeviceLoad>();
            foreach (var device in Setup.Devices)
            {
                var view = View(device);
                var contribution = device.Phases.ToImmutableDictionary(p => p, p => LoadModel.Contribution(device, p, view.Reported, Draw(device.Id, p)));
                var estimate = ImmutableDictionary.CreateBuilder<ConstraintKey, Amperes>();
                var inFlight = ImmutableDictionary.CreateBuilder<ConstraintKey, Amperes>();
                foreach (var key in KeysOf(device))
                {
                    var groupTerm = Estimated(key);
                    var delta = Delta(view, key.Phase, groupTerm);
                    var basis = groupTerm ? LoadModel.GroupTerm(device, key.Phase, view.Reported, Draw(device.Id, key.Phase)) : contribution[key.Phase];
                    estimate[key] = basis + delta;
                    inFlight[key] = delta;
                }

                var draws = device.CurrentDrawPhases.IsDefault ? [] : device.CurrentDrawPhases.Select(p => Draw(device.Id, p)).ToList();
                devices.Add(new DeviceLoad(
                    device.Id,
                    view.Reported,
                    contribution,
                    estimate.ToImmutable(),
                    inFlight.ToImmutable(),
                    LimitsOf(device.Id),
                    draws.All(d => d is { Fresh: true }),
                    draws.Where(d => d?.ReportedAt is not null).ToImmutableDictionary(d => d!.Phase, d => d!.ReportedAt!.Value)));
            }

            return new DispatchEnvelope(entries.ToImmutable(), devices.ToImmutable());
        }

        private PhaseDecisionInputs PhaseDecision(ConstraintKey key)
        {
            var guard = _engine._phases[key];
            var profile = guard.Profile;
            var threshold = ReserveThreshold(key);
            var budget = guard.Budget(Now, Setup.Budget.RecoveryTime);
            if (!guard.Valid || guard.Reading is not { } reading)
            {
                return new PhaseDecisionInputs(key.Phase, false, guard.InvalidReason, null, null, null, null, null, budget, profile.ContinuousCurrent, threshold, null, null, null);
            }

            var band = profile.BandIndex(guard.Multiple);
            return new PhaseDecisionInputs(
                key.Phase,
                true,
                null,
                reading.Signed,
                Effective(key).Signed,
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
