using Switchboard.Domain.Connection;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Guard;

/// <summary>
/// The main breaker as the guard models it (04 §1, BRK-01, BRK-05): rated current, the multiple it
/// carries indefinitely, and the tier table. Bands are compared as multiples of In, never as
/// up_to × In amperes, so a current exactly at a limit lands in the band below without rounding.
/// </summary>
public sealed record BreakerProfile
{
    public BreakerProfile(Amperes rating, BreakerCharacteristic characteristic, double continuousMultiple, TierTable tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (rating.Value <= 0 || continuousMultiple <= 0 || tiers.Bands.IsDefaultOrEmpty || tiers.Bands[^1].UpTo is not null)
        {
            throw new ArgumentException("A breaker needs a positive rating, a positive continuous multiple and a tier table ending in an open band.");
        }

        // The budget consumes dt / allowed above the continuous multiple; an unlimited band there
        // would let an overload run forever (the configuration envelope refuses it too).
        for (var band = 0; band < tiers.Bands.Length; band++)
        {
            if (tiers.Bands[band].Allowed is null && (tiers.Bands[band].UpTo is not { } upTo || upTo > continuousMultiple))
            {
                throw new ArgumentException($"Band {band} reaches above the continuous multiple {continuousMultiple} with an unlimited duration.");
            }
        }

        Rating = rating;
        Characteristic = characteristic;
        ContinuousMultiple = continuousMultiple;
        Tiers = tiers;
    }

    public Amperes Rating { get; }

    public BreakerCharacteristic Characteristic { get; }

    /// <summary>At or below this multiple the breaker carries the current indefinitely and the budget recovers.</summary>
    public double ContinuousMultiple { get; }

    public TierTable Tiers { get; }

    public Amperes ContinuousCurrent => Rating * ContinuousMultiple;

    /// <summary>|I| / In: the breaker heats in either direction, so import and export count alike.</summary>
    public double MultipleOf(Amperes current) => Math.Abs(current.Value) / Rating.Value;

    /// <summary>The band a multiple falls into; band limits are inclusive (I/In ≤ up_to).</summary>
    public int BandIndex(double multiple)
    {
        for (var band = 0; band < Tiers.Bands.Length; band++)
        {
            if (Tiers.Bands[band].UpTo is not { } upTo || multiple <= upTo)
            {
                return band;
            }
        }

        return Tiers.Bands.Length - 1;
    }

    public TierBand Band(int band) => Tiers.Bands[band];

    /// <summary>The multiple a band starts above: 0 for the first band, the previous band's up_to otherwise.</summary>
    public double LowerBound(int band) => band == 0 ? 0 : Tiers.Bands[band - 1].UpTo!.Value;

    public bool IsAboveContinuous(double multiple) => multiple > ContinuousMultiple;

    /// <summary>
    /// The top of the may_add region (the up_to of the last may_add band counted from the bottom),
    /// as a multiple of In; 0 when the first band does not allow adding. Dispatch never plans load
    /// above it (04 §3 "breaker_tier_allowance").
    /// </summary>
    public double MayAddUpTo
    {
        get
        {
            var top = 0.0;
            foreach (var band in Tiers.Bands)
            {
                if (band.Reaction != TierReaction.MayAdd || band.UpTo is not { } upTo)
                {
                    break;
                }

                top = upTo;
            }

            return top;
        }
    }
}
