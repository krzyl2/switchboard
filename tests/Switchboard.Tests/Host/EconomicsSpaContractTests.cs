using System.Collections.Immutable;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Modes;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The SPA reads GET api/economics strictly (web/src/api/economics.ts): a closed list that drifts from the backend's enum, or a key the
/// endpoint stopped writing, turns the tariff and savings cards "unavailable" instead of wrong. These tests hold the lists and the keys
/// of the committed sample to what the endpoint writes, and the device's energy class (UI-12) to the dispatch step it comes from.
/// </summary>
public sealed class EconomicsSpaContractTests
{
    private static readonly string Contract = RepoPaths.Combine("web", "src", "api", "economics.ts");
    private static readonly string SampleFile = RepoPaths.Combine("web", "src", "api", "economics.sample.json");
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private static readonly DeviceName Heater = DeviceName.Parse("heater_l1");
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    // A Monday afternoon, 13:38 in Warsaw after the clocks went back: the off-peak zone, the afternoon peak at 16:00.
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 38, 0, TimeSpan.Zero);

    private static readonly string Economics = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml")).Replace("currency: PLN", "currency: EUR", StringComparison.Ordinal);

    private const string Devices = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
            sources: pv_only
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
            value_follows: import_price
            value_factor: 3
            sources: pv_and_zone:offpeak
        """;

    private static ConfigurationResult Config() => TestConfigurations.Parse(TestDevices.Document(Devices, extra: Economics));

    private static DateTimeOffset Utc(int hour, int minute = 0) => new(2026, 10, 26, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A quarter that exported 0.75 kWh while the heater took 0.375 kWh worth 0.40 and the car 0.125 kWh worth only 0.01 (a device that costs more than it earns).</summary>
    private static EnergyBucket Bucket(DateTimeOffset start) =>
        EnergyBucket.Empty(start) with
        {
            Observed = Quarter,
            GridExport = new KilowattHours(0.75),
            Devices = ImmutableDictionary<DeviceName, DeviceEnergy>.Empty
                .Add(Heater, new DeviceEnergy(new KilowattHours(0.375), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, 0.40))
                .Add(Car, new DeviceEnergy(new KilowattHours(0.125), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, 0.01)),
            Prices = new BucketPrices(PriceTrack.Zero.Held(new PricePoint(0.60, PriceBasis.Zone, "offpeak"), Quarter), PriceTrack.Zero.Held(new PricePoint(0.35, PriceBasis.Flat, null), Quarter), KilowattHours.Zero),
        };

    private static StatisticsSnapshot Statistics()
    {
        // Three complete hours (09:00Z to 12:00Z) and the running one, 12:00Z to 12:30Z so far.
        var starts = Enumerable.Range(0, 12).Select(i => Utc(9) + (i * Quarter)).Concat([Utc(12), Utc(12, 15)]);
        return new(Now, Quarter, false, [], starts.ToImmutableSortedDictionary(s => s, Bucket), LifetimeCounters.Zero, null, 0);
    }

    private static DispatchStatus Dispatch() =>
        DispatchStatus.Initial(Now, ModeState.Resolve(OperatingMode.Shadow, controlAllowed: false, configurationValid: true, stateConfirmed: true)) with
        {
            Economics = new EconomicsView(
                "offpeak",
                new PricePoint(0.60, PriceBasis.Zone, "offpeak"),
                new PricePoint(0.35, PriceBasis.Flat, null),
                SettlementModelId.HourlyNetting,
                [
                    new DeviceEconomicsView(Heater, new EffectiveValue(0.35, ValueBasis.Static), 0.35, SourceSet.PvOnly, null),
                    new DeviceEconomicsView(Car, new EffectiveValue(1.80, ValueBasis.Followed), 1.80, new SourceSet(true, false, ["offpeak"]), EnergyClass.Zone),
                ]),
        };

    private static JsonElement Serialize(EconomicsResponse response) => JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static EconomicsResponse Full() =>
        EconomicsEndpoint.Build(Config(), Now, Warsaw, null, Statistics(), (Utc(9), Utc(12)), Dispatch());

    [Fact]
    public void Spa_closed_lists_match_the_backend_enums_exactly()
    {
        // Why: a basis, value basis, settlement model or class the SPA does not know would make the whole card "unavailable"; one it lists
        // but the backend never sends is dead text. Both directions are held.
        static string[] Names<T>() where T : struct, Enum => [.. Enum.GetValues<T>().Select(ApiText.Snake).Order(StringComparer.Ordinal)];
        static string[] Spa(string name) => [.. SpaContract.List(Contract, name).Order(StringComparer.Ordinal)];

        Assert.Equal(Names<PriceBasis>(), Spa("PRICE_BASES"));
        Assert.Equal(Names<ValueBasis>(), Spa("VALUE_BASES"));
        Assert.Equal(Names<ValueFollows>(), Spa("VALUE_FOLLOWS"));
        Assert.Equal(Names<SettlementModelId>(), Spa("SETTLEMENT_MODELS"));
        Assert.Equal([.. Names<EnergyClass>().Append("none").Order(StringComparer.Ordinal)], Spa("ENERGY_CLASSES"));
        Assert.Equal(["against the controlled loads being off"], SpaContract.List(Contract, "SAVINGS_BASES"));
        Assert.Equal(EconomicsEndpoint.SavingsBasis, SpaContract.List(Contract, "SAVINGS_BASES").Single());
        // The three shapes of a price spec the endpoint names (EconomicsEndpoint.Kind).
        Assert.Equal(["dynamic", "flat", "zoned"], Spa("PRICE_KINDS"));
    }

    [Fact]
    public void Spa_sample_has_exactly_the_keys_the_endpoint_writes()
    {
        var real = Serialize(Full());
        var sample = SpaContract.Sample(SampleFile, real, Now);

        Assert.Equal(SpaContract.Describe([real]), SpaContract.Describe([sample]));
        // The sample is the interesting case: configured, zoned, with the savings, the range and a running period, so every nested object is covered.
        Assert.True(sample.GetProperty("configured").GetBoolean());
        Assert.Equal("EUR", sample.GetProperty("currency").GetString());
        Assert.NotEqual(JsonValueKind.Null, sample.GetProperty("savings").GetProperty("range").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, sample.GetProperty("savings").GetProperty("running").ValueKind);
    }

    [Fact]
    public void A_device_reports_the_energy_class_of_the_last_dispatch_step()
    {
        // Why: "allowed now" on the device card is this class; a device the step gave no class is "none", not unknown, and before any step it is null.
        var response = Full();

        Assert.Equal(("none", "zone"), (response.Devices![0].Class, response.Devices[1].Class));
        Assert.All(EconomicsEndpoint.Build(Config(), Now, Warsaw).Devices!, d => Assert.Null(d.Class));
    }
}
