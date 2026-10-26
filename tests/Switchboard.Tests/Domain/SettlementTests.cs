using Switchboard.Domain.Balance;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Domain;

/// <summary>
/// Settlement module (D6, ECO-03). The settlement figure is what savings and the settlement estimate will be built on, so
/// the two models must differ exactly where the money differs (a mixed period), and the hourly model's allowance must be the
/// released hourly balance rule, not a second implementation of it (the BAL tests stay untouched and are the oracle).
/// </summary>
public sealed class SettlementTests
{
    private static readonly HourlyBalanceParameters Parameters = new(
        FloorWh: 150,
        CeilingWh: 525,
        BurnWindow: TimeSpan.FromMinutes(15),
        SmoothingWindow: TimeSpan.FromMinutes(5),
        MinRemaining: TimeSpan.FromMinutes(1),
        MaxAge: TimeSpan.FromSeconds(60));

    private static readonly HourlyNettingModel Hourly = new(Parameters);

    // The dispatch hysteresis of the shipped defaults is not read here: these are explicit test values.
    private static readonly NetBillingModel Billing = new(new Watts(200), new Watts(-100), TimeSpan.FromSeconds(60));

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly PriceBook Prices = new(
        new PricePoint(1.10, PriceBasis.Zone, "morning_peak"),
        new PricePoint(0.35, PriceBasis.Flat, null));

    private static PeriodFlows Flows(double exported, double imported) => new(new KilowattHours(exported), new KilowattHours(imported));

    private static Watts W(double value) => new(value);

    public static TheoryData<double, double, double, double> SettleTable => new()
    {
        // exported, imported, hourly netting, net billing
        { 2.0, 0.5, 1.5 * 0.35, (2.0 * 0.35) - (0.5 * 1.10) },    // mixed: the hour nets to export, but billing pays the import apart
        { 3.0, 0.0, 3.0 * 0.35, 3.0 * 0.35 },                      // pure export: the models agree
        { 0.0, 1.0, -1.0 * 1.10, -1.0 * 1.10 },                    // pure import: a cost, the models agree
        { 1.0, 1.0, 0.0, 0.35 - 1.10 },                            // balanced hour: netting cancels it, billing does not
        { 0.0, 0.0, 0.0, 0.0 },                                    // nothing exchanged
        { 0.5, 2.0, -1.5 * 1.10, (0.5 * 0.35) - (2.0 * 1.10) },    // mixed: the hour nets to import
    };

    [Theory]
    [MemberData(nameof(SettleTable))]
    public void Settle_pays_the_two_models_as_the_design_table_says(double exported, double imported, double hourly, double billing)
    {
        // Why: a flipped sign or a model that nets when it should not changes every saving figure silently.
        Assert.Equal(hourly, Hourly.Settle(Flows(exported, imported), Prices)!.Value.Amount, 9);
        Assert.Equal(billing, Billing.Settle(Flows(exported, imported), Prices)!.Value.Amount, 9);
    }

    [Fact]
    public void Settle_without_the_price_it_needs_is_unknown_not_zero()
    {
        // Why: a missing price must never become a guess; a balanced hour needs no price at all.
        var noExport = new PriceBook(Prices.Import, null);
        var noImport = new PriceBook(null, Prices.Export);

        Assert.Null(Hourly.Settle(Flows(2, 0), noExport));
        Assert.Null(Hourly.Settle(Flows(0, 2), noImport));
        Assert.Null(Billing.Settle(Flows(2, 1), noExport));
        Assert.Null(Billing.Settle(Flows(2, 1), noImport));
        Assert.Equal(Money.Zero, Hourly.Settle(Flows(1, 1), PriceBook.None));
        Assert.Equal(Money.Zero, Billing.Settle(Flows(0, 0), PriceBook.None));
        Assert.Equal(1.4, Billing.Settle(Flows(4, 0), noImport)!.Value.Amount, 9);
    }

    [Fact]
    public void Models_state_their_netting_period_and_export_value()
    {
        Assert.Equal((SettlementModelId.HourlyNetting, NettingPeriod.Hour), (Hourly.Id, Hourly.NettingPeriod));
        Assert.Equal((SettlementModelId.NetBilling, NettingPeriod.Step), (Billing.Id, Billing.NettingPeriod));
        var t = new DateTimeOffset(2026, 7, 15, 6, 30, 0, TimeSpan.Zero);
        Assert.Equal(0.35, Hourly.ExportValue(t, Prices)!.Value.PerKwh);
        Assert.Equal(0.35, Billing.ExportValue(t, Prices)!.Value.PerKwh);
        Assert.Null(Billing.ExportValue(t, PriceBook.None));
    }

    [Fact]
    public void A_feed_in_tariff_is_net_billing_with_a_flat_export_price()
    {
        // Why: the design ships no separate feed-in class; one flat export price must settle per kWh exported, however much was imported.
        var feedIn = new PriceBook(Prices.Import, new PricePoint(0.80, PriceBasis.Flat, null));

        Assert.Equal((6 * 0.80) - (2 * 1.10), Billing.Settle(Flows(6, 2), feedIn)!.Value.Amount, 9);
    }

    [Fact]
    public void Hourly_netting_decides_exactly_as_the_hourly_balance_rule_over_a_grid_of_inputs()
    {
        // Why: the model wraps the released rule; any drift (a different comparison, a swapped argument) would change
        // 0.9 decisions once dispatch consumes the model. The rule's own tests are the oracle and stay untouched.
        double?[] balances = [null, -300, 0, 100, 400, 800];
        Watts?[] frees = [null, W(-500), W(0), W(1000), W(3000), W(6000)];
        var untils = new[] { 0.2, 1, 5, 14, 15, 30, 59 }.Select(TimeSpan.FromMinutes).ToArray();
        var count = 0;
        foreach (var balance in balances)
        {
            foreach (var free in frees)
            {
                foreach (var load in new[] { 0.0, 1500, 3000 })
                {
                    foreach (var candidate in new[] { 0.0, 1500, 3000 })
                    {
                        foreach (var until in untils)
                        {
                            var expected = HourlyBalanceRule.Decide(balance, free, W(load), W(candidate), until, Parameters);
                            var actual = Hourly.Decide(new SurplusView(balance, free, until), W(load), W(candidate));
                            Assert.Equal(Map(expected), actual.Decision);
                            count++;
                        }
                    }
                }
            }
        }

        Assert.Equal(6 * 6 * 3 * 3 * 7, count);
    }

    private static AllowanceDecision Map(BalanceWant want) => want switch
    {
        BalanceWant.Start => AllowanceDecision.Start,
        BalanceWant.Stop => AllowanceDecision.Stop,
        BalanceWant.Hold => AllowanceDecision.Hold,
        _ => AllowanceDecision.Unknown,
    };

    [Fact]
    public void Hourly_netting_reasons_follow_the_balance_codes()
    {
        var until = TimeSpan.FromMinutes(30);

        Assert.Equal(AllowanceReason.BalanceSurplus, Hourly.Decide(new SurplusView(800, W(3000), until), W(0), W(1500)).Reason);
        Assert.Equal(AllowanceReason.BalanceProjectionLow, Hourly.Decide(new SurplusView(-300, W(0), until), W(1500), W(1500)).Reason);
        Assert.Equal(AllowanceReason.InputUnknown, Hourly.Decide(new SurplusView(null, W(3000), until), W(0), W(1500)).Reason);
    }

    [Fact]
    public void Net_billing_starts_at_the_candidate_plus_the_start_hysteresis_and_not_below()
    {
        // surplus_w = free - load. Candidate 1500 + hysteresis 200 = 1700: the boundary is inclusive, one watt less holds.
        var view = new SurplusView(null, W(2700), TimeSpan.FromMinutes(30));

        Assert.Equal(new Allowance(AllowanceDecision.Start, AllowanceReason.SurplusAvailable), Billing.Decide(view, W(1000), W(1500)));
        Assert.Equal(AllowanceDecision.Hold, Billing.Decide(view with { Free = W(2699) }, W(1000), W(1500)).Decision);
    }

    [Fact]
    public void Net_billing_stops_only_below_the_negative_stop_hysteresis()
    {
        // The stop band is what keeps a device from flapping: -100 W is tolerated, -101 W is not.
        var view = new SurplusView(null, W(900), TimeSpan.FromMinutes(30));

        Assert.Equal(AllowanceDecision.Hold, Billing.Decide(view, W(1000), W(1500)).Decision);
        Assert.Equal(new Allowance(AllowanceDecision.Stop, AllowanceReason.SurplusLow), Billing.Decide(view with { Free = W(899) }, W(1000), W(1500)));
    }

    [Fact]
    public void Net_billing_with_unknown_free_power_is_unknown_never_a_start_or_a_stop()
    {
        // Why: the failure table says no surplus-class start when free power is unknown; the balance counters are irrelevant to it.
        var view = new SurplusView(800, null, TimeSpan.FromMinutes(30));

        Assert.Equal(new Allowance(AllowanceDecision.Unknown, AllowanceReason.InputUnknown), Billing.Decide(view, W(0), W(1500)));
        Assert.Equal(AllowanceDecision.Unknown, Billing.Decide(view with { Free = W(double.NaN) }, W(0), W(1500)).Decision);
    }

    [Fact]
    public void Net_billing_ignores_the_balance_and_the_time_to_the_hour()
    {
        // Why: net billing has no hour to project; a stale or absent hourly balance must not change its answer.
        var a = Billing.Decide(new SurplusView(-5000, W(3000), TimeSpan.FromMinutes(1)), W(0), W(1500));
        var b = Billing.Decide(new SurplusView(null, W(3000), TimeSpan.FromMinutes(59)), W(0), W(1500));

        Assert.Equal(a, b);
    }

    private static Tariff G13()
    {
        var economics = TestConfigurations.Valid(TestConfigurations.Example.TrimEnd() + "\n\n" + File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"))).Configuration!.Economics!;
        return new Tariff(economics.Tariff!, economics.Holidays, Warsaw);
    }

    [Fact]
    public void A_period_price_is_the_time_weighted_mean_of_the_zones_in_force()
    {
        // 06:30-07:30 local on 15 July (04:30-05:30 UTC): half in off-peak (0.60), half in the morning peak (1.10). A mean over the
        // two instants, or the price at the start, would give 0.60 or 0.85 only by luck; the weights are what the hour was billed at.
        var tariff = G13();
        var from = new DateTimeOffset(2026, 7, 15, 4, 30, 0, TimeSpan.Zero);

        var mean = PeriodPrice.Mean(tariff, from, from.AddHours(1))!.Value;
        Assert.Equal(0.85, mean.PerKwh, 9);
        Assert.Equal((PriceBasis.Zone, null), (mean.Basis, mean.Zone));

        var skewed = PeriodPrice.Mean(tariff, from, from.AddMinutes(40))!.Value;
        Assert.Equal(((30 * 0.60) + (10 * 1.10)) / 40, skewed.PerKwh, 9);
    }

    [Fact]
    public void A_period_inside_one_zone_keeps_that_zone_and_a_flat_price_is_flat()
    {
        var inside = PeriodPrice.Mean(G13(), new DateTimeOffset(2026, 7, 15, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 15, 6, 0, 0, TimeSpan.Zero))!.Value;
        var flat = PeriodPrice.Mean(new Tariff(new FlatPrice(0.42), HolidayCalendar.Empty, Warsaw), new DateTimeOffset(2026, 7, 15, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 15, 6, 0, 0, TimeSpan.Zero))!.Value;

        Assert.Equal((1.10, PriceBasis.Zone, "morning_peak"), (inside.PerKwh, inside.Basis, inside.Zone));
        Assert.Equal((0.42, PriceBasis.Flat, null), (flat.PerKwh, flat.Basis, flat.Zone));
    }

    [Fact]
    public void An_empty_period_has_no_price()
    {
        var t = new DateTimeOffset(2026, 7, 15, 5, 0, 0, TimeSpan.Zero);

        Assert.Null(PeriodPrice.Mean(new Tariff(new FlatPrice(0.42), HolidayCalendar.Empty, Warsaw), t, t));
    }

    [Fact]
    public void A_period_across_the_repeated_hour_of_a_25_hour_day_weights_real_elapsed_time()
    {
        // 2026-10-25 in Warsaw has 02:xx twice, and a rule 02:00-03:00 matches both. Over 00:00Z-03:00Z (02:00 CEST to 03:00 CET)
        // two real hours are in the dear zone and one is not: weighting by wall-clock span would give 1.5 or 2.
        HoursRange.TryParse("02:00-03:00", out var night);
        var spec = new ZonedPrice(
            [new TariffZone("dear", 2.0, [new ZoneRule([], [], [night])]), new TariffZone("base", 1.0, [])],
            "base");
        var from = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

        var mean = PeriodPrice.Mean(new Tariff(spec, HolidayCalendar.Empty, Warsaw), from, from.AddHours(3))!.Value;

        Assert.Equal(5.0 / 3, mean.PerKwh, 9);
    }
}
