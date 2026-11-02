using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The cross-unit products the planner, the EV engine and the settlement use instead of a hand-written /1000. Why the bit-for-bit comparisons: the plan's
/// id hashes every double, so a product that evaluates in another order than the expression it replaced would move a plan by one ulp.
/// </summary>
public sealed class UnitProductsTests
{
    private static readonly TimeSpan[] Spans =
    [
        TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(7), TimeSpan.FromHours(1), TimeSpan.FromMinutes(90),
        TimeSpan.FromHours(23), TimeSpan.FromHours(24), TimeSpan.FromHours(25), TimeSpan.FromSeconds(1) / 3, TimeSpan.FromDays(7),
    ];

    private static IEnumerable<double> Powers()
    {
        var rng = new Random(20261102);
        return [0, 1, 230, 1500, 2000.5, 3680, 7360, 11040, .. Enumerable.Range(0, 2000).Select(_ => Math.Round(rng.NextDouble() * 22000, rng.Next(0, 4)))];
    }

    private static void SameBits(double expected, double actual) => Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    [Fact]
    public void Watts_over_a_duration_is_the_old_watts_times_hours_over_a_thousand_to_the_bit()
    {
        // Why: PlanReader, EvEngine, PvForecast and the minimum-run arithmetic wrote w * hours / 1000; the unit must keep that order.
        foreach (var w in Powers())
        {
            foreach (var span in Spans)
            {
                SameBits(w * span.TotalHours / 1000, (new Watts(w) * span).Value);
                SameBits(w * span.TotalHours / 1000, (span * new Watts(w)).Value);
            }
        }
    }

    [Fact]
    public void Kilowatts_over_a_duration_is_the_old_watts_over_a_thousand_times_hours_to_the_bit()
    {
        // Why: the capacity ledger, the deliverable energy and the verifier wrote w / 1000 * hours; the kilowatt view keeps that order.
        foreach (var w in Powers())
        {
            foreach (var span in Spans)
            {
                SameBits(w / 1000 * span.TotalHours, (new Watts(w).ToKilowatts() * span).Value);
                SameBits(w / 1000 * span.TotalHours, (span * new Watts(w).ToKilowatts()).Value);
            }
        }
    }

    [Fact]
    public void Kilowatts_back_to_watts_is_the_old_times_a_thousand_to_the_bit()
    {
        foreach (var w in Powers())
        {
            SameBits(w / 1000 * 1000, new Watts(w).ToKilowatts().ToWatts().Value);
        }
    }

    [Fact]
    public void Energy_over_power_is_the_old_hours_of_kwh_over_kw_to_the_bit()
    {
        // Why: the latest grid start and the predicted completion wrote TimeSpan.FromHours(kwh / kw).
        foreach (var w in Powers().Where(w => w > 0))
        {
            foreach (var kwh in new[] { 0.0, 0.001, 1.5, 7.3, 42, 99.999 })
            {
                Assert.Equal(TimeSpan.FromHours(kwh / (w / 1000)), new KilowattHours(kwh) / new Watts(w).ToKilowatts());
            }
        }
    }

    [Fact]
    public void A_zero_duration_carries_no_energy_and_a_zero_power_none_either()
    {
        Assert.Equal(KilowattHours.Zero, new Watts(11040) * TimeSpan.Zero);
        Assert.Equal(KilowattHours.Zero, new Watts(0) * TimeSpan.FromHours(25));
        Assert.Equal(KilowattHours.Zero, new Watts(11040).ToKilowatts() * TimeSpan.Zero);
    }

    [Fact]
    public void The_energy_of_a_day_is_the_energy_of_its_length_whatever_the_zone_made_it()
    {
        // Why: a 23- and a 25-hour day arrive as TimeSpans, so the unit needs no calendar knowledge; one kW held for the day is its hours in kWh.
        Assert.Equal(23, (new Watts(1000) * TimeSpan.FromHours(23)).Value, 12);
        Assert.Equal(25, (new Watts(1000) * TimeSpan.FromHours(25)).Value, 12);
        Assert.Equal(25, (new Watts(1000).ToKilowatts() * TimeSpan.FromHours(25)).Value, 12);
    }

    [Fact]
    public void A_negative_duration_gives_negative_energy_and_is_left_to_the_caller_to_refuse()
    {
        Assert.Equal(-0.5, (new Watts(2000) * TimeSpan.FromMinutes(-15)).Value, 12);
    }

    [Fact]
    public void A_price_times_energy_is_money_in_either_order_and_the_old_product_to_the_bit()
    {
        // Why: settlement and the plan's cost multiplied price and kWh by hand; the order of the factors cannot matter (IEEE multiplication commutes) and the unit
        // must say so.
        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var price = Math.Round((rng.NextDouble() * 2) - 0.2, 4);
            var kwh = rng.NextDouble() * 30;
            SameBits(kwh * price, (new PricePerKwh(price) * new KilowattHours(kwh)).Amount);
            SameBits(price * kwh, (new KilowattHours(kwh) * new PricePerKwh(price)).Amount);
        }

        Assert.Equal(Money.Zero, new PricePerKwh(0.8) * KilowattHours.Zero);
        Assert.Equal(new Money(-2), new PricePerKwh(-0.5) * new KilowattHours(4));
    }

    [Fact]
    public void Min_and_max_pick_the_smaller_and_the_larger_of_every_unit()
    {
        Assert.Equal(new Watts(1), Watts.Min(new Watts(1), new Watts(2)));
        Assert.Equal(new Watts(1), Watts.Min(new Watts(2), new Watts(1)));
        Assert.Equal(new KilowattHours(-1), KilowattHours.Min(new KilowattHours(-1), new KilowattHours(0)));
        Assert.Equal(new Amperes(6), Amperes.Min(new Amperes(16), new Amperes(6)));
        Assert.Equal(new Volts(230), Volts.Min(new Volts(231), new Volts(230)));
        Assert.Equal(new Percent(40), Percent.Min(new Percent(40), new Percent(90)));
        Assert.Equal(new Kilowatts(3), Kilowatts.Min(new Kilowatts(3), new Kilowatts(4)));
        Assert.Equal(new Kilowatts(4), Kilowatts.Max(new Kilowatts(3), new Kilowatts(4)));
        Assert.Equal(new PricePerKwh(0.2), PricePerKwh.Min(new PricePerKwh(0.2), new PricePerKwh(0.9)));
        Assert.Equal(new PricePerKwh(0.9), PricePerKwh.Max(new PricePerKwh(0.2), new PricePerKwh(0.9)));
        Assert.Equal(new Money(-3), Money.Min(new Money(-3), new Money(1)));
        Assert.Equal(new Money(1), Money.Max(new Money(-3), new Money(1)));
    }

    [Fact]
    public void Min_of_equal_values_is_that_value()
    {
        Assert.Equal(new Watts(5), Watts.Min(new Watts(5), new Watts(5)));
        Assert.Equal(new Money(0), Money.Min(Money.Zero, Money.Zero));
    }

    [Fact]
    public void Money_adds_subtracts_and_compares_only_with_money()
    {
        Assert.Equal(new Money(3), new Money(1) + new Money(2));
        Assert.Equal(new Money(-1), new Money(1) - new Money(2));
        Assert.True(new Money(1) < new Money(2));
        Assert.True(new Money(2) >= new Money(2));
    }

    [Fact]
    public void No_unit_converts_to_another_except_the_named_products_and_the_kilowatt_view()
    {
        // Why: CLAUDE.md, no operators between units in protection. The only operators with a foreign operand are the products named here; Amperes, Volts,
        // Percent and Money take none, and nothing divides by a Watts.
        var units = new[] { typeof(Watts), typeof(KilowattHours), typeof(Kilowatts), typeof(Amperes), typeof(Volts), typeof(Percent), typeof(PricePerKwh), typeof(Money) };
        var allowed = new HashSet<string>
        {
            "Watts*TimeSpan:KilowattHours", "TimeSpan*Watts:KilowattHours", "Kilowatts*TimeSpan:KilowattHours", "TimeSpan*Kilowatts:KilowattHours",
            "KilowattHours/Kilowatts:TimeSpan", "PricePerKwh*KilowattHours:Money", "KilowattHours*PricePerKwh:Money",
        };
        var found = new HashSet<string>();
        foreach (var unit in units)
        {
            foreach (var method in unit.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(m => m.Name.StartsWith("op_", StringComparison.Ordinal)))
            {
                var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
                if (method.Name is "op_Implicit" or "op_Explicit")
                {
                    Assert.Fail($"{unit.Name} converts implicitly or explicitly: {method}");
                }

                if (parameters.All(p => p == unit || p == typeof(double)) && (method.ReturnType == unit || method.ReturnType == typeof(bool)))
                {
                    continue;
                }

                var op = method.Name switch { "op_Multiply" => "*", "op_Division" => "/", _ => method.Name };
                found.Add($"{parameters[0].Name}{op}{parameters[1].Name}:{method.ReturnType.Name}");
            }
        }

        var unknown = found.Where(f => !allowed.Contains(f)).ToList();
        Assert.Empty(unknown);
    }
}
