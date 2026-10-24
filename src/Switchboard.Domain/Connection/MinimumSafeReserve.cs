using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Connection;

/// <summary>Parameters of the reserve derivation; all come from the control defaults file.</summary>
/// <param name="ReferenceStep">The uncontrolled load step the reserve must absorb (a kettle).</param>
/// <param name="ShedLatency">From a detected overload to the load actually dropping.</param>
/// <param name="SigmaMultiplier">How many standard deviations of current measurement noise to keep free.</param>
/// <param name="Floor">The derived minimum never goes below this.</param>
/// <param name="DispatchCycle">A reduce_* band acts only at the next dispatch cycle, so that delay counts too.</param>
public sealed record ReserveDerivationParameters(
    Watts ReferenceStep,
    TimeSpan ShedLatency,
    double SigmaMultiplier,
    Watts Floor,
    TimeSpan DispatchCycle);

/// <summary>The derived minimum safe reserve of one phase, with every input shown (SIG-07).</summary>
/// <param name="RefreshUsed">The refresh interval the minimum was derived from.</param>
/// <param name="RefreshDetected">False when no refresh is detected yet and max_age stood in for it (worst case).</param>
/// <param name="BlindTime">Refresh interval plus shed latency: how long an uncontrolled step can go unanswered.</param>
/// <param name="TierMultiple">The multiple of In the step may push the phase to (m*).</param>
public sealed record ReserveDerivation(
    Phase Phase,
    Watts Configured,
    Watts Minimum,
    bool BelowMinimum,
    TimeSpan RefreshUsed,
    bool RefreshDetected,
    TimeSpan BlindTime,
    double TierMultiple);

public static class MinimumSafeReserve
{
    /// <summary>
    /// The phase is filled up to In − R/V. An uncontrolled step appears without warning, is seen
    /// within one meter refresh and is dropped a shed latency later; until the band's own reaction
    /// acts, the breaker carries it. So the step must land in a band that tolerates that long, and
    /// never in a shed band (the reserve exists so that a kettle does not cause a shed):
    /// <code>
    /// T_refresh = detected refresh of the phase current signal, else its max_age (worst case)
    /// T_blind   = T_refresh + shed_latency
    /// delay(b)  = dispatch_cycle for reduce_* reactions, 0 for may_add and hold
    /// m*        = up_to of the highest band b whose reaction is not shed_* and whose
    ///             allowed ≥ T_blind + delay(b) (unlimited counts); 1.0 when no band qualifies;
    ///             characteristic unknown: m* = min(m*, continuous_multiple.unknown)
    /// R_min     = max(floor_w, reference_step_w − (m* − 1)·In·V + sigma_multiplier·σ_I·V)
    /// </code>
    /// Example: 25 A, 230 V, B, 30 s refresh, 15 s latency, 30 s cycle: T_blind 45 s, the 3 min band
    /// needs 75 s and qualifies, m* = 1.25, R_min = 2000 − 1437.5 + 3·0.1·230 = 631.5 W.
    /// A configured reserve below R_min is flagged, never enforced.
    /// </summary>
    public static ReserveDerivation Compute(
        Phase phase,
        ConnectionProfile profile,
        TierTable tiers,
        double unknownContinuousMultiple,
        TimeSpan? detectedRefresh,
        TimeSpan maxAge,
        Amperes sigma,
        ReserveDerivationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(parameters);

        var refresh = detectedRefresh ?? maxAge;
        var blind = refresh + parameters.ShedLatency;

        double? best = null;
        foreach (var band in tiers.Bands)
        {
            if (band.IsShed || band.UpTo is not { } upTo)
            {
                continue;
            }

            var needed = blind + ReactionDelay(band.Reaction, parameters);
            if (band.Allowed is null || band.Allowed.Value >= needed)
            {
                best = best is { } current ? Math.Max(current, upTo) : upTo;
            }
        }

        var multiple = best ?? 1.0;
        if (profile.Characteristic == BreakerCharacteristic.Unknown)
        {
            multiple = Math.Min(multiple, unknownContinuousMultiple);
        }

        var rating = profile.BreakerRating.Value;
        var volts = profile.Voltage.Value;
        var derived = new Watts(
            parameters.ReferenceStep.Value
            - ((multiple - 1) * rating * volts)
            + (parameters.SigmaMultiplier * sigma.Value * volts));
        var minimum = Watts.Max(parameters.Floor, derived);
        var configured = profile.Reserve.TryGetValue(phase, out var reserve) ? reserve : Watts.Zero;

        return new ReserveDerivation(
            phase,
            configured,
            minimum,
            configured < minimum,
            refresh,
            detectedRefresh is not null,
            blind,
            multiple);
    }

    private static TimeSpan ReactionDelay(TierReaction reaction, ReserveDerivationParameters parameters) =>
        reaction is TierReaction.ReduceOrdinaryCycle or TierReaction.ReduceNextCycle ? parameters.DispatchCycle : TimeSpan.Zero;
}
