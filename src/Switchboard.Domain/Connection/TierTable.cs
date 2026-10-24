using System.Collections.Immutable;

namespace Switchboard.Domain.Connection;

/// <summary>What the guard does in a band of the breaker curve (04 §1).</summary>
public enum TierReaction
{
    MayAdd,
    Hold,
    ReduceOrdinaryCycle,
    ReduceNextCycle,
    ShedOneImmediately,
    ShedAllOnPhase,
}

/// <summary>
/// One row of the tier table: currents up to and including <see cref="UpTo"/> × In belong to this
/// band. A null <see cref="UpTo"/> is the open top band; a null <see cref="Allowed"/> is unlimited.
/// </summary>
public sealed record TierBand(double? UpTo, TimeSpan? Allowed, TierReaction Reaction)
{
    public bool IsShed => Reaction is TierReaction.ShedOneImmediately or TierReaction.ShedAllOnPhase;
}

/// <summary>The bands in ascending order; the last one is open-ended.</summary>
public sealed record TierTable(ImmutableArray<TierBand> Bands)
{
    /// <summary>The band a current of <paramref name="multiple"/> × In falls into; band limits are inclusive.</summary>
    public TierBand BandFor(double multiple)
    {
        foreach (var band in Bands)
        {
            if (band.UpTo is not { } upTo || multiple <= upTo)
            {
                return band;
            }
        }

        return Bands[^1];
    }
}
