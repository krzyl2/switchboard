using Switchboard.Domain.Units;

namespace Switchboard.Domain.Balance;

/// <summary>The target band of the running hour in Wh (BAL-03): <c>Lo</c> is flat, <c>Hi</c> ramps down to it.</summary>
/// <param name="Remaining">Time to the :00 as the projection uses it, floored at min_remaining.</param>
/// <param name="Burning">The hour is inside the burn window: the band has started to collapse.</param>
public readonly record struct BalanceBand(double LoWh, double HiWh, TimeSpan Remaining, bool Burning);

/// <summary>What the rule wants for the load now running (BAL-04).</summary>
public enum BalanceWant
{
    /// <summary>No allowance: the balance or the free power is not known. No balance-driven start or increase.</summary>
    Unknown,

    /// <summary>Neither more nor less energy is wanted.</summary>
    Hold,

    /// <summary>The projection is above the band and still holds after the next candidate: energy is wanted.</summary>
    Start,

    /// <summary>The projection is below the floor: the load should come down.</summary>
    Stop,
}

/// <summary>
/// Projection of the hourly balance, its band and the allowance rule (13-HOURLY-BALANCE), pure:
/// <code>
/// proj(L) = balance + (free − L) · t_rem
/// lo = floor ;  hi = lo + (ceiling − lo) · clamp(t_rem / burn_window, 0, 1)
/// start  if proj(L) &gt; hi and proj(L + P) ≥ lo
/// stop   if proj(L) &lt; lo (withheld in the burn window while proj(L) ≥ 0)
/// </code>
/// It only says whether energy is wanted. Whether it is allowed stays with the guard and the dispatch envelope.
/// </summary>
public static class HourlyBalanceRule
{
    /// <summary>Time to the :00 as the projection uses it: floored at min_remaining so the last seconds stay finite.</summary>
    public static TimeSpan Remaining(TimeSpan untilNext, HourlyBalanceParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return untilNext > parameters.MinRemaining ? untilNext : parameters.MinRemaining;
    }

    /// <summary>The balance at the next :00 if the loads Switchboard runs draw <paramref name="load"/> until then and free power stays as it is, Wh.</summary>
    public static double Project(double balanceWh, Watts free, Watts load, TimeSpan untilNext, HourlyBalanceParameters parameters) =>
        balanceWh + (free - load).Value * Remaining(untilNext, parameters).TotalHours;

    public static BalanceBand Band(TimeSpan untilNext, HourlyBalanceParameters parameters)
    {
        var remaining = Remaining(untilNext, parameters);
        var ramp = parameters.BurnWindow <= TimeSpan.Zero ? 1 : Math.Clamp(remaining / parameters.BurnWindow, 0, 1);
        return new BalanceBand(
            parameters.FloorWh,
            parameters.FloorWh + (parameters.CeilingWh - parameters.FloorWh) * ramp,
            remaining,
            remaining <= parameters.BurnWindow);
    }

    /// <param name="balanceWh">The balance of the running hour (positive = net export); null when unknown.</param>
    /// <param name="free">Free power; null when unknown.</param>
    /// <param name="load">Power of the loads Switchboard runs now (Σ over the devices already running).</param>
    /// <param name="candidate">Power of the next device or stage in merit order.</param>
    public static BalanceWant Decide(double? balanceWh, Watts? free, Watts load, Watts candidate, TimeSpan untilNext, HourlyBalanceParameters parameters)
    {
        if (balanceWh is not { } balance || free is not { } freePower || !double.IsFinite(balance) || !double.IsFinite(freePower.Value))
        {
            return BalanceWant.Unknown;
        }

        var band = Band(untilNext, parameters);
        var now = Project(balance, freePower, load, untilNext, parameters);
        if (now > band.HiWh && Project(balance, freePower, load + candidate, untilNext, parameters) >= band.LoWh)
        {
            return BalanceWant.Start;
        }

        if (now < band.LoWh && !(band.Burning && now >= 0))
        {
            return BalanceWant.Stop;
        }

        return BalanceWant.Hold;
    }
}
