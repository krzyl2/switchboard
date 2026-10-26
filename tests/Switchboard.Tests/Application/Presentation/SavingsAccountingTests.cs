using System.Collections.Immutable;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Application.Presentation;

/// <summary>
/// Savings accounting (ECO-11): per settlement period, value gained minus the extra settlement cost against the controlled loads being
/// off. The fixtures are whole hours of 15-minute buckets with figures worked out by hand (prices: import 1.10, export 0.35), on the
/// Warsaw zone and a virtual "now", so each number can be checked on paper.
/// </summary>
public sealed class SavingsAccountingTests
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly HourlyNettingModel Hourly = new(new HourlyBalanceParameters(150, 525, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60)));
    private static readonly NetBillingModel Billing = new(new Watts(200), new Watts(-100), TimeSpan.FromSeconds(60));

    private static readonly DeviceName Heater = DeviceName.Parse("heater_l1");
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>One quarter-hour bucket, fully observed, with the prices dispatch had for all of it (null = none recorded).</summary>
    private static EnergyBucket Bucket(
        DateTimeOffset start,
        double export,
        double import,
        (DeviceName Device, double Kwh, double Value)[] devices,
        double? importPrice = 1.10,
        double? exportPrice = 0.35,
        PriceBasis basis = PriceBasis.Flat,
        double offsettingImport = 0,
        TimeSpan? observed = null,
        bool prices = true)
    {
        var seen = observed ?? Quarter;
        var track = (double? price) => price is { } p ? PriceTrack.Zero.Held(new PricePoint(p, basis, null), seen) : PriceTrack.Zero;
        return EnergyBucket.Empty(start) with
        {
            Observed = seen,
            GridExport = new KilowattHours(export),
            GridImport = new KilowattHours(import),
            Devices = devices.ToImmutableDictionary(d => d.Device, d => new DeviceEnergy(new KilowattHours(d.Kwh), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, d.Value)),
            Prices = prices ? new BucketPrices(track(importPrice), track(exportPrice), new KilowattHours(offsettingImport)) : null,
        };
    }

    /// <summary>An hour of four equal buckets that together carry the given totals; the value is the energy at <paramref name="valuePerKwh"/>.</summary>
    private static IEnumerable<EnergyBucket> Hour(
        DateTimeOffset start,
        double export,
        double import,
        double heaterKwh,
        double valuePerKwh,
        double offsettingImport = 0,
        PriceBasis basis = PriceBasis.Flat,
        bool prices = true,
        int buckets = 4) =>
        Enumerable.Range(0, buckets).Select(i => Bucket(
            start + (i * Quarter),
            export / 4,
            import / 4,
            [(Heater, heaterKwh / 4, heaterKwh / 4 * valuePerKwh)],
            basis: basis,
            offsettingImport: offsettingImport / 4,
            prices: prices));

    private static StatisticsSnapshot Snapshot(IEnumerable<EnergyBucket> buckets, DateTimeOffset now) =>
        new(now, Quarter, false, [], buckets.ToImmutableSortedDictionary(b => b.Start, b => b), LifetimeCounters.Zero, null, 0);

    private static SavingsPeriod Only(StatisticsSnapshot snapshot, ISettlementModel model, DateTimeOffset now) =>
        Assert.Single(SavingsAccounting.Periods(snapshot, model, Warsaw, now));

    [Fact]
    public void An_hour_in_net_export_nets_the_extra_cost_at_the_export_price()
    {
        // Why: the headline case, worked by hand. Heater 1.5 kWh at 0.50 = 0.75 gained; the hour exported 3.0, so without the heater it would have
        // exported 4.5: the heater cost 1.5 x 0.35 = 0.525; savings 0.225.
        var now = Utc(2026, 7, 15, 11);
        var period = Only(Snapshot(Hour(Utc(2026, 7, 15, 10), export: 3, import: 0, heaterKwh: 1.5, valuePerKwh: 0.50), now), Hourly, now);

        Assert.Equal(PeriodStatus.Counted, period.Status);
        Assert.Equal(0.75, period.ValueGained, 12);
        Assert.Equal(0.525, period.ExtraCost, 12);
        Assert.Equal(0.225, period.Savings, 12);
    }

    [Fact]
    public void An_hour_in_net_import_costs_the_loads_the_import_price_not_the_value_alone()
    {
        // Why: the counterfactual is what separates this from "value of the energy delivered". The car's 1.5 kWh at 2.03 is 3.045 of value, but
        // the hour imported 4.0 and would have imported 2.5 without it: 1.5 x 1.10 = 1.65 of extra cost, savings 1.395, not 3.045.
        var now = Utc(2026, 7, 15, 11);
        var car = Enumerable.Range(0, 4).Select(i => Bucket(Utc(2026, 7, 15, 10) + (i * Quarter), 0, 1.0, [(Car, 0.375, 0.375 * 2.03)], offsettingImport: 0.375));
        var period = Only(Snapshot(car, now), Hourly, now);

        Assert.Equal(3.045, period.ValueGained, 12);
        Assert.Equal(1.65, period.ExtraCost, 12);
        Assert.Equal(1.395, period.Savings, 12);
    }

    [Fact]
    public void A_mixed_hour_that_crosses_zero_prices_both_sides_and_a_negative_saving_is_shown()
    {
        // Why: exported 0.5 and imported 1.0 (b = -0.5), heater 1.5 kWh at 0.50 = 0.75. b + E = +1.0, extra = 0.5 x 1.10 + 1.0 x 0.35 = 0.90 and the
        // saving is -0.15: the sources or the value are mis-set, and the design says that is shown, not hidden or clamped to zero.
        var now = Utc(2026, 7, 15, 11);
        var period = Only(Snapshot(Hour(Utc(2026, 7, 15, 10), export: 0.5, import: 1.0, heaterKwh: 1.5, valuePerKwh: 0.50, offsettingImport: 1.0), now), Hourly, now);

        Assert.Equal(0.90, period.ExtraCost, 12);
        Assert.Equal(-0.15, period.Savings, 12);
        var total = SavingsAccounting.Total([period], Utc(2026, 7, 15, 0), Utc(2026, 7, 16, 0));
        Assert.Equal(-0.15, total.Savings, 12);
        Assert.Equal(-0.15, Assert.Single(total.Devices).Savings, 12);
    }

    [Fact]
    public void An_hour_without_controlled_energy_is_counted_with_nothing_gained_and_nothing_lost()
    {
        // Why: a quiet hour is a priced hour with a zero saving, not an unpriced one; it also moves "since" earlier, which is honest.
        var now = Utc(2026, 7, 15, 11);
        var period = Only(Snapshot(Hour(Utc(2026, 7, 15, 10), export: 3, import: 0, heaterKwh: 0, valuePerKwh: 0.50), now), Hourly, now);

        Assert.Equal((PeriodStatus.Counted, 0.0, 0.0), (period.Status, period.ValueGained, period.ExtraCost));
        Assert.Equal(Utc(2026, 7, 15, 10), SavingsAccounting.Since([period]));
    }

    [Fact]
    public void The_extra_cost_is_split_over_the_devices_by_energy_and_the_shares_add_up()
    {
        // Why: the per-device view must add up to the total. Heater 1.0 kWh (value 0.50/kWh) and car 0.5 kWh (value 2.03/kWh) in an hour exporting 3.0:
        // extra 1.5 x 0.35 = 0.525 split 2:1 = 0.35 and 0.175.
        var now = Utc(2026, 7, 15, 11);
        var buckets = Enumerable.Range(0, 4).Select(i => Bucket(Utc(2026, 7, 15, 10) + (i * Quarter), 0.75, 0, [(Heater, 0.25, 0.125), (Car, 0.125, 0.125 * 2.03)]));
        var period = Only(Snapshot(buckets, now), Hourly, now);

        var heater = period.Devices.Single(d => d.Device == Heater);
        var car = period.Devices.Single(d => d.Device == Car);
        Assert.Equal(0.35, heater.ExtraCost, 12);
        Assert.Equal(0.175, car.ExtraCost, 12);
        Assert.Equal(period.ExtraCost, period.Devices.Sum(d => d.ExtraCost), 12);
        Assert.Equal(period.Savings, period.Devices.Sum(d => d.Savings), 12);
        Assert.Equal(0.5 - 0.35, heater.Savings, 12);
    }

    [Fact]
    public void The_25_hour_day_has_25_hourly_periods_and_the_repeated_hour_counts_twice()
    {
        // Why: 2026-10-25 02:00-03:00 happens twice in Warsaw (00:00Z and 01:00Z). A period keyed by the local clock label would merge them and
        // lose an hour of savings; every one of the 25 hours must be accounted on its own. Each hour: export 3, heater 1.5 at 0.50, saving 0.225.
        var dayStart = Utc(2026, 10, 24, 22);
        var now = Utc(2026, 10, 26, 0);
        var buckets = Enumerable.Range(0, 25).SelectMany(h => Hour(dayStart.AddHours(h), 3, 0, 1.5, 0.50));
        var periods = SavingsAccounting.Periods(Snapshot(buckets, now), Hourly, Warsaw, now);

        Assert.Equal(25, periods.Length);
        Assert.Equal(25, periods.Select(p => p.Start).Distinct().Count());
        Assert.All(periods, p => Assert.Equal(PeriodStatus.Counted, p.Status));
        var day = SavingsAccounting.Total(periods, dayStart, dayStart.AddHours(25));
        Assert.Equal(25, day.Periods);
        Assert.Equal(25 * 0.225, day.Savings, 9);
    }

    [Fact]
    public void The_23_hour_day_has_23_hourly_periods()
    {
        // Why: 2026-03-29 loses the 02:00-03:00 hour; accounting must not invent it (24 periods) or shift later hours by one.
        var dayStart = Utc(2026, 3, 28, 23);
        var now = Utc(2026, 3, 30, 0);
        var buckets = Enumerable.Range(0, 23).SelectMany(h => Hour(dayStart.AddHours(h), 3, 0, 1.5, 0.50));
        var periods = SavingsAccounting.Periods(Snapshot(buckets, now), Hourly, Warsaw, now);

        Assert.Equal(23, periods.Length);
        var day = SavingsAccounting.Total(periods, dayStart, dayStart.AddHours(23));
        Assert.Equal((23, 23 * 0.225), (day.Periods, Math.Round(day.Savings, 9)));
    }

    [Fact]
    public void Hours_without_prices_are_not_counted_nothing_is_back_filled_and_since_is_the_first_priced_hour()
    {
        // Why: ECO-11, "never back-filled". 10:00Z has no price data (an old file, or economics not configured yet), 11:00Z and 12:00Z are priced,
        // 13:00Z lost its prices (a gap in the view). Only 11:00Z and 12:00Z add up; since is 11:00Z; the later gap is reported as unpriced, the
        // first hour (before since) is not, because it says nothing about the savings from then on.
        var now = Utc(2026, 7, 15, 14);
        var buckets = Hour(Utc(2026, 7, 15, 10), 3, 0, 1.5, 0.50, prices: false)
            .Concat(Hour(Utc(2026, 7, 15, 11), 3, 0, 1.5, 0.50))
            .Concat(Hour(Utc(2026, 7, 15, 12), 3, 0, 1.5, 0.50))
            .Concat(Hour(Utc(2026, 7, 15, 13), 3, 0, 1.5, 0.50, prices: false));
        var periods = SavingsAccounting.Periods(Snapshot(buckets, now), Hourly, Warsaw, now);
        var total = SavingsAccounting.Total(periods, Utc(2026, 7, 15, 0), Utc(2026, 7, 16, 0));

        Assert.Equal(Utc(2026, 7, 15, 11), SavingsAccounting.Since(periods));
        Assert.Equal(2, total.Periods);
        Assert.Equal(2 * 0.225, total.Savings, 12);
        Assert.Equal(1, total.UnpricedPeriods);
    }

    [Fact]
    public void A_price_known_for_only_part_of_the_hour_leaves_the_hour_unpriced()
    {
        // Why: a price recorded for 10 of 15 minutes of a bucket would be a mean over the part that had one, and the energy of the other five
        // minutes would be valued by nothing. The hour is not counted rather than guessed.
        var now = Utc(2026, 7, 15, 11);
        var partly = Bucket(Utc(2026, 7, 15, 10), 0.75, 0, [(Heater, 0.375, 0.1875)]) with
        {
            Prices = new BucketPrices(PriceTrack.Zero.Held(new PricePoint(1.10, PriceBasis.Flat, null), TimeSpan.FromMinutes(10)), PriceTrack.Zero.Held(new PricePoint(0.35, PriceBasis.Flat, null), TimeSpan.FromMinutes(10)), KilowattHours.Zero),
        };
        var buckets = Hour(Utc(2026, 7, 15, 10), 3, 0, 1.5, 0.50).Skip(1).Prepend(partly);

        Assert.Equal(PeriodStatus.Unpriced, Only(Snapshot(buckets, now), Hourly, now).Status);
    }

    [Fact]
    public void A_period_with_a_price_at_fallback_is_counted_and_flagged()
    {
        // Why: the design counts it (the configured fallback is a real price) and flags it, so the user sees which figures rest on a stand-in.
        var now = Utc(2026, 7, 15, 12);
        var buckets = Hour(Utc(2026, 7, 15, 10), 3, 0, 1.5, 0.50, basis: PriceBasis.Fallback).Concat(Hour(Utc(2026, 7, 15, 11), 3, 0, 1.5, 0.50));
        var periods = SavingsAccounting.Periods(Snapshot(buckets, now), Hourly, Warsaw, now);
        var total = SavingsAccounting.Total(periods, Utc(2026, 7, 15, 0), Utc(2026, 7, 16, 0));

        Assert.Equal([true, false], periods.Select(p => p.Fallback));
        Assert.Equal((2, 1), (total.Periods, total.FallbackPeriods));
    }

    [Fact]
    public void The_hour_in_progress_is_reported_as_running_and_is_in_no_total()
    {
        // Why: the net export of an unfinished hour can still change sign, so its saving is a preview; summing it would let a number go down
        // after it was shown as part of "today".
        var now = Utc(2026, 7, 15, 10, 30);
        var half = Hour(Utc(2026, 7, 15, 10), 1.5, 0, 0.75, 0.50, buckets: 2);
        var periods = SavingsAccounting.Periods(Snapshot(half, now), Hourly, Warsaw, now);

        var running = Assert.Single(periods);
        Assert.Equal(PeriodStatus.Running, running.Status);
        Assert.Equal(0.375 * 0.50, running.ValueGained, 12); // two quarter-hours of a 0.1875 kWh each
        var total = SavingsAccounting.Total(periods, Utc(2026, 7, 15, 0), Utc(2026, 7, 16, 0));
        Assert.Equal((0, 0.0), (total.Periods, total.Savings));
        Assert.Null(SavingsAccounting.Since(periods));
    }

    [Fact]
    public void An_hour_that_was_not_watched_throughout_is_incomplete_not_accounted()
    {
        // Why: with 15 minutes missing (a restart) the hour's net export is not known, and a saving computed from the rest could be of either sign.
        var now = Utc(2026, 7, 15, 13);
        var buckets = Hour(Utc(2026, 7, 15, 10), 3, 0, 1.5, 0.50, buckets: 3).Concat(Hour(Utc(2026, 7, 15, 11), 3, 0, 1.5, 0.50));
        var periods = SavingsAccounting.Periods(Snapshot(buckets, now), Hourly, Warsaw, now);
        var total = SavingsAccounting.Total(periods, Utc(2026, 7, 15, 0), Utc(2026, 7, 16, 0));

        Assert.Equal([PeriodStatus.Incomplete, PeriodStatus.Counted], periods.Select(p => p.Status));
        Assert.Equal(0.225, total.Savings, 12);
    }

    [Fact]
    public void Under_net_billing_every_closed_bucket_is_a_period_and_import_and_export_are_paid_apart()
    {
        // Why: net billing has no hour to net in. One bucket: exported 2, imported 1, heater 1.5 kWh of which 1.0 ran while importing, at value 0.60:
        // 0.90 gained; without the heater the bucket would have imported 0 and exported 2.5: Settle goes from 0.7 - 1.1 = -0.4 to 0.875, extra 1.275,
        // saving -0.375. Hourly netting on the same figures would price the net +1 only (extra 0.525): the models must differ.
        var now = Utc(2026, 7, 15, 10, 15);
        var bucket = Bucket(Utc(2026, 7, 15, 10), 2, 1, [(Heater, 1.5, 0.90)], offsettingImport: 1.0);
        var billing = Only(Snapshot([bucket], now), Billing, now);
        var hourly = SavingsAccounting.Periods(Snapshot([bucket], Utc(2026, 7, 15, 11)), Hourly, Warsaw, Utc(2026, 7, 15, 11)).Single();

        Assert.Equal(PeriodStatus.Counted, billing.Status);
        Assert.Equal(Utc(2026, 7, 15, 10) + Quarter, billing.End);
        Assert.Equal(1.275, billing.ExtraCost, 12);
        Assert.Equal(0.90 - 1.275, billing.Savings, 12);
        Assert.Equal(PeriodStatus.Incomplete, hourly.Status);
        Assert.Equal(1.5 * 0.35, Counterfactual.ExtraCost(Hourly, new PeriodEnergy(new KilowattHours(2), new KilowattHours(1), new KilowattHours(1.5), new KilowattHours(1.0)), new PriceBook(new PricePoint(1.10, PriceBasis.Flat, null), new PricePoint(0.35, PriceBasis.Flat, null)))!.Value.Amount, 12);
    }

    [Fact]
    public void Buckets_after_now_belong_to_a_clock_that_stepped_back_and_are_not_accounted()
    {
        // Why: as in api/statistics, a bucket after "now" is a stretch the clock has not reached again; a savings figure must not count the future.
        var now = Utc(2026, 7, 15, 10);
        var future = Hour(Utc(2026, 7, 15, 10), 3, 0, 1.5, 0.50);

        Assert.Empty(SavingsAccounting.Periods(Snapshot(future, now - Quarter), Hourly, Warsaw, now - Quarter));
    }
}
