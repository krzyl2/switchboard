using System.Collections.Immutable;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Presentation;

/// <summary>
/// Builds the power picture of one control step from that step alone: the signal layer's phase
/// powers (net import, generation share, house load, SIG-08), the device powers dispatch counted for
/// foreign load (measured draw, else a relay's rating while it reports on), the guard's currents and
/// thresholds, and dispatch's intentions. Pure and deterministic; it never reads a clock, and it
/// changes nothing it reads.
/// </summary>
public static class PowerViewBuilder
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    /// <param name="foreignDetected">The last foreign load event per phase that belongs to the current excursion.</param>
    /// <param name="voltageLossFraction">
    /// Statistics:VoltageLossFraction: a fresh phase voltage below this fraction of the nominal one is
    /// a voltage loss (the statistics count it), never a basis to convert power to current at.
    /// </param>
    public static PowerView Build(GuardStep step, IReadOnlyDictionary<Phase, DateTimeOffset> foreignDetected, double voltageLossFraction)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(foreignDetected);
        var at = step.Evaluation.At;
        var status = step.Evaluation.Status;
        var inputs = step.Inputs;
        if (inputs?.DispatchSetup is not { } setup || inputs.Presentation is not { } presentation || status.Breaker is not { } breaker)
        {
            var reason = inputs?.Dispatch.Phases.FirstOrDefault(p => !p.DataValid)?.InvalidReason ?? DispatchInvalidReason.ConfigurationInvalid;
            return Empty(at, reason);
        }

        var devicePowers = DevicePowers(step, setup, inputs);
        var phases = ImmutableArray.CreateBuilder<PhasePower>();
        var banners = ImmutableArray.CreateBuilder<ReserveBanner>();
        foreach (var phase in setup.Phases)
        {
            if (status.Phases.FirstOrDefault(p => p.Phase == phase) is not { } guard)
            {
                continue;
            }

            var power = Phase(phase, guard, breaker, setup, inputs, presentation, step.Dispatch, devicePowers, foreignDetected, voltageLossFraction);
            phases.Add(power);
            if (power.OverReserve)
            {
                banners.Add(Banner(power, guard, breaker, setup, step, at));
            }
        }

        var devices = setup.Devices.Select(d =>
        {
            var parts = devicePowers.Where(p => p.Device == d.Id).Select(p => p.View).ToImmutableArray();
            var known = parts.All(p => p.Power is not null);
            return new DevicePowerView(
                d.Id,
                parts,
                known ? new Watts(parts.Sum(p => p.Power!.Value.Value)) : null,
                !known ? PowerSource.Unknown : parts.All(p => p.Source == PowerSource.Measured) ? PowerSource.Measured : PowerSource.RatedModel);
        }).ToImmutableArray();

        var invalid = setup.Phases.Select(inputs.Dispatch.For).FirstOrDefault(p => p is not { DataValid: true });
        return new PowerView(
            at,
            invalid is null,
            invalid is null ? null : invalid.InvalidReason ?? DispatchInvalidReason.ConfigurationInvalid,
            Total([.. phases.Select(p => p.Balance)]),
            phases.ToImmutable(),
            devices,
            banners.ToImmutable(),
            setup.Timing.Cycle);
    }

    /// <summary>The next dispatch cycle boundary strictly after <paramref name="at"/>: boundaries are multiples of the cycle since the Unix epoch.</summary>
    public static DateTimeOffset NextBoundary(DateTimeOffset at, TimeSpan cycle)
    {
        var elapsed = (at - Epoch).Ticks;
        return Epoch + TimeSpan.FromTicks(((elapsed / cycle.Ticks) + 1) * cycle.Ticks);
    }

    private static PowerView Empty(DateTimeOffset at, DispatchInvalidReason reason)
    {
        var missing = Quantity.Missing(Map(reason));
        var balance = new BalanceParts(missing, null, missing, missing, missing, [], missing, missing, false);
        return new PowerView(at, false, reason, balance, [], [], [], TimeSpan.Zero);
    }

    private static PhasePower Phase(
        Phase phase,
        PhaseGuardStatus guard,
        BreakerStatus breaker,
        DispatchSetup setup,
        ControlInputs inputs,
        PresentationInputs presentation,
        DispatchEvaluation? dispatch,
        List<(DeviceName Device, bool Storage, DevicePhasePowerView View)> devicePowers,
        IReadOnlyDictionary<Phase, DateTimeOffset> foreignDetected,
        double voltageLossFraction)
    {
        var phaseInputs = inputs.Dispatch.For(phase);
        var balance = Balance(phase, phaseInputs, presentation, setup, devicePowers);

        // A fresh 0 V (a lost phase) would turn every conversion into 0/0: below the loss fraction
        // the nominal voltage is the basis, as without a voltage signal.
        var (voltage, voltageMeasured) = phaseInputs?.Voltage is { } fresh && fresh.Value >= presentation.NominalVoltage.Value * voltageLossFraction
            ? (fresh, true)
            : (presentation.NominalVoltage, false);
        var baseline = dispatch?.Phases.FirstOrDefault(p => p.Phase == phase)?.Baseline;
        Watts? foreignStep = balance.Uncontrolled.Value is { } uncontrolled && baseline is { } b ? new Watts(Math.Max(0, uncontrolled - b.Value)) : null;
        var known = devicePowers.Where(d => !d.Storage && d.View.Phase == phase && d.View.Power is not null).Sum(d => d.View.Power!.Value.Value);
        var (bar, scaled) = Bar(guard, balance, foreignStep, new Watts(known), voltage);
        var overReserve = guard.DataValid
            && ((guard.Current is { } measured && guard.Direction != FlowDirection.Export && measured.Abs() > guard.ReserveThreshold)
                || (guard.EffectiveCurrent is { } estimate && estimate > guard.ReserveThreshold));
        return new PhasePower(
            phase,
            guard.Current,
            guard.Direction,
            guard.ReportedAt,
            guard.ReserveThreshold,
            breaker.Rating,
            guard.Continuous,
            voltage,
            voltageMeasured,
            balance,
            foreignStep,
            baseline,
            foreignDetected.TryGetValue(phase, out var detected) ? detected : null,
            bar,
            scaled,
            overReserve);
    }

    private static BalanceParts Balance(
        Phase phase,
        PhaseDispatch? inputs,
        PresentationInputs presentation,
        DispatchSetup setup,
        List<(DeviceName Device, bool Storage, DevicePhasePowerView View)> devicePowers)
    {
        var netImport = inputs?.NetImport is { } net
            ? Quantity.Known(net.Value, inputs.PowerReportedAt)
            : Quantity.Missing(inputs?.InvalidReason is { } invalid and not DispatchInvalidReason.GenerationUnknown ? Map(invalid) : PowerUnknownReason.PowerNotFresh);
        var generation = presentation.Generation == GenerationState.NotDeclared ? Quantity.Missing(PowerUnknownReason.NotDeclared)
            : inputs?.Generation is { } share ? Quantity.Known(share.Value, presentation.GenerationReportedAt)
            : Quantity.Missing(inputs?.InvalidReason is DispatchInvalidReason.SourceDisconnected or DispatchInvalidReason.ConfigurationInvalid ? Map(inputs.InvalidReason.Value) : PowerUnknownReason.GenerationUnknown);
        GenerationNote? note = generation.Value is not null && presentation.Generation == GenerationState.AsleepCountedAsZero ? GenerationNote.AsleepCountedAsZero : null;

        // An inverter asleep counts as 0 W by rule, not by a measurement: its last report (dusk) is
        // the generation row's age, and must not make a fresh house load look hours old.
        var load = inputs?.Load is { } houseLoad
            ? Quantity.Known(houseLoad.Value, note is null ? Oldest(netImport.ReportedAt, generation.ReportedAt) : netImport.ReportedAt)
            : Quantity.Missing(netImport.Unknown ?? generation.Unknown ?? PowerUnknownReason.GenerationUnknown);

        var onPhase = devicePowers.Where(d => d.View.Phase == phase).ToList();
        var controlled = Sum(onPhase.Where(d => !d.Storage).Select(d => d.View).ToList());
        var anyStorage = devicePowers.Any(d => d.Storage);
        var storage = anyStorage ? Sum(onPhase.Where(d => d.Storage).Select(d => d.View).ToList()) : Quantity.Missing(PowerUnknownReason.NotDeclared);
        var unknownDevices = onPhase.Where(d => d.View.Power is null).Select(d => d.Device).Distinct().ToImmutableArray();

        var uncontrolled = load.Value is { } l && controlled.Value is { } c && storage.InBalance is { } s
            ? Quantity.Known(l - c - s, Oldest(load.ReportedAt, controlled.ReportedAt, storage.ReportedAt))
            : Quantity.Missing(load.Unknown ?? controlled.Unknown ?? storage.Unknown ?? PowerUnknownReason.DevicePowerUnknown);
        return new BalanceParts(generation, note, netImport, load, controlled, unknownDevices, storage, uncontrolled, uncontrolled.Value < 0);
    }

    /// <summary>Σ of device powers: unknown when any is; reported at the oldest measurement (the rated model has no report time).</summary>
    private static Quantity Sum(List<DevicePhasePowerView> powers) =>
        powers.All(p => p.Power is not null)
            ? Quantity.Known(powers.Sum(p => p.Power!.Value.Value), Oldest([.. powers.Select(p => p.ReportedAt)]))
            : Quantity.Missing(PowerUnknownReason.DevicePowerUnknown);

    /// <summary>
    /// The phase bar in amperes: the measured magnitude split into base, declared devices and a
    /// foreign step, each converted at the phase voltage. Exporting, the whole current is one export
    /// segment. While the house's own part is unknown, the known devices are drawn and the rest is
    /// unattributed. Parts that do not fit under the measured current (local generation covers part
    /// of the load) are scaled to fit, and the bar says so.
    /// </summary>
    private static (ImmutableArray<BarSegment> Bar, bool Scaled) Bar(PhaseGuardStatus guard, BalanceParts balance, Watts? foreignStep, Watts knownControlled, Volts voltage)
    {
        if (guard.Current is not { } current)
        {
            return ([], false);
        }

        var total = current.Abs();
        if (guard.Direction == FlowDirection.Export)
        {
            return ([new BarSegment(SegmentKind.Export, total, balance.NetImport.Value is { } net ? new Watts(-net) : null)], false);
        }

        if (balance.Uncontrolled.Value is { } uncontrolled && balance.Controlled.Value is { } controlledW)
        {
            var foreignW = foreignStep ?? Watts.Zero;
            var controlled = Amperes.Max(Amperes.Zero, SignalUnits.CurrentAt(new Watts(controlledW), voltage));
            var foreign = Amperes.Max(Amperes.Zero, SignalUnits.CurrentAt(foreignW, voltage));
            var scaled = controlled + foreign > total;
            if (scaled)
            {
                var factor = total.Value / (controlled + foreign).Value;
                controlled *= factor;
                foreign *= factor;
            }

            return (
            [
                new BarSegment(SegmentKind.Base, Amperes.Max(Amperes.Zero, total - controlled - foreign), new Watts(uncontrolled - foreignW.Value)),
                new BarSegment(SegmentKind.Controlled, controlled, new Watts(controlledW)),
                new BarSegment(SegmentKind.Foreign, foreign, foreignStep),
            ], scaled);
        }

        var known = Amperes.Max(Amperes.Zero, SignalUnits.CurrentAt(knownControlled, voltage));
        var knownScaled = known > total;
        if (knownScaled)
        {
            known = total;
        }

        return (
        [
            new BarSegment(SegmentKind.Controlled, known, knownControlled),
            new BarSegment(SegmentKind.Unattributed, total - known, null),
        ], knownScaled);
    }

    /// <summary>
    /// The banner's timing follows the guard's own reaction (GuardEngine.React): shedding everything
    /// from the measured band, every other tier step from the band of the estimate I_est, and a
    /// persisted reserve request at the next boundary before an ordinary-cycle reduction.
    /// </summary>
    private static ReserveBanner Banner(PhasePower power, PhaseGuardStatus guard, BreakerStatus breaker, DispatchSetup setup, GuardStep step, DateTimeOffset at)
    {
        var next = NextBoundary(at, setup.Timing.Cycle);
        var byBoundary = next + (setup.Timing.Cycle * (setup.Timing.PersistenceDownCycles - 1));
        var reaction = Reaction(guard, breaker);
        (ReactionTiming Timing, DateTimeOffset? Due) timing = guard.Direction == FlowDirection.Export ? (ReactionTiming.CannotReduceExporting, null)
            : reaction switch
            {
                TierReaction.ShedAllOnPhase => (ReactionTiming.Now, at),
                TierReaction.ShedOneImmediately => (ReactionTiming.NextSample, null),
                TierReaction.ReduceNextCycle => (ReactionTiming.NextBoundary, next),
                _ when guard.ReductionRequested => (ReactionTiming.NextBoundary, next),
                _ => (ReactionTiming.ByBoundary, byBoundary),
            };
        return new ReserveBanner(
            power.Phase,
            guard.Current ?? guard.EffectiveCurrent ?? Amperes.Zero,
            guard.EffectiveCurrent,
            guard.ReserveThreshold,
            power.ForeignStep,
            power.ForeignDetectedAt,
            reaction,
            guard.ReductionRequested,
            timing.Timing,
            timing.Due,
            Planned(power.Phase, setup, step));
    }

    /// <summary>The reaction the guard acts on: shed_all_on_phase from the measured band, otherwise the band of the estimate (the measured one without an estimate).</summary>
    private static TierReaction? Reaction(PhaseGuardStatus guard, BreakerStatus breaker)
    {
        if (guard.Reaction == TierReaction.ShedAllOnPhase || guard.EffectiveCurrent is not { } estimate || breaker.Tiers.IsDefaultOrEmpty || breaker.Rating.Value <= 0)
        {
            return guard.Reaction;
        }

        return new TierTable(breaker.Tiers).BandFor(Math.Max(0, estimate.Value) / breaker.Rating.Value).Reaction;
    }

    /// <summary>The reductions on the phase that dispatch or protection intends and Home Assistant does not show yet, and those waiting for a lockout.</summary>
    private static ImmutableArray<PlannedChange> Planned(Phase phase, DispatchSetup setup, GuardStep step)
    {
        if (step.DispatchStatus is not { } status)
        {
            return [];
        }

        var planned = ImmutableArray.CreateBuilder<PlannedChange>();
        foreach (var device in status.Devices.Where(d => d.Phases.Contains(phase)))
        {
            if (setup.Device(device.Id)?.Spec is not { } spec)
            {
                continue;
            }

            var current = device.Reported ?? device.Intended;
            if (device.Intended is { } intended && device.Reported is { } reported && DeviceLevels.Total(spec, intended) < DeviceLevels.Total(spec, reported))
            {
                planned.Add(new PlannedChange(device.Id, reported, intended, device.Origin, device.Reason, null));
            }
            else if (device.Deferred is { } deferred && current is not null && DeviceLevels.Total(spec, deferred.Target) < DeviceLevels.Total(spec, current))
            {
                planned.Add(new PlannedChange(device.Id, current, deferred.Target, device.Origin, deferred.Reason, deferred.DueAt));
            }
        }

        return planned.ToImmutable();
    }

    /// <summary>Every declared device's power on each of its phases, with the report time of a measured one.</summary>
    private static List<(DeviceName Device, bool Storage, DevicePhasePowerView View)> DevicePowers(GuardStep step, DispatchSetup setup, ControlInputs inputs)
    {
        var counted = step.Dispatch?.DevicePowers ?? [];
        var samples = inputs.Dispatch.DevicePowers.IsDefault ? [] : inputs.Dispatch.DevicePowers;
        var result = new List<(DeviceName, bool, DevicePhasePowerView)>();
        foreach (var device in setup.Devices)
        {
            foreach (var phase in device.Spec.Phases)
            {
                var power = counted.FirstOrDefault(p => p.Device == device.Id && p.Phase == phase);
                var source = power?.Power is null ? PowerSource.Unknown : power.Source;
                DateTimeOffset? reportedAt = source == PowerSource.Measured
                    ? samples.FirstOrDefault(s => s.Device == device.Id && s.Phase == phase)?.ReportedAt
                    : null;
                result.Add((device.Id, device.Spec.Type == DeviceType.Storage, new DevicePhasePowerView(phase, power?.Power, source, reportedAt)));
            }
        }

        return result;
    }

    private static BalanceParts Total(ImmutableArray<BalanceParts> phases)
    {
        if (phases.IsEmpty)
        {
            var missing = Quantity.Missing(PowerUnknownReason.ConfigurationInvalid);
            return new BalanceParts(missing, null, missing, missing, missing, [], missing, missing, false);
        }

        return new BalanceParts(
            SumOf(phases.Select(p => p.Generation)),
            phases.Select(p => p.GenerationNote).FirstOrDefault(n => n is not null),
            SumOf(phases.Select(p => p.NetImport)),
            SumOf(phases.Select(p => p.Load)),
            SumOf(phases.Select(p => p.Controlled)),
            [.. phases.SelectMany(p => p.UnknownDevices).Distinct()],
            SumOf(phases.Select(p => p.Storage)),
            SumOf(phases.Select(p => p.Uncontrolled)),
            phases.Any(p => p.ModelExceedsLoad));
    }

    /// <summary>Σ over phases: known only when every phase is; "not declared" only when it is on every phase.</summary>
    private static Quantity SumOf(IEnumerable<Quantity> quantities)
    {
        var list = quantities.ToList();
        if (list.All(q => q.Value is not null))
        {
            return Quantity.Known(list.Sum(q => q.Value!.Value), Oldest([.. list.Select(q => q.ReportedAt)]));
        }

        return list.All(q => q.Unknown == PowerUnknownReason.NotDeclared)
            ? Quantity.Missing(PowerUnknownReason.NotDeclared)
            : Quantity.Missing(list.FirstOrDefault(q => q.Value is null && q.Unknown != PowerUnknownReason.NotDeclared)?.Unknown ?? PowerUnknownReason.NotDeclared);
    }

    /// <summary>The oldest of the report times given (a derived figure is as old as its oldest input); null when none is known.</summary>
    private static DateTimeOffset? Oldest(params DateTimeOffset?[] times)
    {
        DateTimeOffset? oldest = null;
        foreach (var time in times)
        {
            if (time is { } value && (oldest is null || value < oldest))
            {
                oldest = value;
            }
        }

        return oldest;
    }

    private static PowerUnknownReason Map(DispatchInvalidReason reason) => reason switch
    {
        DispatchInvalidReason.ConfigurationInvalid => PowerUnknownReason.ConfigurationInvalid,
        DispatchInvalidReason.SourceDisconnected => PowerUnknownReason.SourceDisconnected,
        DispatchInvalidReason.PowerUnmapped => PowerUnknownReason.PowerUnmapped,
        DispatchInvalidReason.PowerNotFresh => PowerUnknownReason.PowerNotFresh,
        _ => PowerUnknownReason.GenerationUnknown,
    };
}
