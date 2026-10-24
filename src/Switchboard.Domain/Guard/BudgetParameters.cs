namespace Switchboard.Domain.Guard;

/// <summary>Overload budget with thermal memory (04 §1); bound from the control defaults file.</summary>
/// <param name="RecoveryTime">An empty budget refills in this long while the current stays at or below continuous.</param>
/// <param name="StopIncreasesBelow">Below this no increase is allowed on the phase.</param>
/// <param name="ShedOneBelow">Below this one device is shed even if the current is legal.</param>
/// <param name="TopTierAtOrBelow">At or below this the phase is treated as the top tier: everything controlled is shed.</param>
public sealed record BudgetParameters(
    TimeSpan RecoveryTime,
    double StopIncreasesBelow,
    double ShedOneBelow,
    double TopTierAtOrBelow);
