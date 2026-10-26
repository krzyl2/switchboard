using Switchboard.Domain.Balance;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The device-off counterfactual of a settlement period (ECO-11, 14-ECONOMICS, Savings): <c>extra_cost = Settle(b + E) - Settle(b)</c>.
/// Every figure below is computed by hand from the prices (export 0.35, import 1.10) and written next to the assertion, so a change of
/// sign, of model or of the netting rule is a failing test and not a quietly different saving.
/// </summary>
public sealed class CounterfactualTests
{
    private static readonly HourlyNettingModel Hourly = new(new HourlyBalanceParameters(150, 525, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60)));
    private static readonly NetBillingModel Billing = new(new Watts(200), new Watts(-100), TimeSpan.FromSeconds(60));

    private static readonly PriceBook Prices = new(
        new PricePoint(1.10, PriceBasis.Zone, "morning_peak"),
        new PricePoint(0.35, PriceBasis.Flat, null));

    private static PeriodEnergy Energy(double exported, double imported, double controlled, double offsettingImport = 0) =>
        new(new KilowattHours(exported), new KilowattHours(imported), new KilowattHours(controlled), new KilowattHours(offsettingImport));

    [Fact]
    public void An_hour_that_ends_in_net_export_costs_the_loads_the_export_price()
    {
        // Why: the point of the counterfactual. b = +3, E = 1.5: without the loads the hour would have exported 4.5 kWh instead of 3,
        // so the loads cost 1.5 kWh at the export price, 0.525, whatever instant each kWh was drawn at.
        var extra = Counterfactual.ExtraCost(Hourly, Energy(exported: 3, imported: 0, controlled: 1.5), Prices)!.Value;

        Assert.Equal(1.5 * 0.35, extra.Amount, 12);
    }

    [Fact]
    public void An_hour_that_ends_in_net_import_costs_the_loads_the_import_price()
    {
        // Why: b = -4, E = 1.5: without the loads the hour would have imported 2.5 instead of 4, so the loads cost 1.5 kWh at the
        // import price, 1.65. Valuing the same kWh at the export price would understate the cost by two thirds.
        var extra = Counterfactual.ExtraCost(Hourly, Energy(exported: 0, imported: 4, controlled: 1.5, offsettingImport: 1.5), Prices)!.Value;

        Assert.Equal(1.5 * 1.10, extra.Amount, 12);
    }

    [Fact]
    public void A_mixed_hour_splits_the_loads_between_the_two_prices_where_the_balance_crosses_zero()
    {
        // Why: b = -0.5, E = 1.5, so b + E = +1.0. Settle(1.0) - Settle(-0.5) = 0.35 - (-0.55) = 0.90 = 0.5 kWh of avoided import at 1.10
        // plus 1.0 kWh of added export at 0.35. A model that priced all of E at one price would give 0.525 or 1.65.
        var extra = Counterfactual.ExtraCost(Hourly, Energy(exported: 0.5, imported: 1.0, controlled: 1.5), Prices)!.Value;

        Assert.Equal((0.5 * 1.10) + (1.0 * 0.35), extra.Amount, 12);
    }

    [Fact]
    public void No_controlled_energy_costs_nothing_and_needs_no_price()
    {
        // Why: an hour in which no load ran must not carry a cost, and must not become "unpriced" for want of a price it does not need.
        Assert.Equal(0, Counterfactual.ExtraCost(Hourly, Energy(2, 1, 0), Prices)!.Value.Amount);
        Assert.Equal(0, Counterfactual.ExtraCost(Billing, Energy(2, 1, 0), Prices)!.Value.Amount);
        Assert.Equal(0, Counterfactual.ExtraCost(Hourly, Energy(0, 0, 0), PriceBook.None)!.Value.Amount);
    }

    [Fact]
    public void Net_billing_pays_the_import_it_avoided_and_the_export_it_forwent_apart()
    {
        // Why: exported 2, imported 1, E = 1.5 of which 1.0 ran while importing. Without the loads: import 0, export 2.5, so Settle goes from
        // 0.7 - 1.1 = -0.4 to 0.875, a difference of 1.275 = 1.0 kWh of import at 1.10 plus 0.5 kWh of export at 0.35. Under hourly netting
        // the same flows cost only 0.525 (the hour nets to +1, the loads cost 1.5 at the export price): the models must differ here.
        var energy = Energy(exported: 2, imported: 1, controlled: 1.5, offsettingImport: 1.0);

        Assert.Equal((1.0 * 1.10) + (0.5 * 0.35), Counterfactual.ExtraCost(Billing, energy, Prices)!.Value.Amount, 12);
        Assert.Equal(1.5 * 0.35, Counterfactual.ExtraCost(Hourly, energy, Prices)!.Value.Amount, 12);
    }

    [Fact]
    public void A_price_the_flows_need_and_do_not_have_makes_the_period_unknown_not_free()
    {
        // Why: a period without a known price is not counted (nothing is guessed); returning 0 would count it with its full value as a gain.
        Assert.Null(Counterfactual.ExtraCost(Hourly, Energy(3, 0, 1.5), new PriceBook(Prices.Import, null)));
        Assert.Null(Counterfactual.ExtraCost(Hourly, Energy(0, 4, 1.5), new PriceBook(null, Prices.Export)));
        Assert.Null(Counterfactual.ExtraCost(Billing, Energy(2, 1, 1.5, 1.0), PriceBook.None));
    }

    [Theory]
    [InlineData(0.35, 1.10)]
    [InlineData(1.10, 0.35)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.9, 0.9)]
    public void The_extra_cost_is_never_negative_for_any_flows_and_any_prices_that_are_not_negative(double export, double import)
    {
        // Why: the design says extra_cost >= 0 (the loads can only have cost income or added import). A negative one would turn the loads
        // into a source of money in the savings, and the sign of the counterfactual is the easiest thing to get wrong.
        var prices = new PriceBook(new PricePoint(import, PriceBasis.Flat, null), new PricePoint(export, PriceBasis.Flat, null));
        var random = new Random(26_10_2026);
        for (var i = 0; i < 4000; i++)
        {
            var controlled = random.NextDouble() < 0.1 ? 0 : random.NextDouble() * 6;
            var imported = random.NextDouble() < 0.4 ? 0 : random.NextDouble() * 5;
            var exported = random.NextDouble() < 0.4 ? 0 : random.NextDouble() * 5;
            var offset = Math.Min(controlled, imported) * random.NextDouble();
            var energy = Energy(exported, imported, controlled, offset);

            foreach (ISettlementModel model in new ISettlementModel[] { Hourly, Billing })
            {
                var extra = Counterfactual.ExtraCost(model, energy, prices)!.Value.Amount;
                Assert.True(extra >= 0, $"{model.Id}: extra cost {extra} for {energy}");
            }
        }
    }

    [Fact]
    public void The_shares_of_the_devices_add_up_to_the_extra_cost()
    {
        // Why: the per-device view must not invent or lose money: 0.9 split over 1.0 / 0.5 kWh is 0.6 and 0.3, and a device with no energy carries none.
        var shares = new[] { (1.0, 0.0), (0.5, 0.0), (0.0, 0.0) }.Select(d => Counterfactual.Share(0.9, d.Item1, 1.5)).ToList();

        Assert.Equal([0.6, 0.3, 0.0], shares.Select(s => Math.Round(s, 12)));
        Assert.Equal(0.9, shares.Sum(), 12);
        Assert.Equal(0, Counterfactual.Share(0.9, 1.0, 0));
    }
}
