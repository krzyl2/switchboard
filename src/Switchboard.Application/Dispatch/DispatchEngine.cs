using System.Collections.Immutable;
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

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The dispatch loop of 04 §3, deterministic: it never reads a clock, every call is handed
/// <c>now</c>, and it runs right after the guard in the same step, so protection always comes
/// first. It reads watts and volts from its own port, amperes only from the guard's envelope
/// (SIG-04), and produces one wish per device at each cycle boundary:
/// <list type="bullet">
/// <item>Headroom A per main and group phase = E + the estimates of the devices on it; devices
/// are walked in start order (the reverse of the shed order, DISP-03; rotation groups by phase
/// ranking, DISP-10) and take what fits with start or stop hysteresis (<see cref="Allocator"/>).</item>
/// <item>Increases need a boundary, persistence_up_cycles agreeing boundaries, the guard's
/// permission on every constraint, no open settling window there, fewer open increase windows
/// than max_simultaneous_increases (DISP-04, DISP-08), valid dispatch inputs and fresh draws, and a
/// responsive device (DISP-07).</item>
/// <item>Decreases are unlimited (DISP-08): at once when the guard asks for a reduction (dispatch
/// then reduces until E ≥ 0), when a device worth more takes the headroom in the same cycle, for an
/// unresponsive device or an override; otherwise after persistence_down_cycles boundaries.</item>
/// <item>Manual overrides outrank allocation, hysteresis, rank and the unresponsive exclusion, but
/// a forced increase still needs the guard's permission and must fit under the breaker allowance
/// (may_add top × In − I_est): protection is never outranked.</item>
/// </list>
/// Stage changes are limited to max_step_change_stages per cycle (07-PITFALLS). Lockouts are
/// applied by the effector gate, which records a deferred intent.
/// </summary>
public sealed class DispatchEngine
{
    private readonly Dictionary<DeviceName, DeviceState> _devices = [];
    private readonly PhaseRanking _ranking = new();
    private readonly ForeignLoadTracker _foreign = new();
    private ImmutableArray<DispatchWish> _wishes = [];
    private ImmutableArray<DeviceDispatchState> _states = [];
    private DispatchSetup? _setup;
    private long _overrideVersion;
    private DateTimeOffset? _lastNow;

    public DispatchEvaluation Evaluate(DispatchInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_lastNow is { } last && now < last)
        {
            // A backward clock step: report times from before it are not comparable with what comes after.
            _foreign.Reset();
        }

        _lastNow = now;
        var guard = input.Guard;
        if (input.Setup is not { } setup || guard.Setup is not { } protection || !guard.ConfigurationValid)
        {
            // Without a valid configuration dispatch wants nothing; protection holds the last devices off.
            _wishes = [];
            _states = [];
            foreach (var state in _devices.Values)
            {
                state.ConfirmStreak = 0;
                state.DecreaseStreak = 0;
            }

            return DispatchEvaluation.Empty(now);
        }

        Sync(setup);
        var events = new List<DispatchEvent>();
        var ours = new Dictionary<Phase, Watts?>();
        var devicePowers = ImmutableArray.CreateBuilder<DevicePhasePower>();
        ObserveForeignLoad(input, setup, events, ours, devicePowers);

        var overridesChanged = input.Overrides.Version != _overrideVersion;
        var changed = overridesChanged
            ? setup.Devices.Where(d => input.Overrides.For(d.Id) != DeviceOverride.Auto || _devices[d.Id].Override != OverrideMode.Auto).Select(d => d.Id).ToHashSet()
            : [];
        _overrideVersion = input.Overrides.Version;
        if (guard.AtBoundary)
        {
            var headroom = setup.Phases
                .Select(p => (Phase: p, Entry: guard.Envelope.For(ConstraintKey.Main(p))))
                .Where(x => x.Entry is { DataValid: true, Available: not null })
                .ToDictionary(x => x.Phase, x => x.Entry!.Available!.Value.Value);
            if (headroom.Count > 0)
            {
                _ranking.Observe(PhaseRanking.Propose(headroom), setup.Timing.PhaseRankingStabilityCycles);
            }
        }

        if (guard.AtBoundary || changed.Count > 0)
        {
            Allocate(input, setup, protection, now, guard.AtBoundary, changed);
        }
        else
        {
            _wishes = [.. _wishes.Select(w => w with { Fresh = false })];
        }

        foreach (var device in setup.Devices)
        {
            _devices[device.Id].Override = input.Overrides.For(device.Id).Mode;
        }

        var phases = setup.Phases.Select(p =>
        {
            var (foreign, baseline) = _foreign.Last(p);
            var rank = _ranking.Adopted.IndexOf(p);
            return new PhaseDispatchState(p, foreign, baseline, rank < 0 ? null : rank + 1) { Ours = ours.GetValueOrDefault(p) };
        });
        return new DispatchEvaluation(now, _wishes, [.. events], _states, [.. phases], _ranking.Adopted) { DevicePowers = devicePowers.ToImmutable() };
    }

    /// <summary>Dispatch adds no deadline of its own: it acts at the guard's cycle boundaries.</summary>
    public static DateTimeOffset? NextDeadline(DateTimeOffset now) => null;

    private void Sync(DispatchSetup setup)
    {
        if (ReferenceEquals(setup, _setup))
        {
            return;
        }

        foreach (var gone in _devices.Keys.Where(k => setup.Device(k) is null).ToList())
        {
            _devices.Remove(gone);
        }

        foreach (var device in setup.Devices)
        {
            var fingerprint = SpecIdentity.Fingerprint(device.Spec);
            if (!_devices.TryGetValue(device.Id, out var state) || state.Fingerprint != fingerprint)
            {
                _devices[device.Id] = new DeviceState { Fingerprint = fingerprint };
            }
        }

        if (_setup is null || !_setup.Phases.SequenceEqual(setup.Phases))
        {
            _ranking.Reset();
            _foreign.Reset();
        }

        _setup = setup;
    }

    /// <summary>
    /// Foreign load per phase (04 §4): house load minus our devices (their power draw, else a
    /// relay's rated power while reported on; unknown while a device on the phase is a setpoint
    /// device without a power draw). In active mode a commanded change the reported state does not
    /// show yet counts as ours (the meter may see the load before the relay reports), so our own
    /// command is never foreign, until its verification refuted it: a relay that measurably did not
    /// follow is not counted as on, so a real kettle is not hidden behind it and the end of its
    /// command is no "foreign" rise that would excuse its next mismatch. What it counted (ours per
    /// phase, each device's power and where it came from) is handed back for read-only consumers.
    /// </summary>
    private void ObserveForeignLoad(DispatchInput input, DispatchSetup setup, List<DispatchEvent> events, Dictionary<Phase, Watts?> oursByPhase, ImmutableArray<DevicePhasePower>.Builder devicePowers)
    {
        var active = input.Mode.Effective == OperatingMode.Active;
        foreach (var phase in setup.Phases)
        {
            var dispatch = input.Inputs.For(phase);
            Watts? ours = Watts.Zero;
            foreach (var device in setup.Devices.Where(d => d.Spec.IsOn(phase)))
            {
                var load = input.Guard.Envelope.Device(device.Id);
                var power = input.Inputs.DevicePowers.IsDefault ? null : input.Inputs.DevicePowers.FirstOrDefault(p => p.Device == device.Id && p.Phase == phase);
                Watts? own = device.HasPowerDraw(phase) ? power is { Fresh: true, Power: { } w } ? w : null
                    : device.Spec.IsSetpoint ? null
                    : load?.Reported is { } reported ? device.RatedPower(reported, phase)
                    : null;
                // A commanded change counts as ours until the reported state shows it (the meter may
                // see the load first): only in active mode, where something was really applied.
                if (active && input.Ledger.Entry(device.Id) is { TargetCommanded: true, CommandRefuted: false, Target: { } target }
                    && !device.Spec.IsSetpoint && !device.HasPowerDraw(phase) && load?.Reported is { } shown && !shown.Equals(target))
                {
                    own = own + device.RatedPower(target, phase) - device.RatedPower(shown, phase);
                }

                ours = ours is { } sum && own is { } add ? sum + add : null;
                devicePowers.Add(new DevicePhasePower(
                    device.Id,
                    phase,
                    own,
                    own is null ? PowerSource.Unknown : device.HasPowerDraw(phase) ? PowerSource.Measured : PowerSource.RatedModel));
            }

            oursByPhase[phase] = ours;
            Watts? foreign = dispatch is { DataValid: true, Load: { } houseLoad } && ours is { } controlled ? houseLoad - controlled : null;
            if (_foreign.Observe(phase, foreign, dispatch?.PowerReportedAt, setup.ForeignLoad))
            {
                var (_, baseline) = _foreign.Last(phase);
                events.Add(new DispatchEvent(
                    DecisionScope.Phase,
                    phase,
                    DecisionAction.ForeignLoadDetected,
                    ReasonCode.ReserveThreshold,
                    DecisionInputs.None with
                    {
                        Dispatch = new DispatchDecisionInputs(
                            [new ConstraintHeadroom(ConstraintKey.Main(phase), input.Guard.Envelope.For(ConstraintKey.Main(phase))?.Available, null)],
                            setup.StartHysteresis,
                            setup.StopHysteresis,
                            null,
                            ImmutableDictionary<Phase, Amperes>.Empty,
                            ImmutableDictionary<ConstraintKey, Amperes>.Empty,
                            null,
                            new VerificationInputs(baseline?.Value ?? 0, foreign!.Value.Value, MeasureUnit.Watts, Verdict.Inconclusive),
                            TimeSpan.Zero,
                            TimeSpan.Zero,
                            null,
                            OverrideMode.Auto),
                    }));
            }
        }
    }

    private void Allocate(DispatchInput input, DispatchSetup setup, ProtectionSetup protection, DateTimeOffset now, bool atBoundary, HashSet<DeviceName> overrideChanged)
    {
        var guard = input.Guard;
        var envelope = guard.Envelope;
        var order = StartOrder.For(setup.Devices.Select(d => d.Spec), id => setup.Device(id)?.RotationGroup, _ranking.Adopted);
        var plans = new List<Plan>();
        var openWindows = setup.Devices
            .Select(d => (Device: d, Window: input.Ledger.Entry(d.Id)?.Window))
            .Where(x => x.Window is { Kind: WindowKind.Command })
            .ToList();
        var increaseBudget = setup.Timing.MaxSimultaneousIncreases - openWindows.Count(x => x.Window!.Increase);

        for (var i = 0; i < order.Length; i++)
        {
            var spec = order[i];
            var device = setup.Device(spec.Id)!;
            var load = envelope.Device(spec.Id);
            var ledger = input.Ledger.Entry(spec.Id);
            var limits = load?.Limits ?? ImmutableDictionary<int, StageLimit>.Empty;
            var reported = load?.Reported;
            var levels = DeviceLevels.All(spec);
            var basis = DeviceLevels.Cap(spec, ledger?.Target ?? reported ?? DeviceLevels.Off(spec), limits);
            var basisIndex = IndexAtOrBelow(spec, levels, basis);
            var capIndex = IndexAtOrBelow(spec, levels, DeviceLevels.Cap(spec, DeviceLevels.Max(spec), limits));
            var constraints = Constraints(spec, protection);
            var mode = input.Overrides.For(spec.Id).Mode;
            var state = _devices[spec.Id];

            var guardAllows = constraints.All(k => envelope.For(k) is { IncreasesAllowed: true });
            var windowFree = !openWindows.Any(w => SharesConstraint(w.Device.Spec, spec, protection));
            var dataValid = spec.Phases.All(p => input.Inputs.For(p) is { DataValid: true }) && (load?.DrawFresh ?? false) && PowerDrawsFresh(device, input.Inputs);
            var unresponsive = ledger?.Unresponsive == true;
            var probeDue = unresponsive && ledger!.RetryAt is { } retry && now >= retry;
            var known = reported is not null;
            var gates = atBoundary && guardAllows && windowFree && dataValid && known && (!unresponsive || probeDue);
            var provisional = gates && state.ConfirmStreak + 1 >= setup.Timing.PersistenceUpCycles;
            var lockedMin = Math.Min(LockedMinimum(spec, levels, basis, input.Lockouts, now), capIndex);

            var plan = new Plan(device, i + 1, levels, basis, basisIndex, capIndex, constraints, mode, unresponsive && !probeDue)
            {
                GuardAllows = guardAllows,
                WindowFree = windowFree,
                DataValid = dataValid && known,
                Gates = gates,
                MinAllowed = lockedMin,
                MaxAllowed = provisional ? capIndex : Math.Min(basisIndex, capIndex),
            };

            if (mode == OverrideMode.ForceOff)
            {
                plan.MaxAllowed = Math.Min(Math.Max(0, lockedMin), capIndex);
                plan.Forced = 0;
            }
            else if (mode == OverrideMode.ForceOn)
            {
                // Outranks allocation, hysteresis, rank and unresponsive exclusion; never protection:
                // the guard must allow increases and the ceiling must fit under the breaker allowance.
                var forced = capIndex;
                if (forced <= basisIndex)
                {
                    // Pinned at its forced level: reserved before the walk, so no device is planned
                    // into the headroom the forced device keeps using.
                    plan.Forced = forced;
                    plan.MinAllowed = forced;
                    plan.MaxAllowed = forced;
                }
                else
                {
                    var breakerAllows = constraints.All(k => envelope.For(k) is { OverrideAllowed: true });
                    var room = constraints.All(k => envelope.For(k) is { BreakerRoom: { } r }
                        && DeviceLevels.Ceiling(spec, levels[forced], k.Phase) - (load?.EstimateOn(k) ?? Amperes.Zero) <= r);
                    plan.BlockedBy = !breakerAllows
                        ? constraints.Any(k => envelope.For(k) is { BudgetLow: true }) ? ReasonCode.GuardBudgetLow : ReasonCode.HeadroomInsufficient
                        : !room ? ReasonCode.HeadroomInsufficient
                        : null;
                    var can = plan.BlockedBy is null && atBoundary && windowFree && known;
                    plan.Forced = can ? forced : basisIndex;
                    plan.ForcedIncrease = can;
                    plan.MinAllowed = plan.Forced.Value;
                    plan.MaxAllowed = plan.Forced.Value;
                }
            }
            else if (plan.Excluded)
            {
                // Unresponsive (DISP-07): off, and excluded from increases until its probe is due.
                plan.MaxAllowed = Math.Min(Math.Max(0, lockedMin), capIndex);
            }

            plan.MinAllowed = Math.Min(plan.MinAllowed, plan.MaxAllowed);
            plans.Add(plan);
        }

        var available = Available(envelope, plans, setup);
        var result = Run(plans, available, setup);

        // DISP-08: only as many increases as the limit leaves, in start order; the rest wait for
        // their own settling window, and the walk is redone without them. Holding a device back
        // frees what it had reserved, so the redone walk can turn another device's keep into an
        // increase: the count is repeated until no increase is left beyond the limit.
        var granted = new HashSet<DeviceName>();
        var excluded = true;
        while (excluded)
        {
            var budget = increaseBudget - granted.Count;
            excluded = false;
            foreach (var plan in plans)
            {
                var wish = plan.Forced ?? result[plan.Device.Id].Wish;
                if (Total(plan, wish) <= Total(plan, plan.BasisIndex) || !(plan.ForcedIncrease || plan.MaxAllowed > plan.BasisIndex) || granted.Contains(plan.Device.Id))
                {
                    continue;
                }

                if (budget > 0)
                {
                    budget--;
                    granted.Add(plan.Device.Id);
                    continue;
                }

                plan.MaxAllowed = Math.Min(plan.BasisIndex, plan.CapIndex);
                plan.MinAllowed = Math.Min(plan.MinAllowed, plan.MaxAllowed);
                if (plan.ForcedIncrease)
                {
                    plan.Forced = plan.BasisIndex;
                    plan.ForcedIncrease = false;
                }

                plan.LimitWaiting = true;
                excluded = true;
            }

            if (excluded)
            {
                result = Run(plans, available, setup);
            }
        }

        var increased = new List<Plan>();
        var wishes = ImmutableArray.CreateBuilder<DispatchWish>();
        var states = ImmutableArray.CreateBuilder<DeviceDispatchState>();
        foreach (var plan in plans)
        {
            var allocation = result[plan.Device.Id];
            var state = _devices[plan.Device.Id];
            var spec = plan.Device.Spec;
            var wishIndex = plan.Forced ?? allocation.Wish;
            var level = DeviceLevels.Toward(spec, plan.Basis, plan.Levels[wishIndex], setup.Timing.MaxStepChangeStages);
            var change = Classify(spec, level, plan.Basis);

            // A reported level off the grid (a setpoint below the floor, a storage discharge, a stage
            // pattern the cumulative levels do not hold) was snapped to a grid index; the snap itself
            // is no change. A level no different in total stays exactly as reported, and an increase
            // the allocation did not plan above the basis, or the gates did not allow, is not made:
            // it would bypass guard permission, settling, confirmation and DISP-08.
            if (change == DispatchChange.Keep
                || (change == DispatchChange.Increase && (wishIndex <= plan.BasisIndex || !(plan.ForcedIncrease || plan.MaxAllowed > plan.BasisIndex))))
            {
                level = plan.Basis;
                change = DispatchChange.Keep;
            }

            var origin = plan.Mode == OverrideMode.Auto ? DecisionOrigin.Dispatch : DecisionOrigin.Override;

            if (atBoundary)
            {
                state.ConfirmStreak = allocation.Uncapped > plan.BasisIndex && plan.Gates ? state.ConfirmStreak + 1 : 0;
                state.DecreaseStreak = allocation.Wish < plan.BasisIndex ? state.DecreaseStreak + 1 : 0;
            }

            var reduction = plan.Constraints.Any(k => guard.Envelope.For(k) is { ReductionRequested: true });
            var preempted = increased.Any(u => SharesConstraint(u.Device.Spec, spec, input.Guard.Setup!));
            ReasonCode reason;
            ReasonCode? pending = null;
            if (change == DispatchChange.Decrease)
            {
                // Off the boundary only overrides act (an override change woke dispatch).
                var applyNow = (plan.Mode != OverrideMode.Auto || atBoundary)
                    && (plan.Mode != OverrideMode.Auto || plan.Excluded || reduction || preempted || state.DecreaseStreak >= setup.Timing.PersistenceDownCycles);

                reason = origin == DecisionOrigin.Override ? ReasonCode.ManualOverride
                    : plan.Excluded ? ReasonCode.DeviceUnresponsive
                    : reduction ? ReasonCode.ReserveThreshold
                    : preempted || allocation.Limit == AllocationLimit.Rank ? ReasonCode.ValueRankBelowCutoff
                    : ReasonCode.ReserveThreshold;
                if (!applyNow)
                {
                    level = plan.Basis;
                    change = DispatchChange.Keep;
                }
            }
            else if (change == DispatchChange.Increase)
            {
                reason = origin == DecisionOrigin.Override ? ReasonCode.ManualOverride : ReasonCode.HeadroomAvailable;
                increased.Add(plan);
            }
            else
            {
                reason = origin == DecisionOrigin.Override ? ReasonCode.ManualOverride
                    : plan.Excluded ? ReasonCode.DeviceUnresponsive
                    : allocation.Limit == AllocationLimit.None ? ReasonCode.HeadroomAvailable
                    : allocation.Limit == AllocationLimit.Rank ? ReasonCode.ValueRankBelowCutoff
                    : ReasonCode.HeadroomInsufficient;
            }

            if (change != DispatchChange.Increase && plan.Mode == OverrideMode.Auto && allocation.Uncapped > plan.BasisIndex)
            {
                pending = PendingReason(plan, guard, state, setup);
            }
            else if (plan.BlockedBy is { } blocked)
            {
                pending = blocked;
            }

            var fresh = atBoundary || overrideChanged.Contains(plan.Device.Id);
            var blockedOverride = plan.Mode == OverrideMode.ForceOn && plan.BlockedBy is not null;
            wishes.Add(new DispatchWish(
                plan.Device.Id,
                level,
                reason,
                origin,
                change,
                blockedOverride ? plan.BlockedBy : null,
                fresh,
                Inputs(plan, allocation, guard, input, setup, now))
            {
                PendingLevel = blockedOverride ? DeviceLevels.Toward(spec, plan.Basis, plan.Levels[plan.CapIndex], setup.Timing.MaxStepChangeStages) : null,
            });
            states.Add(new DeviceDispatchState(plan.Device.Id, reason, pending, plan.StartRank, level));
        }

        // Off the boundary, only the devices whose override changed get a new wish.
        _wishes = atBoundary
            ? wishes.ToImmutable()
            : [.. wishes.Select(w => overrideChanged.Contains(w.Device) ? w : (_wishes.FirstOrDefault(old => old.Device == w.Device) ?? w) with { Fresh = false })];
        _states = states.ToImmutable();
    }

    private static ReasonCode PendingReason(Plan plan, Guard.GuardEvaluation guard, DeviceState state, DispatchSetup setup) =>
        plan.Excluded ? ReasonCode.DeviceUnresponsive
        : !plan.DataValid ? ReasonCode.GuardStaleSignal
        : !plan.GuardAllows ? plan.Constraints.Any(k => guard.Envelope.For(k) is { BudgetLow: true }) ? ReasonCode.GuardBudgetLow : ReasonCode.HeadroomInsufficient
        : !plan.WindowFree || plan.LimitWaiting ? ReasonCode.SettlingWindowOpen
        : state.ConfirmStreak < setup.Timing.PersistenceUpCycles ? ReasonCode.AwaitingConfirmation
        : ReasonCode.HeadroomInsufficient;

    /// <summary>
    /// A = E + the estimate of every planned device on the constraint: what dispatch may give its
    /// own devices (each device's own in-flight target is in E once and added back once, so a
    /// started device is never charged twice for itself). Where the guard asks for a reduction the
    /// (negative) stop hysteresis is added, so "keep while ceiling ≤ A − H_stop" reads "keep while
    /// E ≥ 0": dispatch reduces until the reserve threshold holds again.
    /// </summary>
    private static ImmutableDictionary<ConstraintKey, Amperes> Available(DispatchEnvelope envelope, List<Plan> plans, DispatchSetup setup)
    {
        var available = ImmutableDictionary.CreateBuilder<ConstraintKey, Amperes>();
        foreach (var entry in envelope.Entries.Where(e => e is { DataValid: true, Available: not null }))
        {
            var estimates = plans.Where(p => p.Constraints.Contains(entry.Key)).Sum(p => envelope.Device(p.Device.Id)?.EstimateOn(entry.Key).Value ?? 0);
            available[entry.Key] = entry.Available!.Value + new Amperes(estimates) + (entry.ReductionRequested ? setup.StopHysteresis : Amperes.Zero);
        }

        return available.ToImmutable();
    }

    private static Dictionary<DeviceName, DeviceAllocation> Run(List<Plan> plans, ImmutableDictionary<ConstraintKey, Amperes> available, DispatchSetup setup)
    {
        var request = new AllocationRequest(
            [.. plans.Select(p => new AllocationDevice(p.Device.Spec, p.Constraints, p.Basis, p.Levels, p.MinAllowed, p.MaxAllowed))],
            available,
            setup.StartHysteresis,
            setup.StopHysteresis);
        return Allocator.Allocate(request).Devices.ToDictionary(d => d.Device);
    }

    private static DispatchDecisionInputs Inputs(Plan plan, DeviceAllocation allocation, Guard.GuardEvaluation guard, DispatchInput input, DispatchSetup setup, DateTimeOffset now)
    {
        var spec = plan.Device.Spec;
        var load = guard.Envelope.Device(spec.Id);
        var ledger = input.Ledger.Entry(spec.Id);
        return new DispatchDecisionInputs(
            [.. plan.Constraints.Select(k => new ConstraintHeadroom(k, guard.Envelope.For(k)?.Available, allocation.Headroom.TryGetValue(k, out var remaining) && double.IsFinite(remaining.Value) ? remaining : null))],
            setup.StartHysteresis,
            setup.StopHysteresis,
            plan.StartRank,
            load?.Contribution ?? ImmutableDictionary<Phase, Amperes>.Empty,
            load?.InFlight ?? ImmutableDictionary<ConstraintKey, Amperes>.Empty,
            ledger?.Window is { Kind: WindowKind.Command } ? SettlingState.Open : ledger?.SettlingTimedOut == true ? SettlingState.TimedOut : null,
            null,
            spec.Stages.Select(s => input.Lockouts.MinOnRemaining(spec.Id, s.Index, now, LockoutTimeline.Target)).Max(),
            spec.Stages.Select(s => input.Lockouts.MinOffRemaining(spec.Id, s.Index, now, LockoutTimeline.Target)).Max(),
            ledger?.Deferred?.Since,
            plan.Mode);
    }

    /// <summary>The main phases a device draws on and, as a group member, its group's phases.</summary>
    private static ImmutableArray<ConstraintKey> Constraints(DeviceSpec spec, ProtectionSetup protection)
    {
        var keys = spec.Phases.Select(ConstraintKey.Main).ToList();
        if (spec.CircuitGroup is { } group && protection.Group(group) is { } declared)
        {
            keys.AddRange(spec.Phases.Where(declared.Phases.Contains).Select(p => new ConstraintKey(group, p)));
        }

        return [.. keys];
    }

    private static bool SharesConstraint(DeviceSpec a, DeviceSpec b, ProtectionSetup protection) =>
        Constraints(a, protection).Intersect(Constraints(b, protection)).Any();

    private static bool PowerDrawsFresh(DispatchDevice device, DispatchInputs inputs) =>
        device.PowerDrawPhases.IsDefault || device.PowerDrawPhases.All(p =>
            !inputs.DevicePowers.IsDefault && inputs.DevicePowers.Any(s => s.Device == device.Id && s.Phase == p && s.Fresh));

    /// <summary>The lowest level a min_on lockout (target timeline) still allows: a relay stage that is on and locked stays on.</summary>
    private static int LockedMinimum(DeviceSpec spec, ImmutableArray<DeviceLevel> levels, DeviceLevel basis, IReadOnlyLockouts lockouts, DateTimeOffset now)
    {
        if (spec.IsSetpoint)
        {
            var on = DeviceLevels.Ceiling(spec, basis, spec.Phases[0]) > Amperes.Zero;
            var canStop = spec.CanSwitchOff || spec.Type == DeviceType.Storage;
            return on && canStop && lockouts.MinOnRemaining(spec.Id, spec.Stages[0].Index, now, LockoutTimeline.Target) > TimeSpan.Zero
                ? levels.Select((l, i) => (l, i)).First(x => DeviceLevels.Total(spec, x.l) > 0).i
                : 0;
        }

        var locked = 0;
        foreach (var stage in spec.Stages)
        {
            if (!basis.Relays.IsDefault && stage.Index - 1 < basis.Relays.Length && basis.Relays[stage.Index - 1] && lockouts.MinOnRemaining(spec.Id, stage.Index, now, LockoutTimeline.Target) > TimeSpan.Zero)
            {
                locked = Math.Max(locked, stage.Index);
            }
        }

        return locked;
    }

    /// <summary>The highest level whose total does not exceed the given level's (a reported setpoint between grid steps rounds down).</summary>
    private static int IndexAtOrBelow(DeviceSpec spec, ImmutableArray<DeviceLevel> levels, DeviceLevel level)
    {
        var exact = levels.IndexOf(level);
        if (exact >= 0)
        {
            return exact;
        }

        var total = DeviceLevels.Total(spec, level);
        var index = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            if (DeviceLevels.Total(spec, levels[i]) <= total + 1e-9)
            {
                index = i;
            }
        }

        return index;
    }

    private static double Total(Plan plan, int index) => DeviceLevels.Total(plan.Device.Spec, plan.Levels[index]);

    private static DispatchChange Classify(DeviceSpec spec, DeviceLevel level, DeviceLevel basis)
    {
        if (level.Equals(basis))
        {
            return DispatchChange.Keep;
        }

        var to = DeviceLevels.Total(spec, level);
        var from = DeviceLevels.Total(spec, basis);
        return to > from + DeviceLevel.SetpointTolerance ? DispatchChange.Increase
            : to < from - DeviceLevel.SetpointTolerance ? DispatchChange.Decrease
            : DispatchChange.Keep;
    }

    private sealed class DeviceState
    {
        public string Fingerprint { get; init; } = string.Empty;

        /// <summary>Consecutive boundaries at which the device would have taken an increase (persistence_up_cycles).</summary>
        public int ConfirmStreak { get; set; }

        /// <summary>Consecutive boundaries at which its allocation was below its level (persistence_down_cycles).</summary>
        public int DecreaseStreak { get; set; }

        public OverrideMode Override { get; set; }
    }

    /// <summary>One device's place in this cycle's allocation.</summary>
    private sealed class Plan(DispatchDevice device, int startRank, ImmutableArray<DeviceLevel> levels, DeviceLevel basis, int basisIndex, int capIndex, ImmutableArray<ConstraintKey> constraints, OverrideMode mode, bool excluded)
    {
        public DispatchDevice Device => device;

        public int StartRank => startRank;

        public ImmutableArray<DeviceLevel> Levels => levels;

        public DeviceLevel Basis => basis;

        public int BasisIndex => basisIndex;

        public int CapIndex => capIndex;

        public ImmutableArray<ConstraintKey> Constraints => constraints;

        public OverrideMode Mode => mode;

        /// <summary>Unresponsive and not yet due for its probe.</summary>
        public bool Excluded => excluded;

        public bool GuardAllows { get; init; }

        public bool WindowFree { get; init; }

        public bool DataValid { get; init; }

        public bool Gates { get; init; }

        public int MinAllowed { get; set; }

        public int MaxAllowed { get; set; }

        /// <summary>An override's level index, applied instead of the allocation's wish.</summary>
        public int? Forced { get; set; }

        public bool ForcedIncrease { get; set; }

        public ReasonCode? BlockedBy { get; set; }

        /// <summary>An increase that waits for the simultaneous-increase limit (DISP-08).</summary>
        public bool LimitWaiting { get; set; }
    }
}
