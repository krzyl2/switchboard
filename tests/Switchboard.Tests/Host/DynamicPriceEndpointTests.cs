using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Host.Composition;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// ECO-10 as the user sees it: <c>GET api/economics</c> says the live price now, its basis, the signal's age and whether the fallback is in force
/// (the first place to check that the entity is read in the right unit), and the diagnostics page (<c>GET api/signals</c>) lists the price
/// signal with its state, age and a unit that carries the user's own currency label.
/// </summary>
public sealed class DynamicPriceEndpointTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly SignalSourceSelection Live = new(SignalSourceKind.HomeAssistant, null, 1);
    private const string Entity = "sensor.rce_price";

    private const string Signal = "  market_price:\n    entity: sensor.rce_price\n    kind: price\n    unit: PLN/MWh\n    max_age: 5m\n    role: price_import\n";

    private const string Economics =
        "\neconomics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    fallback: 0.90\n    add: 0.40\n"
        + "  settlement:\n    model: hourly_netting\n    export_price:\n      signal: market_price\n      fallback: 0.30\n";

    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase(Signal) + Economics);

    private EconomicsResponse Economy() =>
        EconomicsEndpoint.Build(_store.Configuration, _store.Now, Warsaw, _store.Snapshot().Prices);

    [Fact]
    public void A_live_price_shows_its_value_basis_and_the_signal_with_its_age()
    {
        _store.Push(Entity, "450");
        _store.Advance(TimeSpan.FromSeconds(42));

        var tariff = Economy().Tariff!;

        Assert.Equal(("dynamic", 0.85, "live", false), (tariff.Kind, Math.Round(tariff.ImportPrice!.Value, 9), tariff.PriceBasis, tariff.FallbackInForce));
        Assert.Equal(new PriceSignalDto("market_price", "fresh", "none", 0.45, 42), tariff.Signal);
        Assert.Equal(("dynamic", 0.45, "live", false), (Economy().Settlement!.ExportPrice!.Kind, Economy().Settlement!.ExportPrice!.PerKwh, Economy().Settlement!.ExportPrice!.PriceBasis, Economy().Settlement!.ExportPrice!.FallbackInForce));
    }

    [Fact]
    public void A_stale_price_shows_the_fallback_in_force_and_why_never_the_last_value()
    {
        _store.Push(Entity, "450");
        _store.Advance(TimeSpan.FromMinutes(5));

        var tariff = Economy().Tariff!;

        Assert.Equal((0.90, "fallback", true), (tariff.ImportPrice, tariff.PriceBasis, tariff.FallbackInForce));
        Assert.Equal(("stale", "max_age_exceeded", (double?)null), (tariff.Signal!.State, tariff.Signal.Detail, tariff.Signal.PerKwh));
        Assert.Equal(300, tariff.Signal.AgeS);
    }

    [Fact]
    public void A_price_never_reported_is_at_its_fallback_and_the_signal_is_unavailable()
    {
        var tariff = Economy().Tariff!;

        Assert.Equal((0.90, "fallback", true, "unavailable"), (tariff.ImportPrice, tariff.PriceBasis, tariff.FallbackInForce, tariff.Signal!.State));
        Assert.Null(tariff.Signal.AgeS);
    }

    [Fact]
    public void A_static_tariff_has_no_signal_and_is_never_in_fallback()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase() + "\neconomics:\n  currency: PLN\n  tariff: 0.5\n");

        var tariff = EconomicsEndpoint.Build(store.Configuration, store.Now, Warsaw, store.Snapshot().Prices).Tariff!;

        Assert.Equal(("flat", null, false), (tariff.Kind, tariff.Signal, tariff.FallbackInForce));
    }

    [Fact]
    public void The_devices_follow_the_live_price_in_the_api_as_in_dispatch()
    {
        var devices = "\ndevices:\n  - id: heater\n    type: onoff\n    actuator: switch.heater\n    phase: L1\n    rated_current_a: 6\n    rated_power_w: 1300\n    value_per_kwh: 0.1\n    value_follows: export_price\n    value_factor: 2\n";
        var store = new StoreHarness(TestConfigurations.ThreePhase(Signal) + Economics + devices);
        store.Push(Entity, "200");

        var live = EconomicsEndpoint.Build(store.Configuration, store.Now, Warsaw, store.Snapshot().Prices).Devices!.Single();
        store.Advance(TimeSpan.FromMinutes(6));
        var fallback = EconomicsEndpoint.Build(store.Configuration, store.Now, Warsaw, store.Snapshot().Prices).Devices!.Single();

        Assert.Equal((0.4, "followed"), (live.VEff, live.ValueBasis));
        Assert.Equal((0.6, "fallback"), (fallback.VEff, fallback.ValueBasis));
    }

    [Fact]
    public void The_diagnostics_list_the_price_signal_with_its_age_and_the_users_currency_in_the_unit()
    {
        _store.Push(Entity, "450");
        _store.Advance(TimeSpan.FromSeconds(30));
        var configuration = _store.Configuration.Configuration!;
        var diagnostics = new SignalDiagnostics(
            _store.Snapshot(),
            _store.Configuration,
            configuration.Signals.ToDictionary(s => s.Name, s => s.Entity).ToImmutableDictionary(),
            []);

        var signal = SignalsEndpoint.Build(diagnostics, _store.Now, Live, TestConfigurations.Path).Signals.Single(s => s.Name == "market_price");

        Assert.Equal(("price", "price_import", "fresh", 0.45, "PLN/kWh", 30), (signal.Kind, signal.Role, signal.State, signal.Value, signal.Unit, (int)signal.AgeS!.Value));
        Assert.Equal(new RawDto("450", "PLN/MWh"), signal.Raw);
    }

    [Fact]
    public void The_diagnostics_show_a_rejected_price_with_no_value_and_no_unit()
    {
        _store.Push(Entity, "450", unit: "EUR/MWh");
        var configuration = _store.Configuration.Configuration!;
        var diagnostics = new SignalDiagnostics(_store.Snapshot(), _store.Configuration, configuration.Signals.ToDictionary(s => s.Name, s => s.Entity).ToImmutableDictionary(), []);

        var signal = SignalsEndpoint.Build(diagnostics, _store.Now, Live, TestConfigurations.Path).Signals.Single(s => s.Name == "market_price");

        Assert.Equal(("rejected", "unit_mismatch", null, null), (signal.State, signal.Detail, signal.Value, signal.Unit));
    }
}
