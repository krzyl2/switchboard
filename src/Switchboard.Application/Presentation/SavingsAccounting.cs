using System.Collections.Immutable;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Presentation;

/// <summary>Whether a settlement period is in the savings.</summary>
public enum PeriodStatus
{
    /// <summary>Complete and priced: summed into the totals.</summary>
    Counted,

    /// <summary>The period in progress, priced so far: reported apart, never summed into a total.</summary>
    Running,

    /// <summary>An hour that was not observed throughout (a restart, a gap): its net export is not known, so it is not accounted.</summary>
    Incomplete,

    /// <summary>Some price the period needs was not known for all of its observed time (no <c>economics:</c> section then, an old statistics file, a missing price): not counted, nothing is back-filled.</summary>
    Unpriced,
}

/// <summary>One device's part of a period or a range: its energy, the value it earned and the share of the extra settlement cost it carries.</summary>
public sealed record DeviceSavings(DeviceName Device, KilowattHours Energy, double ValueGained, double ExtraCost)
{
    /// <summary>A negative figure is shown as it is: it says the sources or the value are mis-set.</summary>
    public double Savings => ValueGained - ExtraCost;
}

/// <summary>One settlement period (a local hour, or a statistics bucket under net billing) accounted against the controlled loads being off.</summary>
/// <param name="Start">UTC instant of the period's start; for an hour, the start of the local hour (23- and 25-hour days have 23 and 25 of them).</param>
/// <param name="Fallback">Some price of the period was at a dynamic price's fallback: the period is counted and flagged.</param>
public sealed record SavingsPeriod(DateTimeOffset Start, DateTimeOffset End, PeriodStatus Status, bool Fallback, double ValueGained, double ExtraCost, ImmutableArray<DeviceSavings> Devices)
{
    public double Savings => ValueGained - ExtraCost;
}

/// <summary>The counted periods of a range, summed.</summary>
/// <param name="Periods">Counted periods whose start lies in the range.</param>
/// <param name="FallbackPeriods">Of those, the ones with a price at fallback.</param>
/// <param name="UnpricedPeriods">Periods in the range after the first counted one that could not be counted for want of a price.</param>
/// <param name="IncompletePeriods">Hours in the range after the first counted one that were not observed throughout.</param>
public sealed record SavingsTotal(
    DateTimeOffset From,
    DateTimeOffset To,
    int Periods,
    int FallbackPeriods,
    int UnpricedPeriods,
    int IncompletePeriods,
    double ValueGained,
    double ExtraCost,
    ImmutableArray<DeviceSavings> Devices)
{
    public double Savings => ValueGained - ExtraCost;
}

/// <summary>
/// Savings accounting (ECO-11, 14-ECONOMICS, Savings): for each settlement period, with <c>E_d</c> the controlled energy of device d,
/// <c>E</c> their sum and <c>b</c> the period's actual net export, <c>value_gained = sum E_d * v_eff_d</c> (time-weighted, recorded
/// step by step in the statistics), <c>extra_cost = Settle(b + E) - Settle(b)</c> at the period's time-weighted prices, and
/// <c>savings = value_gained - extra_cost</c>. It is computed on read from the retained buckets and the configured settlement model; it writes
/// nothing, reads no clock but the one it is handed and changes no decision. An estimate against the loads being off, not a bill.
/// <para>
/// The period is the model's: the local hour for hourly netting (hour logic shared with the hourly balance, so 23- and 25-hour days
/// have 23 and 25 periods), the statistics bucket for net billing. A period is accounted once complete; the running one is reported apart.
/// </para>
/// </summary>
public static class SavingsAccounting
{
    // Seconds are persisted as doubles and do not round-trip every tick.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Every period the retained buckets hold, oldest first, accounted or not.</summary>
    public static ImmutableArray<SavingsPeriod> Periods(StatisticsSnapshot snapshot, ISettlementModel model, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(zone);
        var hourly = model.NettingPeriod == NettingPeriod.Hour;

        // Buckets after now belong to a stretch a clock step back has not reached again (as in api/statistics).
        var groups = snapshot.Buckets.Values
            .Where(b => b.Start <= now && b.Observed > TimeSpan.Zero)
            .GroupBy(b => hourly ? HourlyBalanceTracker.HourStartOf(b.Start, zone).ToUniversalTime() : b.Start)
            .OrderBy(g => g.Key);
        var periods = ImmutableArray.CreateBuilder<SavingsPeriod>();
        foreach (var group in groups)
        {
            var end = group.Key + (hourly ? Hour : snapshot.BucketLength);
            periods.Add(Period(group.Key, end, [.. group], model, hourly, now));
        }

        return periods.ToImmutable();
    }

    /// <summary>The first counted period's start, the "since" of the savings; null when none is counted.</summary>
    public static DateTimeOffset? Since(ImmutableArray<SavingsPeriod> periods) =>
        periods.Where(p => p.Status == PeriodStatus.Counted).Select(p => (DateTimeOffset?)p.Start).FirstOrDefault();

    /// <summary>The periods starting in [<paramref name="from"/>, <paramref name="to"/>) summed. Only counted periods add up; the others only count.</summary>
    public static SavingsTotal Total(ImmutableArray<SavingsPeriod> periods, DateTimeOffset from, DateTimeOffset to)
    {
        var since = Since(periods);
        var inRange = periods.Where(p => p.Start >= from && p.Start < to).ToList();
        var counted = inRange.Where(p => p.Status == PeriodStatus.Counted).ToList();
        var devices = counted.SelectMany(p => p.Devices).GroupBy(d => d.Device).OrderBy(g => g.Key)
            .Select(g => new DeviceSavings(g.Key, new KilowattHours(g.Sum(d => d.Energy.Value)), g.Sum(d => d.ValueGained), g.Sum(d => d.ExtraCost)))
            .ToImmutableArray();
        return new SavingsTotal(
            from,
            to,
            counted.Count,
            counted.Count(p => p.Fallback),
            since is { } first ? inRange.Count(p => p.Status == PeriodStatus.Unpriced && p.Start >= first) : 0,
            since is { } start ? inRange.Count(p => p.Status == PeriodStatus.Incomplete && p.Start >= start) : 0,
            counted.Sum(p => p.ValueGained),
            counted.Sum(p => p.ExtraCost),
            devices);
    }

    private static SavingsPeriod Period(DateTimeOffset start, DateTimeOffset end, List<EnergyBucket> buckets, ISettlementModel model, bool hourly, DateTimeOffset now)
    {
        var running = end > now;
        var observed = TimeSpan.FromTicks(buckets.Sum(b => b.Observed.Ticks));
        var status = running ? PeriodStatus.Running : PeriodStatus.Counted;
        if (!running && hourly && observed + Tolerance < end - start)
        {
            // The net export of an hour is only known if the hour was watched throughout; a part of it says nothing about the rest.
            return Unaccounted(start, end, PeriodStatus.Incomplete);
        }

        var prices = buckets.Select(b => b.Prices).OfType<BucketPrices>().Aggregate((BucketPrices?)null, (sum, p) => sum is null ? p : sum.Add(p));
        if (prices is null || !Covers(prices.Import, observed) || !Covers(prices.Export, observed))
        {
            return Unaccounted(start, end, PeriodStatus.Unpriced);
        }

        var devices = buckets.SelectMany(b => b.Devices).GroupBy(d => d.Key).OrderBy(g => g.Key)
            .Select(g => (Device: g.Key, Energy: g.Aggregate(DeviceEnergy.Zero, (sum, d) => sum.Add(d.Value))))
            .ToList();
        var controlled = devices.Sum(d => d.Energy.Total.Value);
        var energy = new PeriodEnergy(
            new KilowattHours(buckets.Sum(b => b.GridExport.Value)),
            new KilowattHours(buckets.Sum(b => b.GridImport.Value)),
            new KilowattHours(controlled),
            prices.ControlledOffsettingImport);
        var book = new PriceBook(Point(prices.Import), Point(prices.Export));
        if (Counterfactual.ExtraCost(model, energy, book) is not { } extra)
        {
            return Unaccounted(start, end, PeriodStatus.Unpriced);
        }

        var perDevice = devices
            .Select(d => new DeviceSavings(d.Device, d.Energy.Total, d.Energy.Value, Counterfactual.Share(extra.Amount, d.Energy.Total.Value, controlled)))
            .ToImmutableArray();
        return new SavingsPeriod(start, end, status, prices.Import.AnyFallback || prices.Export.AnyFallback, perDevice.Sum(d => d.ValueGained), extra.Amount, perDevice);
    }

    private static SavingsPeriod Unaccounted(DateTimeOffset start, DateTimeOffset end, PeriodStatus status) =>
        new(start, end, status, false, 0, 0, []);

    /// <summary>A price kind that was recorded at all must have been known throughout the observed time; a kind never configured (no export price, no tariff) is simply absent.</summary>
    private static bool Covers(PriceTrack track, TimeSpan observed) => track.Priced == TimeSpan.Zero || track.Priced + Tolerance >= observed;

    private static PricePoint? Point(PriceTrack track) =>
        track.Mean is { } mean
            ? new PricePoint(
                mean,
                track.AnyFallback ? PriceBasis.Fallback : track.Live > TimeSpan.Zero ? PriceBasis.Live : track.Zone > TimeSpan.Zero ? PriceBasis.Zone : PriceBasis.Flat,
                null)
            : null;
}
