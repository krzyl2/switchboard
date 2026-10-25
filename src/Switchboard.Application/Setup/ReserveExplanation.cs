using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Setup;

/// <summary>The minimum safe reserve of one phase next to the configured one (SIG-07, CONN-04).</summary>
/// <param name="RefreshS">The meter refresh the minimum was derived from: detected, else max_age (worst case).</param>
public sealed record ReservePhase(Phase Phase, double ReserveW, double MinimumSafeW, double RefreshS, bool RefreshDetected, bool BelowMinimum);

/// <summary>
/// The kettle arithmetic of 04 §1 with the user's own numbers: the phase is filled up to the reserve
/// threshold (In − R/V), then an uncontrolled step of reference_step_w appears. Where it lands on the
/// breaker curve decides what the user is told: under the rating, tolerated for a while with a
/// reduction planned in that time, or a shed (the reserve is too small for one kettle).
/// </summary>
/// <param name="PhaseW">In × V: what one phase carries at the breaker rating.</param>
/// <param name="AllocatableW">In × V − R: the most Switchboard plans for its own devices on that phase.</param>
/// <param name="WorstCaseA">In + (step − R) / V: the current with the step on a fully allocated phase.</param>
/// <param name="Multiple">WorstCaseA / In.</param>
/// <param name="AllowedS">How long the tier table tolerates that multiple; null for unlimited.</param>
public sealed record ReserveArithmetic(
    double ReserveW,
    double PhaseW,
    double ContinuousA,
    double AllocatableW,
    double ReferenceStepW,
    double WorstCaseA,
    double Multiple,
    TierReaction Reaction,
    double? AllowedS);

public sealed record ReserveReport(ImmutableArray<ReservePhase> PerPhase, ReserveArithmetic Explanation);

/// <summary>What the reserve derivation needs to know about one phase's current signal.</summary>
public sealed record PhaseRefresh(TimeSpan? Detected, TimeSpan MaxAge, double SigmaA);

/// <summary>
/// Computes both from the runtime's own model: the tier table and <see cref="MinimumSafeReserve.Compute"/>
/// with the control parameters of the document being edited (defaults merged with its control:), so
/// the wizard never shows a margin the guard would not see.
/// </summary>
public static class ReserveExplanation
{
    public static ReserveReport? Compute(ConnectionProfile? profile, ControlParameters control, IReadOnlyDictionary<Phase, PhaseRefresh> refresh)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(refresh);
        if (profile is null)
        {
            return null;
        }

        var tiers = control.Breaker.Tiers[profile.Characteristic];
        var phases = ImmutableArray.CreateBuilder<ReservePhase>();
        foreach (var phase in profile.Phases)
        {
            if (!refresh.TryGetValue(phase, out var signal))
            {
                continue;
            }

            var derivation = MinimumSafeReserve.Compute(
                phase,
                profile,
                tiers,
                control.Breaker.ContinuousMultiple[BreakerCharacteristic.Unknown],
                signal.Detected,
                signal.MaxAge,
                new Amperes(signal.SigmaA),
                control.ReserveDerivation);
            phases.Add(new ReservePhase(
                phase,
                derivation.Configured.Value,
                derivation.Minimum.Value,
                derivation.RefreshUsed.TotalSeconds,
                derivation.RefreshDetected,
                derivation.BelowMinimum));
        }

        var reserve = profile.Phases.Select(p => profile.Reserve.TryGetValue(p, out var r) ? r.Value : 0).DefaultIfEmpty(0).Min();
        return new ReserveReport(phases.ToImmutable(), Arithmetic(profile, control, reserve));
    }

    /// <summary>The kettle arithmetic for a reserve of <paramref name="reserveW"/>.</summary>
    public static ReserveArithmetic Arithmetic(ConnectionProfile profile, ControlParameters control, double reserveW)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(control);
        var rating = profile.BreakerRating.Value;
        var volts = profile.Voltage.Value;
        var step = control.ReserveDerivation.ReferenceStep.Value;
        var worst = rating + ((step - reserveW) / volts);
        var multiple = worst / rating;
        var band = control.Breaker.Tiers[profile.Characteristic].BandFor(multiple);
        return new ReserveArithmetic(
            reserveW,
            rating * volts,
            control.Breaker.ContinuousMultiple[profile.Characteristic] * rating,
            (rating * volts) - reserveW,
            step,
            worst,
            multiple,
            band.Reaction,
            band.Allowed?.TotalSeconds);
    }
}
