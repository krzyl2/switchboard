using System.Collections.Immutable;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.TestSteps;

namespace Switchboard.Tests.Application.Presentation;

/// <summary>
/// The statistics store the prices dispatch used (ECO-11): per bucket the time-weighted import and export price with their basis, per
/// device the energy at the effective value in force. Prices are held from step to step on the signal clock, exactly like every other
/// figure, so a price that changes inside a bucket must weigh by how long it lasted, never by its last or first value.
/// </summary>
public sealed class PriceAccumulationTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    private static readonly DeviceName Heater = DeviceName.Parse("heater_l1");
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly HourlyNettingModel Hourly = new(new HourlyBalanceParameters(150, 525, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60)));
    private static readonly NetBillingModel Billing = new(new Watts(200), new Watts(-100), TimeSpan.FromSeconds(60));

    // 12:00:00 UTC, a whole 5-minute bucket of the test options starts here.
    private static readonly DateTimeOffset T0 = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static PricePoint? Price(double? value, PriceBasis basis = PriceBasis.Flat, string? zone = null) => value is { } v ? new PricePoint(v, basis, zone) : null;

    /// <summary>The step's dispatch status with the economics view dispatch would have had: two prices and the heater's effective value.</summary>
    private static DispatchStatus Status(DateTimeOffset at, PricePoint? import, PricePoint? export, double heaterValue) =>
        Intended(at) with
        {
            Economics = new EconomicsView(import?.Zone, import, export, SettlementModelId.HourlyNetting, [new DeviceEconomicsView(Heater, new EffectiveValue(heaterValue, ValueBasis.Static), heaterValue, null, null)]),
        };

    private static PowerView Heating(DateTimeOffset at, double netImport = 2000, double generation = 0) =>
        View(at, netImport, generation, ("heater_l1", 1500, PowerSource.Measured));

    /// <summary>Observes the steps of [from, to] every 10 s; <paramref name="status"/> may return null (no economics view) for a step.</summary>
    private static EnergyAccumulator Run(DateTimeOffset from, DateTimeOffset to, Func<DateTimeOffset, DispatchStatus?> status, Func<DateTimeOffset, PowerView>? view = null)
    {
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        for (var at = from; at <= to; at += Poll)
        {
            accumulator.Observe(Step(at, status: status(at)), (view ?? (t => Heating(t)))(at));
        }

        return accumulator;
    }

    private static EnergyBucket The(EnergyAccumulator accumulator, DateTimeOffset bucket) => accumulator.Snapshot().Buckets[bucket];

    [Fact]
    public void A_dynamic_price_that_changes_inside_a_bucket_is_weighted_by_how_long_each_price_held()
    {
        // Why: the live price moves on the :00 or any time it is reported. 120 s at 0.60 and 180 s at 1.10 average to
        // (0.60 x 120 + 1.10 x 180) / 300 = 0.90; the price at the end of the bucket (1.10) or at its start (0.60) would misstate the period's cost.
        var change = T0.AddSeconds(120);
        var accumulator = Run(T0, T0.AddMinutes(5), at => Status(at, Price(at < change ? 0.60 : 1.10, PriceBasis.Live), Price(0.35), 0.50));

        var prices = The(accumulator, T0).Prices!;
        Assert.Equal(0.90, prices.Import.Mean!.Value, 9);
        Assert.Equal(0.35, prices.Export.Mean!.Value, 9);
        Assert.Equal(TimeSpan.FromMinutes(5), prices.Import.Live);
        Assert.False(prices.Import.AnyFallback);
    }

    [Fact]
    public void A_zone_change_inside_a_bucket_is_weighted_the_same_way_and_keeps_both_in_the_zone_basis()
    {
        // Why: a tariff zone can change at a step inside a bucket (a configuration change, a clock step). 120 s at 1.10 then 180 s at 0.60 is 0.80.
        var change = T0.AddSeconds(120);
        var accumulator = Run(T0, T0.AddMinutes(5), at => Status(at, at < change ? Price(1.10, PriceBasis.Zone, "morning_peak") : Price(0.60, PriceBasis.Zone, "offpeak"), Price(0.35), 0.50));

        var import = The(accumulator, T0).Prices!.Import;
        Assert.Equal(0.80, import.Mean!.Value, 9);
        Assert.Equal((TimeSpan.FromMinutes(5), TimeSpan.Zero), (import.Zone, import.Flat));
    }

    [Fact]
    public void Time_at_a_price_fallback_is_recorded_and_flags_the_bucket()
    {
        // Why: ECO-11 counts a bucket with a fallback price and flags it. 180 s live at 0.60 and 120 s at the 0.90 fallback: mean 0.72, 120 s of fallback.
        var change = T0.AddSeconds(180);
        var accumulator = Run(T0, T0.AddMinutes(5), at => Status(at, at < change ? Price(0.60, PriceBasis.Live) : Price(0.90, PriceBasis.Fallback), Price(0.35), 0.50));

        var import = The(accumulator, T0).Prices!.Import;
        Assert.Equal(0.72, import.Mean!.Value, 9);
        Assert.True(import.AnyFallback);
        Assert.Equal((TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(120)), (import.Live, import.Fallback));
    }

    [Fact]
    public void The_value_of_a_device_is_its_energy_at_the_effective_value_of_each_step()
    {
        // Why: value_gained is energy x v_eff time-weighted. The heater draws 1.5 kW for 300 s; its value follows a price that is 0.50 for the first 120 s
        // and 1.00 for the next 180 s: 1.5 x (120/3600) x 0.50 + 1.5 x (180/3600) x 1.00 = 0.025 + 0.075 = 0.10 (a static 0.50 would give 0.0625).
        var change = T0.AddSeconds(120);
        var accumulator = Run(T0, T0.AddMinutes(5), at => Status(at, Price(1.10), Price(0.35), at < change ? 0.50 : 1.00));

        Assert.Equal(0.10, The(accumulator, T0).Devices[Heater].Value, 9);
    }

    [Fact]
    public void The_controlled_energy_that_ran_while_importing_is_recorded_for_net_billing()
    {
        // Why: net billing pays import and export apart, so the counterfactual needs how much of the controlled energy ran in steps that imported.
        // Heater 1.5 kW with 2 kW imported all along: 0.125 kWh in 300 s, all of it while importing. With the house exporting 0.5 kW it is none.
        var importing = Run(T0, T0.AddMinutes(5), at => Status(at, Price(1.10), Price(0.35), 0.50));
        var exporting = Run(T0, T0.AddMinutes(5), at => Status(at, Price(1.10), Price(0.35), 0.50), at => Heating(at, netImport: -500, generation: 2000));

        Assert.Equal(0.125, The(importing, T0).Prices!.ControlledOffsettingImport.Value, 9);
        Assert.Equal(0, The(exporting, T0).Prices!.ControlledOffsettingImport.Value, 9);
    }

    [Fact]
    public void Without_an_economics_view_no_price_data_is_recorded_and_the_value_stays_the_static_one()
    {
        // Why: no economics section, no prices: the 0.9 statistics must come out unchanged (Value = energy x value_per_kwh) and the bucket must say
        // it has no price data rather than a zero price.
        var accumulator = Run(T0, T0.AddMinutes(5), _ => null);

        var bucket = The(accumulator, T0);
        Assert.Null(bucket.Prices);
        Assert.Equal(0.125 * TestSteps.Setup.Device(Heater)!.Spec.ValuePerKwh, bucket.Devices[Heater].Value, 9);
    }

    [Fact]
    public void A_view_lost_for_part_of_a_bucket_leaves_the_bucket_unpriced_for_savings()
    {
        // Why: a bucket whose price was known for 3 of its 5 minutes would value the other 2 minutes by nothing. Savings must leave it out rather than
        // account a price mean over part of the energy.
        var lost = T0.AddSeconds(180);
        var accumulator = Run(T0, T0.AddMinutes(5), at => at < lost ? Status(at, Price(1.10), Price(0.35), 0.50) : null);
        var now = T0.AddMinutes(5);

        var period = Assert.Single(SavingsAccounting.Periods(accumulator.Snapshot(), Billing, Warsaw, now));

        Assert.Equal(TimeSpan.FromSeconds(180), The(accumulator, T0).Prices!.Import.Priced);
        Assert.Equal(PeriodStatus.Unpriced, period.Status);
    }

    [Fact]
    public void A_whole_hour_of_steps_gives_the_hand_computed_saving()
    {
        // Why: the chain from control steps to the saving, end to end. The house exports 0.5 kW all hour (0.5 kWh) while the heater draws 1.5 kW
        // (1.5 kWh, value 0.50): value 0.75; without the heater the hour would export 2.0 kWh instead of 0.5, so the extra cost is
        // (2.0 - 0.5) x 0.35 = 0.525 and the saving 0.225. 12:00-13:00 Warsaw is 10:00-11:00 UTC.
        var start = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var accumulator = Run(start, end, at => Status(at, Price(1.10), Price(0.35), 0.50), at => Heating(at, netImport: -500, generation: 2000));

        var period = Assert.Single(SavingsAccounting.Periods(accumulator.Snapshot(), Hourly, Warsaw, end));

        Assert.Equal(PeriodStatus.Counted, period.Status);
        Assert.Equal(0.75, period.ValueGained, 9);
        Assert.Equal(0.525, period.ExtraCost, 9);
        Assert.Equal(0.225, period.Savings, 9);
    }

    [Fact]
    public void Prices_add_up_over_buckets_and_a_range_keeps_the_time_weighting()
    {
        // Why: the range totals of api/statistics add buckets; a mean of the bucket means would weight a 1-minute bucket like a 5-minute one.
        var accumulator = Run(T0, T0.AddMinutes(10), at => Status(at, Price(at < T0.AddMinutes(5) ? 0.60 : 1.20), Price(0.35), 0.50));

        var sum = accumulator.Snapshot().Buckets.Values.Where(b => b.Prices is not null).Aggregate(EnergyBucket.Empty(T0), (total, b) => total.Add(b));

        Assert.Equal(0.90, sum.Prices!.Import.Mean!.Value, 9);
        Assert.Equal(TimeSpan.FromMinutes(10), sum.Prices.Import.Priced);
    }
}
