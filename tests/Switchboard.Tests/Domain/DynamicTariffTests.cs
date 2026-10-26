using System.Collections.Immutable;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Domain;

/// <summary>
/// ECO-10 in the domain: a dynamic price is the signal's value plus <c>add(t)</c> while the signal is usable, else the static fallback, and
/// never the last known value. Pure, on explicit instants and an explicit zone, with the price inputs written out by hand.
/// </summary>
public sealed class DynamicTariffTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly SignalName Market = SignalName.Parse("market_price");

    private static readonly ZonedPrice DistributionZones = new(
        [
            new TariffZone("peak", 0.40, [new ZoneRule([DayClass.Workday], [], [new HoursRange(7 * 60, 13 * 60)])]),
            new TariffZone("offpeak", 0.10, []),
        ],
        "offpeak");

    private static readonly ZonedPrice FallbackZones = new(
        [
            new TariffZone("peak", 1.20, [new ZoneRule([DayClass.Workday], [], [new HoursRange(7 * 60, 13 * 60)])]),
            new TariffZone("offpeak", 0.70, []),
        ],
        "offpeak");

    // A workday: Wednesday 2026-07-15, local 08:30 (peak) and 14:00 (off-peak).
    private static readonly DateTimeOffset Peak = new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.FromHours(2)).ToUniversalTime();
    private static readonly DateTimeOffset Off = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.FromHours(2)).ToUniversalTime();
    private static readonly DateTimeOffset Reported = Peak - TimeSpan.FromMinutes(1);

    private static PriceInputs Inputs(SignalState state = SignalState.Fresh, double? value = 0.30, SignalDetail detail = SignalDetail.None) =>
        new(ImmutableDictionary<SignalName, PriceSample>.Empty.Add(Market, new PriceSample(state, detail, value, Reported)));

    private static Tariff Dynamic(PriceSpec? add, PriceSpec fallback) => new(new DynamicPrice(Market, fallback, add), HolidayCalendar.Empty, Warsaw);

    [Fact]
    public void A_fresh_signal_plus_the_add_of_the_moment_is_the_price_and_the_basis_is_live()
    {
        // Why: the price is what the market says plus the distribution charge of that moment: 0.30 + 0.40 in the peak, 0.30 + 0.10 off-peak.
        var tariff = Dynamic(DistributionZones, FallbackZones);

        Assert.Equal(new PricePoint(0.70, PriceBasis.Live, "peak"), tariff.PriceAt(Peak, Inputs()));
        Assert.Equal(new PricePoint(0.40, PriceBasis.Live, "offpeak"), tariff.PriceAt(Off, Inputs()));
    }

    [Fact]
    public void Without_an_add_the_price_is_the_signal_itself()
    {
        var tariff = Dynamic(null, new FlatPrice(0.9));

        Assert.Equal(new PricePoint(0.30, PriceBasis.Live, null), tariff.PriceAt(Peak, Inputs()));
    }

    [Theory]
    [InlineData(SignalState.Stale, SignalDetail.MaxAgeExceeded)]
    [InlineData(SignalState.Unavailable, SignalDetail.StateUnavailable)]
    [InlineData(SignalState.Unavailable, SignalDetail.StateUnknown)]
    [InlineData(SignalState.Rejected, SignalDetail.UnitMismatch)]
    public void A_signal_that_is_not_fresh_gives_the_fallback_even_when_a_last_value_is_still_attached(SignalState state, SignalDetail detail)
    {
        // Why: "never the last known value". Even a sample that still carries the number (here 0.30) is not used once its state is not fresh.
        var tariff = Dynamic(DistributionZones, FallbackZones);

        var price = tariff.PriceAt(Peak, Inputs(state, 0.30, detail));

        Assert.Equal(new PricePoint(1.20, PriceBasis.Fallback, "peak"), price);
    }

    [Fact]
    public void Without_inputs_or_without_the_signal_in_them_the_price_is_at_its_fallback()
    {
        var tariff = Dynamic(null, new FlatPrice(0.9));

        Assert.Equal(new PricePoint(0.9, PriceBasis.Fallback, null), tariff.PriceAt(Peak));
        Assert.Equal(new PricePoint(0.9, PriceBasis.Fallback, null), tariff.PriceAt(Peak, PriceInputs.Empty));
    }

    [Theory]
    [InlineData(-0.45)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_total_that_is_not_a_price_is_not_guessed_at_and_the_fallback_stands_in(double value)
    {
        // Why: a market may publish a negative value, and with an add of 0.10 the total (-0.35) is still not a price the rest of the system can
        // compare (the decision log refuses negative prices). It is not clamped to zero, which would be a guess: the fallback is the price.
        var tariff = Dynamic(new FlatPrice(0.10), new FlatPrice(0.9));

        Assert.Equal(new PricePoint(0.9, PriceBasis.Fallback, null), tariff.PriceAt(Peak, Inputs(value: value)));
    }

    [Fact]
    public void A_negative_signal_that_the_add_lifts_to_a_price_is_live()
    {
        // Why: -0.05 market with a 0.40 distribution charge is 0.35 and perfectly usable: the sum is what counts.
        var tariff = Dynamic(DistributionZones, FallbackZones);

        var price = tariff.PriceAt(Peak, Inputs(value: -0.05))!.Value;
        Assert.Equal((0.35, PriceBasis.Live, "peak"), (Math.Round(price.PerKwh, 9), price.Basis, price.Zone));
    }

    [Fact]
    public void The_zone_is_the_add_while_live_and_the_fallback_while_not()
    {
        // Why: sources name zones; a zone that no longer exists because the entity is down must not be claimed, and the zone of the fallback
        // (its own schedule) is the one in force while the fallback is.
        var tariff = Dynamic(DistributionZones, new ZonedPrice([new TariffZone("flat_zone", 0.9, [])], "flat_zone"));

        Assert.Equal("peak", tariff.ZoneAt(Peak, Inputs())!.Id);
        Assert.Equal("flat_zone", tariff.ZoneAt(Peak, Inputs(SignalState.Stale))!.Id);
        Assert.Equal("flat_zone", tariff.ZoneAt(Peak)!.Id);
    }

    [Fact]
    public void The_next_change_follows_the_schedule_in_force()
    {
        var tariff = Dynamic(DistributionZones, new FlatPrice(0.9));

        var live = tariff.NextChange(Peak, Inputs());
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 13, 0, 0, TimeSpan.FromHours(2)).ToUniversalTime(), live!.Value.At);
        Assert.Equal("offpeak", live.Value.Zone);
        Assert.Null(tariff.NextChange(Peak, Inputs(SignalState.Stale)));
    }

    [Fact]
    public void A_static_spec_ignores_the_inputs_entirely()
    {
        // Why: ECO-15. Prices handed to a flat or zoned tariff change nothing, so a file without a dynamic spec behaves as before.
        var flat = new Tariff(new FlatPrice(0.5), HolidayCalendar.Empty, Warsaw);
        var zoned = new Tariff(FallbackZones, HolidayCalendar.Empty, Warsaw);

        Assert.Equal(flat.PriceAt(Peak), flat.PriceAt(Peak, Inputs()));
        Assert.Equal(zoned.PriceAt(Peak), zoned.PriceAt(Peak, Inputs()));
        Assert.Equal(zoned.NextChange(Peak), zoned.NextChange(Peak, Inputs()));
    }

    [Fact]
    public void A_device_following_a_dynamic_price_is_worth_a_multiple_of_the_live_price_and_flagged_at_the_fallback()
    {
        // Why: value_follows with a dynamic price: factor times the live price (followed), and factor times the fallback with the basis fallback.
        var tariff = Dynamic(null, new FlatPrice(0.9));
        var spec = new ValueSpec(0.10, ValueFollows.ExportPrice, 2);

        var live = EffectiveValue.At(spec, new PriceBook(null, tariff.PriceAt(Peak, Inputs())));
        var fallback = EffectiveValue.At(spec, new PriceBook(null, tariff.PriceAt(Peak)));

        Assert.Equal(new EffectiveValue(0.60, ValueBasis.Followed), live);
        Assert.Equal(new EffectiveValue(1.8, ValueBasis.Fallback), fallback);
    }

    [Fact]
    public void The_domain_economics_module_never_converts_a_unit_or_names_an_entity()
    {
        // Why: the per-MWh conversion is the signal layer's alone (CLAUDE.md); the domain only ever sees currency per kWh.
        // Code only: a comment may say where the conversion happens.
        var source = string.Join('\n', Directory.GetFiles(Support.RepoPaths.Combine("src", "Switchboard.Domain", "Economics"), "*.cs")
            .SelectMany(File.ReadAllLines).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("MWh", source, StringComparison.Ordinal);
        Assert.DoesNotContain("1000", source, StringComparison.Ordinal);
    }
}
