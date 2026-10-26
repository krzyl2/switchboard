using Switchboard.Domain.Balance;
using Switchboard.Domain.Economics;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The value model (ECO-04) and the sources grammar. v_eff decides who gets energy first and the worth test decides whether
/// energy is wanted at its price, so a wrong basis or a boundary off by one margin either burns surplus that was worth more sold
/// or buys energy dearer than it is worth. A price that does not exist must fall back to the static value, never to a guess.
/// </summary>
public sealed class EffectiveValueTests
{
    private static readonly PriceBook Prices = new(
        new PricePoint(1.10, PriceBasis.Zone, "morning_peak"),
        new PricePoint(0.35, PriceBasis.Flat, null));

    private static readonly DateTimeOffset T = new(2026, 7, 15, 6, 30, 0, TimeSpan.Zero);

    private static readonly ISettlementModel Hourly = new HourlyNettingModel(new HourlyBalanceParameters(150, 525, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60)));

    [Fact]
    public void A_static_value_ignores_the_prices_and_the_factor()
    {
        var value = EffectiveValue.At(new ValueSpec(2.03, ValueFollows.Static, 7), Prices);

        Assert.Equal(new EffectiveValue(2.03, ValueBasis.Static), value);
    }

    [Fact]
    public void A_followed_import_price_is_the_factor_times_the_price()
    {
        // A heat pump with COP 3 fed from a 1.10 grid is worth 3.30 per kWh of heat.
        Assert.Equal(new EffectiveValue(3.3, ValueBasis.Followed).PerKwh, EffectiveValue.At(new ValueSpec(0.4, ValueFollows.ImportPrice, 3), Prices).PerKwh, 9);
        Assert.Equal(ValueBasis.Followed, EffectiveValue.At(new ValueSpec(0.4, ValueFollows.ImportPrice, 3), Prices).Basis);
    }

    [Fact]
    public void A_followed_export_price_is_the_factor_times_the_export_price()
    {
        var value = EffectiveValue.At(new ValueSpec(0.1, ValueFollows.ExportPrice, 1.5), Prices);

        Assert.Equal(0.525, value.PerKwh, 9);
        Assert.Equal(ValueBasis.Followed, value.Basis);
    }

    [Fact]
    public void A_followed_price_that_does_not_exist_falls_back_to_the_static_value()
    {
        // Why: the design forbids a last known value; with no price at this instant the configured number stands in and the log says so.
        var value = EffectiveValue.At(new ValueSpec(0.4, ValueFollows.ExportPrice, 3), new PriceBook(Prices.Import, null));
        var none = EffectiveValue.At(new ValueSpec(0.4, ValueFollows.ImportPrice, 3), PriceBook.None);

        Assert.Equal(new EffectiveValue(0.4, ValueBasis.Fallback), value);
        Assert.Equal(new EffectiveValue(0.4, ValueBasis.Fallback), none);
    }

    [Fact]
    public void Worth_holds_exactly_at_the_margin_and_fails_just_below()
    {
        // Heaters at 0.35 against an export price of 0.35 with margin 0: the example of the design is a tie that runs.
        var heater = new EffectiveValue(0.35, ValueBasis.Static);
        var cost = EnergyCost.Pv(Hourly, T, Prices)!.Value;

        Assert.True(ValueTest.Worth(heater, cost, 0));
        Assert.False(ValueTest.Worth(new EffectiveValue(0.349, ValueBasis.Static), cost, 0));
        Assert.True(ValueTest.Worth(new EffectiveValue(0.45, ValueBasis.Static), cost, 0.10));
        Assert.False(ValueTest.Worth(new EffectiveValue(0.449, ValueBasis.Static), cost, 0.10));
    }

    [Fact]
    public void Worth_is_not_fooled_by_floating_point_noise_in_the_difference()
    {
        // 0.7 - 0.2 is 0.49999999999999994 in binary floating point: a value exactly one margin above the cost must still be worth it.
        Assert.True(ValueTest.Worth(new EffectiveValue(0.7, ValueBasis.Static), new EnergyCost(EnergyClass.Zone, "a", 0.2), 0.5));
    }

    [Fact]
    public void A_dearer_zone_than_the_value_is_not_worth_buying()
    {
        // The car (2.03) may buy off-peak (0.60) but a heater (0.35) must not.
        var offpeak = EnergyCost.ZoneEnergy(new PriceBook(new PricePoint(0.60, PriceBasis.Zone, "offpeak"), null))!.Value;

        Assert.Equal((EnergyClass.Zone, "offpeak"), (offpeak.Class, offpeak.Zone));
        Assert.True(ValueTest.Worth(new EffectiveValue(2.03, ValueBasis.Static), offpeak, 0));
        Assert.False(ValueTest.Worth(new EffectiveValue(0.35, ValueBasis.Static), offpeak, 0));
    }

    [Fact]
    public void Costs_are_absent_when_the_price_is_absent()
    {
        // Why: no export price means the PV cost test is off (stated), no import price means a zone class cannot be evaluated; neither is a 0 cost.
        Assert.Null(EnergyCost.Pv(Hourly, T, PriceBook.None));
        Assert.Null(EnergyCost.ZoneEnergy(PriceBook.None));
        Assert.Equal(0.35, EnergyCost.Pv(Hourly, T, Prices)!.Value.PerKwh);
    }

    [Theory]
    [InlineData("pv_only", true, false, "pv_only")]
    [InlineData("any", true, true, "any")]
    [InlineData("pv_and_zone:offpeak", true, false, "pv_and_zone:offpeak")]
    [InlineData("pv_and_zone: weekend , offpeak", true, false, "pv_and_zone:offpeak,weekend")]
    [InlineData("  pv_only ", true, false, "pv_only")]
    public void Valid_sources_parse_and_print_canonically(string text, bool pv, bool allZones, string canonical)
    {
        Assert.True(SourceSet.TryParse(text, out var set, out var problem), problem);

        Assert.Equal((pv, allZones, canonical), (set!.Pv, set.AllZones, set.ToText()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pv")]
    [InlineData("PV_ONLY")]
    [InlineData("grid")]
    [InlineData("pv_and_zone:")]
    [InlineData("pv_and_zone:a,,b")]
    [InlineData("pv_and_zone:a b")]
    [InlineData("pv_and_zone:a,a")]
    [InlineData("zone:a")]
    public void Invalid_sources_are_refused_with_a_reason(string text)
    {
        Assert.False(SourceSet.TryParse(text, out var set, out var problem));

        Assert.Null(set);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void Narrowing_drops_unknown_zones_and_never_adds_one()
    {
        SourceSet.TryParse("pv_and_zone:ghost,offpeak", out var set, out _);

        var narrowed = set!.Narrow(new HashSet<string> { "offpeak", "morning_peak" }, out var dropped);

        Assert.Equal(["ghost"], dropped);
        Assert.Equal("pv_and_zone:offpeak", narrowed.ToText());
        Assert.Equal("pv_only", set.Narrow(new HashSet<string>(), out _).ToText());
        Assert.Equal("any", SourceSet.Any.Narrow(new HashSet<string>(), out var none).ToText());
        Assert.Empty(none);
    }

    [Fact]
    public void Allowed_zones_follow_the_set()
    {
        SourceSet.TryParse("pv_and_zone:offpeak", out var some, out _);

        Assert.True(some!.AllowsZone("offpeak"));
        Assert.False(some.AllowsZone("morning_peak"));
        Assert.False(SourceSet.PvOnly.AllowsZone("offpeak"));
        Assert.True(SourceSet.Any.AllowsZone("anything"));
    }

    [Fact]
    public void Sets_with_the_same_zones_in_any_written_order_are_equal()
    {
        // Why: rotation group members must have equal sources; "a,b" and "b,a" are the same sources.
        SourceSet.TryParse("pv_and_zone:b,a", out var one, out _);
        SourceSet.TryParse("pv_and_zone:a,b", out var two, out _);
        SourceSet.TryParse("pv_and_zone:a", out var three, out _);

        Assert.Equal(one, two);
        Assert.Equal(one!.GetHashCode(), two!.GetHashCode());
        Assert.NotEqual(one, three);
    }
}
