namespace Switchboard.Domain.Planning;

/// <summary>
/// The forecast side of the planner's parameters (<c>planner:</c> in control-defaults, 15-PLANNER): every value comes from
/// configuration, none is a literal in code. Validated on construction; the binder checks the same ranges first and reports an issue.
/// </summary>
/// <param name="Slot">Length of one planning slot: 15, 30 or 60 minutes (<c>planner.slot</c>).</param>
/// <param name="PvDerate">Factor on the forecast PV energy, 0 to 1 (<c>planner.pv_derate</c>).</param>
/// <param name="LoadHalfLife">Half-life of a load-profile bin's weights (<c>planner.load.halflife_days</c>).</param>
/// <param name="LoadMinObservedFraction">An hour counts only when observed at least this fraction of it, above 0 and at most 1 (<c>planner.load.min_observed_fraction</c>).</param>
/// <param name="LoadFallbackW">The house load assumed while no bin of the profile is filled, W (<c>planner.load.fallback_w</c>).</param>
public sealed record ForecastParameters(
    TimeSpan Slot,
    double PvDerate,
    TimeSpan LoadHalfLife,
    double LoadMinObservedFraction,
    double LoadFallbackW)
{
    /// <summary>The slot lengths the code supports.</summary>
    public static readonly IReadOnlyList<TimeSpan> SupportedSlots = [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];

    public TimeSpan Slot { get; } = SupportedSlots.Contains(Slot)
        ? Slot
        : throw new ArgumentOutOfRangeException(nameof(Slot), Slot, "the slot is 15, 30 or 60 minutes");

    public double PvDerate { get; } = double.IsFinite(PvDerate) && PvDerate is >= 0 and <= 1
        ? PvDerate
        : throw new ArgumentOutOfRangeException(nameof(PvDerate), PvDerate, "the derate is between 0 and 1");

    public TimeSpan LoadHalfLife { get; } = LoadHalfLife > TimeSpan.Zero
        ? LoadHalfLife
        : throw new ArgumentOutOfRangeException(nameof(LoadHalfLife), LoadHalfLife, "the half-life is positive");

    public double LoadMinObservedFraction { get; } = double.IsFinite(LoadMinObservedFraction) && LoadMinObservedFraction is > 0 and <= 1
        ? LoadMinObservedFraction
        : throw new ArgumentOutOfRangeException(nameof(LoadMinObservedFraction), LoadMinObservedFraction, "the fraction is above 0 and at most 1");

    public double LoadFallbackW { get; } = double.IsFinite(LoadFallbackW) && LoadFallbackW >= 0
        ? LoadFallbackW
        : throw new ArgumentOutOfRangeException(nameof(LoadFallbackW), LoadFallbackW, "the fallback load is not negative");
}
