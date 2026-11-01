using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Guard;

/// <summary>
/// What the guard shows of one evaluation, read from the load estimator, the guards and the holds
/// and never changing them: the decision inputs recorded with an intent or an event, the phase and
/// device status, the headroom envelope for dispatch and the safe-state summary. Whether the kill
/// switch or a system-wide safe state holds everything is handed in by the engine at the moment of
/// the call (<c>holdsAll</c>), because the projector does not know the engine's state.
/// </summary>
internal sealed class GuardStatusProjector
{
    private readonly LoadEstimator _load;
    private readonly PhaseGuards _phases;
    private readonly HoldLedger _holds;
    private readonly ILockoutView _lockouts;

    public GuardStatusProjector(LoadEstimator load, PhaseGuards phases, HoldLedger holds, ILockoutView lockouts)
    {
        _load = load;
        _phases = phases;
        _holds = holds;
        _lockouts = lockouts;
    }

    private ProtectionSetup Setup => _load.Setup;

    private DateTimeOffset Now => _load.Now;

    public DecisionInputs PhaseInputs(ConstraintKey key) => new(PhaseDecision(key), null, null, null, null);

    public DecisionInputs Inputs(DeviceSpec device, int stage, ConstraintKey key)
    {
        var sample = _load.Sample(device.Id, stage);
        return new DecisionInputs(
            PhaseDecision(key),
            new DeviceDecisionInputs(
                sample is { Fresh: true } ? sample.Applied : null,
                sample is { Fresh: true },
                _load.InvalidReason(device.Id, stage),
                _load.Rank(device.Id),
                device.ValuePerKwh,
                device.ShedPriority,
                _lockouts.MinOnRemaining(device.Id, stage, Now),
                _lockouts.MinOffRemaining(device.Id, stage, Now),
                device.CanSwitchOff),
            null,
            null,
            null);
    }

    /// <summary>The whole status; without a projector (no setup in this process) only the mode, the kill switch and the safe state.</summary>
    public static GuardStatus Status(GuardStatusProjector? projector, GuardInput input, DateTimeOffset now, SafeStateStatus safeState, bool holdsAll)
    {
        var setup = projector?.Setup;
        return new GuardStatus(
            now,
            input.Mode,
            input.KillSwitch,
            safeState,
            setup is null ? null : new BreakerStatus(
                setup.Breaker.Rating,
                setup.Breaker.Characteristic,
                setup.Breaker.ContinuousMultiple,
                setup.Breaker.ContinuousCurrent,
                setup.Breaker.Tiers.Bands),
            projector is null ? [] : [.. setup!.Phases.Select(p => projector.PhaseStatus(ConstraintKey.Main(p), holdsAll))],
            projector is null ? [] : [.. setup!.Devices.Select(projector.DeviceStatus)])
        {
            Groups = projector is null ? [] : [.. setup!.Groups.SelectMany(g => g.Phases.Select(p => new GroupGuardStatus(g.Id, projector.PhaseStatus(new ConstraintKey(g.Id, p), holdsAll))))],
        };
    }

    /// <summary>The safe state shown: system-wide when it holds, else the main phases without valid data.</summary>
    public static SafeStateStatus SafeState(ReasonCode? systemSafe, DateTimeOffset? systemSafeSince, PhaseGuards phases, ImmutableDictionary<ConstraintKey, ReasonCode> phaseSafe)
    {
        if (systemSafe is { } system)
        {
            return new SafeStateStatus(true, SafeStateScope.All, system, [.. phases.Declared.Where(k => k.Group is null).Select(k => k.Phase).Order()], systemSafeSince);
        }

        var safePhases = phaseSafe.Where(p => p.Key.Group is null).ToList();
        if (safePhases.Count == 0)
        {
            return SafeStateStatus.None;
        }

        var since = safePhases.Select(p => phases[p.Key].InvalidSince).Where(s => s is not null).Min();
        return new SafeStateStatus(true, SafeStateScope.Phases, safePhases[0].Value, [.. safePhases.Select(p => p.Key.Phase).Order()], since);
    }

    public PhaseGuardStatus PhaseStatus(ConstraintKey key, bool holdsAll)
    {
        var guard = _phases[key];
        var profile = guard.Profile;
        var threshold = _load.ReserveThreshold(key);
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
        var effective = _load.Effective(key);
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
            IncreasesAllowed(key, effective, toReserve, lowBudget, holdsAll),
            guard.ReductionRequested,
            state,
            reading.Direction,
            reading.ReportedAt);
    }

    private bool IncreasesAllowed(ConstraintKey key, EffectiveCurrent effective, Amperes toReserve, bool lowBudget, bool holdsAll) =>
        toReserve > Amperes.Zero && BreakerAllows(key, effective, lowBudget, holdsAll);

    /// <summary>Everything an increase needs except the reserve threshold: what a manual override is held to.</summary>
    private bool BreakerAllows(ConstraintKey key, EffectiveCurrent effective, bool lowBudget, bool holdsAll)
    {
        var guard = _phases[key];
        var profile = guard.Profile;
        var effectiveReaction = profile.Band(profile.BandIndex(effective.Multiple)).Reaction;
        return !holdsAll
            && guard.Valid
            && !lowBudget
            && (effectiveReaction == TierReaction.MayAdd || guard.Reading!.Direction == FlowDirection.Export);
    }

    public DeviceGuardStatus DeviceStatus(DeviceSpec device)
    {
        var unsafePhases = device.Phases.Where(p => !_phases[ConstraintKey.Main(p)].Valid).ToImmutableArray();
        var unsafeStages = device.Stages.Where(s => _load.Sample(device.Id, s.Index) is not { Fresh: true }).Select(s => s.Index).ToImmutableArray();
        var groupUnsafe = Setup.ConstraintsOf(device).Any(k => k.Group is not null && !_phases[k].Valid);
        var limits = device.Stages
            .Select(s => _holds.Find(device.Id, s.Index))
            .ToList();
        var state = !unsafePhases.IsEmpty || !unsafeStages.IsEmpty || groupUnsafe || !_load.StateFresh(device) ? DeviceGuardState.Unsafe
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
                _load.Applied(device.Id, s.Index),
                _load.Sample(device.Id, s.Index) is { Fresh: true },
                _load.InvalidReason(device.Id, s.Index),
                limits[i]?.Target))],
            _load.Rank(device.Id),
            device.ValuePerKwh,
            device.ShedPriority,
            device.NoShed,
            device.CanSwitchOff,
            device.Stages.Select(s => _lockouts.MinOnRemaining(device.Id, s.Index, Now)).Max(),
            device.Stages.Select(s => _lockouts.MinOffRemaining(device.Id, s.Index, Now)).Max(),
            unsafePhases,
            unsafeStages);
    }

    /// <summary>
    /// The envelope for dispatch (03): per main and group phase E = min(In − reserve, may_add
    /// top × In) − I_est and the guard's permissions; per device its contribution, estimate and
    /// in-flight change, so dispatch never reads a current of its own (SIG-04).
    /// </summary>
    public DispatchEnvelope Envelope(bool holdsAll)
    {
        var entries = ImmutableArray.CreateBuilder<EnvelopeEntry>();
        foreach (var key in PhaseGuards.Keys(Setup))
        {
            var guard = _phases[key];
            var budget = guard.Budget(Now, Setup.Budget.RecoveryTime);
            var lowBudget = OverloadBudget.IsBelow(budget, Setup.Budget.StopIncreasesBelow);
            if (!guard.Valid || guard.Reading is null)
            {
                entries.Add(new EnvelopeEntry(key, false, null, null, null, Amperes.Zero, false, guard.ReductionRequested, lowBudget, null));
                continue;
            }

            var effective = _load.Effective(key);
            var threshold = _load.ReserveThreshold(key);
            var top = guard.Profile.Rating * guard.Profile.MayAddUpTo;
            entries.Add(new EnvelopeEntry(
                key,
                true,
                Amperes.Min(threshold, top) - effective.Signed,
                top - effective.Signed,
                effective.Signed,
                effective.InFlight,
                IncreasesAllowed(key, effective, threshold - effective.Signed, lowBudget, holdsAll),
                guard.ReductionRequested,
                lowBudget,
                guard.Reading.ReportedAt)
            {
                OverrideAllowed = BreakerAllows(key, effective, lowBudget, holdsAll),
            });
        }

        var devices = ImmutableArray.CreateBuilder<DeviceLoad>();
        foreach (var device in Setup.Devices)
        {
            var view = _load.View(device);
            var contribution = device.Phases.ToImmutableDictionary(p => p, p => LoadModel.Contribution(device, p, view.Reported, _load.Draw(device.Id, p)));
            var estimate = ImmutableDictionary.CreateBuilder<ConstraintKey, Amperes>();
            var inFlight = ImmutableDictionary.CreateBuilder<ConstraintKey, Amperes>();
            foreach (var key in Setup.ConstraintsOf(device))
            {
                var groupTerm = _load.Estimated(key);
                var delta = _load.Delta(view, key.Phase, groupTerm);
                var basis = groupTerm ? LoadModel.GroupTerm(device, key.Phase, view.Reported, _load.Draw(device.Id, key.Phase)) : contribution[key.Phase];
                estimate[key] = basis + delta;
                inFlight[key] = delta;
            }

            var draws = device.CurrentDrawPhases.IsDefault ? [] : device.CurrentDrawPhases.Select(p => _load.Draw(device.Id, p)).ToList();
            devices.Add(new DeviceLoad(
                device.Id,
                view.Reported,
                contribution,
                estimate.ToImmutable(),
                inFlight.ToImmutable(),
                _load.LimitsOf(device.Id),
                draws.All(d => d is { Fresh: true }),
                draws.Where(d => d?.ReportedAt is not null).ToImmutableDictionary(d => d!.Phase, d => d!.ReportedAt!.Value)));
        }

        return new DispatchEnvelope(entries.ToImmutable(), devices.ToImmutable());
    }

    private PhaseDecisionInputs PhaseDecision(ConstraintKey key)
    {
        var guard = _phases[key];
        var profile = guard.Profile;
        var threshold = _load.ReserveThreshold(key);
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
            _load.Effective(key).Signed,
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
