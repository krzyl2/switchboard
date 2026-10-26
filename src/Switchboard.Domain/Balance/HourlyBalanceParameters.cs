namespace Switchboard.Domain.Balance;

/// <summary>
/// The hourly balance's control parameters (13-HOURLY-BALANCE, <c>hourly_balance:</c>); every one
/// comes from configuration, none is a literal in code.
/// </summary>
/// <param name="FloorWh">Hard floor of the target band (lo): a projection below it asks for a stop.</param>
/// <param name="CeilingWh">Top of the band while the hour is young (hi): a projection above it asks for a start.</param>
/// <param name="BurnWindow">The band collapses from the ceiling to the floor over this many minutes before the :00 (the burn-down).</param>
/// <param name="SmoothingWindow">Free power is the mean of this many minutes of samples.</param>
/// <param name="MinRemaining">Time to the :00 is floored at this when projecting, so the last seconds do not blow the projection up.</param>
/// <param name="MaxAge">The longest gap between two free-power samples the smoothing window bridges; a longer silence starts the window afresh.</param>
public sealed record HourlyBalanceParameters(
    double FloorWh,
    double CeilingWh,
    TimeSpan BurnWindow,
    TimeSpan SmoothingWindow,
    TimeSpan MinRemaining,
    TimeSpan MaxAge);
