namespace Switchboard.Domain.Guard;

/// <summary>
/// The per-phase overload budget with thermal memory (04 §1, BRK-02), as pure functions of explicit
/// time deltas:
/// <code>
/// budget ∈ [0, 1]
/// I/In &gt; continuous:  budget −= dt / allowed(band of I/In); a band allowed 0 s empties it at once
/// I/In ≤ continuous:  budget += dt / recovery_time
/// </code>
/// Threshold tests compare with <see cref="Tolerance"/> so that a crossing computed to the tick is
/// also seen at that tick, whatever the floating-point rounding of the budget on the way.
/// </summary>
public static class OverloadBudget
{
    /// <summary>
    /// Numerical tolerance for threshold comparisons, not a control parameter: far below one tick of
    /// any duration (1 h is 3.6·10¹⁰ ticks) and far above the rounding of a few hundred additions.
    /// </summary>
    public const double Tolerance = 1e-12;

    public static double Advance(double budget, double heldMultiple, BreakerProfile profile, TimeSpan dt, TimeSpan recovery)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (dt <= TimeSpan.Zero)
        {
            return budget;
        }

        if (profile.IsAboveContinuous(heldMultiple))
        {
            var allowed = AllowedAt(heldMultiple, profile);
            return allowed <= TimeSpan.Zero ? 0 : Math.Max(0, budget - ((double)dt.Ticks / allowed.Ticks));
        }

        return Math.Min(1, budget + ((double)dt.Ticks / recovery.Ticks));
    }

    /// <summary>How long a full refill takes from here: (1 − budget) · recovery_time.</summary>
    public static TimeSpan RecoveryRemaining(double budget, TimeSpan recovery) =>
        TimeSpan.FromTicks((long)Math.Round(Math.Clamp(1 - budget, 0, 1) * recovery.Ticks));

    public static bool IsBelow(double budget, double threshold) => budget < threshold - Tolerance;

    public static bool IsAtOrBelow(double budget, double threshold) => budget <= threshold + Tolerance;

    /// <summary>The first instant (from now, to the tick) at which the budget is below <paramref name="threshold"/>; null if it never gets there at this current.</summary>
    public static TimeSpan? TimeUntilBelow(double budget, double threshold, double heldMultiple, BreakerProfile profile, TimeSpan recovery)
    {
        if (IsBelow(budget, threshold))
        {
            return TimeSpan.Zero;
        }

        if (!Consuming(heldMultiple, profile, out var allowed) || threshold - Tolerance <= 0)
        {
            return null;
        }

        var estimate = (long)Math.Floor((budget - threshold + Tolerance) * allowed.Ticks) + 1;
        return Search(estimate, n => IsBelow(Advance(budget, heldMultiple, profile, TimeSpan.FromTicks(n), recovery), threshold));
    }

    /// <summary>The first instant at which the budget is at or below <paramref name="threshold"/>; null if never at this current.</summary>
    public static TimeSpan? TimeUntilAtOrBelow(double budget, double threshold, double heldMultiple, BreakerProfile profile, TimeSpan recovery)
    {
        if (IsAtOrBelow(budget, threshold))
        {
            return TimeSpan.Zero;
        }

        if (!Consuming(heldMultiple, profile, out var allowed))
        {
            return null;
        }

        var estimate = (long)Math.Ceiling((budget - threshold - Tolerance) * allowed.Ticks);
        return Search(estimate, n => IsAtOrBelow(Advance(budget, heldMultiple, profile, TimeSpan.FromTicks(n), recovery), threshold));
    }

    /// <summary>The first instant at which a recovering budget is no longer below <paramref name="threshold"/>; null if it is not recovering.</summary>
    public static TimeSpan? TimeUntilNotBelow(double budget, double threshold, double heldMultiple, BreakerProfile profile, TimeSpan recovery)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!IsBelow(budget, threshold))
        {
            return TimeSpan.Zero;
        }

        if (profile.IsAboveContinuous(heldMultiple) || threshold > 1)
        {
            return null;
        }

        var estimate = (long)Math.Ceiling((threshold - Tolerance - budget) * recovery.Ticks);
        return Search(estimate, n => !IsBelow(Advance(budget, heldMultiple, profile, TimeSpan.FromTicks(n), recovery), threshold));
    }

    private static bool Consuming(double heldMultiple, BreakerProfile profile, out TimeSpan allowed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        allowed = profile.IsAboveContinuous(heldMultiple) ? AllowedAt(heldMultiple, profile) : TimeSpan.Zero;
        return profile.IsAboveContinuous(heldMultiple);
    }

    // BreakerProfile guarantees a finite duration for every band reaching above the continuous multiple.
    private static TimeSpan AllowedAt(double multiple, BreakerProfile profile) => profile.Band(profile.BandIndex(multiple)).Allowed!.Value;

    /// <summary>The smallest tick count, near the estimate, for which the (monotone) condition holds.</summary>
    private static TimeSpan Search(long estimate, Func<long, bool> holds)
    {
        var n = Math.Max(0, estimate);
        while (n > 0 && holds(n - 1))
        {
            n--;
        }

        while (!holds(n))
        {
            n++;
        }

        return TimeSpan.FromTicks(n);
    }
}
