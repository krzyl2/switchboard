using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// ECO-10 in the signal layer: a price entity becomes currency per kWh here and nowhere else, only while it is fresh (from
/// <c>last_reported</c> and <c>max_age</c>, SIG-05, SIG-06), and only in the user's own currency. Anything else is simply not a price:
/// no value reaches dispatch, which then uses the fallback. Prices travel apart from protection (SIG-04 by type).
/// </summary>
public sealed class PriceSignalTests
{
    private const string Entity = "sensor.rce_price";

    private static string Signals(string unit = "PLN/MWh", string role = "price_import", string maxAge = "5m") => $"""
          market_price:
            entity: {Entity}
            kind: price
            unit: {unit}
            max_age: {maxAge}
            role: {role}

        """;

    private const string Economics = "\neconomics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    fallback: 0.9\n";

    private static StoreHarness Store(string unit = "PLN/MWh", string role = "price_import") => new(TestConfigurations.ThreePhase(Signals(unit, role)) + Economics);

    private static SignalDefinition Definition(string unit, string? currency = "PLN") =>
        new(SignalName.Parse("market_price"), EntityId.Parse(Entity), SignalKind.Price, unit, null, null, false, TimeSpan.FromMinutes(5), null, SignalRole.PriceImport, null, false) { PriceCurrency = currency };

    [Theory]
    [InlineData("PLN/MWh", "450", 0.45)]
    [InlineData("PLN/MWh", "1234.5", 1.2345)]
    [InlineData("PLN/MWh", "-12", -0.012)]
    [InlineData("PLN/kWh", "0.45", 0.45)]
    public void A_price_is_per_kwh_after_the_signal_layer_and_a_per_mwh_one_is_converted_exactly(string unit, string state, double expected)
    {
        // Why: 450 PLN/MWh is 0.45 per kWh, exactly (a division, not a multiplication by an inexact 0.001), because the value is
        // compared with other prices to the grosz. A market may publish a negative price: it is a value, not an error.
        var result = SignalNormalizer.Normalize(Definition(unit), state, unit);

        Assert.Equal(NormalizationOutcome.Accepted, result.Outcome);
        Assert.Equal(new PriceValue(expected), result.Value);
    }

    [Theory]
    [InlineData("PLN/MWh", "PLN/kWh")]
    [InlineData("PLN/kWh", "PLN/MWh")]
    [InlineData("PLN/MWh", "EUR/MWh")]
    [InlineData("PLN/MWh", null)]
    public void A_unit_that_differs_from_the_declaration_is_refused_not_converted(string declared, string? reported)
    {
        // Why: a per-MWh number read as per kWh is a 1000x error, and a currency the user did not declare is not theirs.
        var result = SignalNormalizer.Normalize(Definition(declared), "450", reported);

        Assert.Equal((NormalizationOutcome.Rejected, SignalDetail.UnitMismatch), (result.Outcome, result.Detail));
    }

    [Theory]
    [InlineData("EUR/MWh", "PLN")]
    [InlineData("EUR/kWh", "PLN")]
    [InlineData("PLN/MWh", null)]
    public void A_currency_label_that_is_not_the_configured_currency_makes_the_signal_unusable(string unit, string? currency)
    {
        // Why: the unit is declared and reported consistently, yet it is not economics.currency (or there is no economics section): refused.
        var result = SignalNormalizer.Normalize(Definition(unit, currency), "450", unit);

        Assert.Equal((NormalizationOutcome.Rejected, SignalDetail.UnitMismatch), (result.Outcome, result.Detail));
    }

    [Theory]
    [InlineData("unavailable", SignalDetail.StateUnavailable)]
    [InlineData("unknown", SignalDetail.StateUnknown)]
    public void Unavailable_and_unknown_are_no_price(string state, SignalDetail detail)
    {
        var result = SignalNormalizer.Normalize(Definition("PLN/MWh"), state, "PLN/MWh");

        Assert.Equal((NormalizationOutcome.Unavailable, null, detail), (result.Outcome, result.Value, result.Detail));
    }

    [Theory]
    [InlineData("cheap")]
    [InlineData("NaN")]
    [InlineData("1e999")]
    public void A_price_that_is_not_a_finite_number_is_never_read_as_zero(string state)
    {
        var result = SignalNormalizer.Normalize(Definition("PLN/MWh"), state, "PLN/MWh");

        Assert.Equal(NormalizationOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void A_fresh_price_reaches_dispatch_in_the_price_inputs_in_kwh_with_its_report_time()
    {
        var store = Store();
        store.Push(Entity, "450");

        var sample = store.Snapshot().Prices!.Sample(SignalName.Parse("market_price"))!.Value;

        Assert.True(sample.Usable);
        Assert.Equal((0.45, store.Now), (sample.PerKwh, sample.ReportedAt));
    }

    [Fact]
    public void The_price_goes_stale_at_max_age_and_then_carries_no_value()
    {
        // Why: SIG-05. At exactly max_age the signal is stale, and a stale price offers no number at all: dispatch cannot use the last
        // known value because it is not there to use.
        var store = Store();
        store.Push(Entity, "450");
        store.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.True(store.Snapshot().Prices!.Sample(SignalName.Parse("market_price"))!.Value.Usable);

        store.Advance(TimeSpan.FromSeconds(1));

        var sample = store.Snapshot().Prices!.Sample(SignalName.Parse("market_price"))!.Value;
        Assert.Equal((SignalState.Stale, SignalDetail.MaxAgeExceeded, (double?)null), (sample.State, sample.Detail, sample.PerKwh));
        Assert.Null(store.Snapshot().Prices!.Usable(SignalName.Parse("market_price")));
    }

    [Fact]
    public void A_repeated_state_with_a_newer_last_reported_keeps_the_price_fresh()
    {
        // Why: SIG-06. A price that does not change for an hour is reported again only as a newer last_reported; that is what keeps it live.
        var store = Store();
        store.Push(Entity, "450");
        var firstUpdate = store.Now;
        store.Advance(TimeSpan.FromMinutes(4));

        store.ProbeAnswer(Entity, "450", firstUpdate, store.Now);
        store.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(0.45, store.Snapshot().Prices!.Usable(SignalName.Parse("market_price")));
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("unknown")]
    public void An_unavailable_or_unknown_entity_carries_no_value(string state)
    {
        var store = Store();
        store.Push(Entity, "450");
        store.Advance(TimeSpan.FromSeconds(30));

        store.Push(Entity, state);

        var sample = store.Snapshot().Prices!.Sample(SignalName.Parse("market_price"))!.Value;
        Assert.Equal((SignalState.Unavailable, (double?)null), (sample.State, sample.PerKwh));
    }

    [Fact]
    public void A_price_reported_in_another_unit_than_declared_is_rejected_and_shows_so_in_the_diagnostics()
    {
        var store = Store();

        store.Push(Entity, "450", unit: "PLN/kWh");

        var reading = store.Reading("market_price");
        Assert.Equal((SignalState.Rejected, SignalDetail.UnitMismatch, "PLN/kWh"), (reading.State, reading.Detail, reading.RawUnit));
        Assert.Null(store.Snapshot().Prices!.Usable(SignalName.Parse("market_price")));
    }

    [Fact]
    public void A_signal_in_a_foreign_currency_is_rejected_from_the_first_sample()
    {
        var store = Store(unit: "EUR/MWh");

        store.Push(Entity, "450");

        Assert.Equal((SignalState.Rejected, SignalDetail.UnitMismatch), (store.Reading("market_price").State, store.Reading("market_price").Detail));
        Assert.Contains(store.Configuration.Warnings, w => w.Code == ConfigurationIssueCodes.UnitMismatch && w.Path == "signals.market_price.unit");
    }

    [Fact]
    public void There_are_no_price_inputs_when_no_price_signal_is_mapped()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase());

        Assert.Null(store.Snapshot().Prices);
    }

    [Fact]
    public void Prices_travel_beside_the_dispatch_inputs_and_never_inside_the_protection_inputs()
    {
        // Why: SIG-04 by type. A price view reachable from ProtectionInputs, DispatchInputs or the guard's step would be the first step towards a price
        // moving a limit: the control inputs carry it as a field of their own, and the snapshot of the store cycle as one of its own.
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(ProtectionInputs)), t => t == typeof(PriceInputs) || t == typeof(PriceSample));
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(DispatchInputs)), t => t == typeof(PriceInputs) || t == typeof(PriceSample));
        Assert.Contains(typeof(ControlInputs).GetProperties(), p => p.PropertyType == typeof(PriceInputs));
    }

    [Fact]
    public void A_price_signal_is_never_a_protection_or_dispatch_measurement()
    {
        // Why: a price must not turn up as a current, a power or a device state anywhere the guard or dispatch read measurements.
        var store = Store();
        store.Push(Entity, "450");
        var snapshot = store.Snapshot();

        Assert.All(snapshot.Protection.Phases, p => Assert.NotEqual(0.45, p.Current?.Signed.Value));
        Assert.Equal(SignalKind.Price, store.Reading("market_price").Kind);
    }
}
