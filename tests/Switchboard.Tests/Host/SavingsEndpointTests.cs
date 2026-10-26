using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Switchboard.Application.Configuration;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// ECO-11 on the wire: GET api/economics carries the savings (today, an optional range asked for as api/statistics asks, per device, the
/// "since", the flags) and GET api/statistics carries the time-weighted prices. Without an economics section the savings are null and the
/// dashboard keeps saying they need a tariff.
/// </summary>
public sealed class SavingsEndpointTests
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DeviceName Heater = DeviceName.Parse("heater_l1");
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    private const string Economics = "economics:\n  currency: EUR\n  tariff: 1.10\n  settlement:\n    model: hourly_netting\n    export_price: 0.35\n";

    private static ConfigurationResult Config(string economics) => TestConfigurations.Parse(TestConfigurations.ThreePhase() + "\n" + economics);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static EnergyBucket Bucket(DateTimeOffset start, double export, double import, double heaterKwh, double value, PriceBasis basis = PriceBasis.Flat, bool prices = true) =>
        EnergyBucket.Empty(start) with
        {
            Observed = Quarter,
            GridExport = new KilowattHours(export),
            GridImport = new KilowattHours(import),
            Devices = ImmutableDictionary<DeviceName, DeviceEnergy>.Empty.Add(Heater, new DeviceEnergy(new KilowattHours(heaterKwh), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, value)),
            Prices = prices
                ? new BucketPrices(PriceTrack.Zero.Held(new PricePoint(1.10, basis, null), Quarter), PriceTrack.Zero.Held(new PricePoint(0.35, PriceBasis.Flat, null), Quarter), KilowattHours.Zero)
                : null,
        };

    /// <summary>An hour of four buckets: exports 3.0 kWh, the heater takes 1.5 kWh worth 0.75, so the hour saves 0.225 (see SavingsAccountingTests).</summary>
    private static IEnumerable<EnergyBucket> Hour(DateTimeOffset start, PriceBasis basis = PriceBasis.Flat, bool prices = true) =>
        Enumerable.Range(0, 4).Select(i => Bucket(start + (i * Quarter), 0.75, 0, 0.375, 0.1875, basis, prices));

    private static StatisticsSnapshot Snapshot(IEnumerable<EnergyBucket> buckets, DateTimeOffset now) =>
        new(now, Quarter, false, [], buckets.ToImmutableSortedDictionary(b => b.Start, b => b), LifetimeCounters.Zero, null, 0);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Savings_carry_the_basis_the_currency_label_verbatim_the_model_and_the_first_priced_hour()
    {
        // Why: the card shows the number with the user's own label and says what it is measured against; a symbol of ours, or a number with no basis
        // line, would be read as a bill. Since is where the accounting began: nothing before it is guessed.
        var now = Utc(2026, 7, 15, 13, 30);
        var buckets = Hour(Utc(2026, 7, 15, 10), prices: false).Concat(Hour(Utc(2026, 7, 15, 11))).Concat(Hour(Utc(2026, 7, 15, 12)));

        var savings = EconomicsEndpoint.Build(Config(Economics), now, Warsaw, null, Snapshot(buckets, now)).Savings!;

        Assert.Equal("against the controlled loads being off", savings.Basis);
        Assert.Equal("EUR", savings.Currency);
        Assert.Equal("hourly_netting", savings.Model);
        Assert.Equal(Utc(2026, 7, 15, 11), savings.Since);
        Assert.Equal(2, savings.Retained.Periods);
        Assert.Equal(2 * 0.225, savings.Retained.Savings, 9);
        Assert.Equal(0.75 * 2, savings.Retained.ValueGained, 9);
        Assert.Equal(0.525 * 2, savings.Retained.ExtraCost, 9);
        Assert.False(savings.Retained.Fallback);
    }

    [Fact]
    public void Today_is_the_local_day_not_the_utc_day()
    {
        // Why: 23:30 UTC on 15 July is 01:30 on the 16th in Warsaw. The hour at 21:00 UTC (23:00 local, the 15th) belongs to yesterday, and the
        // hour at 22:00 UTC (00:00 local on the 16th) to today, whatever the UTC date says.
        var now = Utc(2026, 7, 15, 23, 30);
        var buckets = Hour(Utc(2026, 7, 15, 21)).Concat(Hour(Utc(2026, 7, 15, 22)));

        var savings = EconomicsEndpoint.Build(Config(Economics), now, Warsaw, null, Snapshot(buckets, now)).Savings!;

        Assert.Equal(Utc(2026, 7, 15, 22), savings.Today.From);
        Assert.Equal(Utc(2026, 7, 16, 22), savings.Today.To);
        Assert.Equal(1, savings.Today.Periods);
        Assert.Equal(2, savings.Retained.Periods);
    }

    [Fact]
    public void The_25_hour_day_is_a_25_hour_day_with_25_hourly_periods()
    {
        // Why: 2026-10-25 in Warsaw runs from 22:00Z on the 24th to 23:00Z on the 25th. Today must span those 25 hours, and a range over the
        // day (asked for after it ended, so no hour is still running) must count each of them, the repeated 02:00 hour twice.
        var buckets = Enumerable.Range(0, 25).SelectMany(h => Hour(Utc(2026, 10, 24, 22).AddHours(h))).ToList();
        var noon = Utc(2026, 10, 25, 11);
        var after = Utc(2026, 10, 26, 6);
        var day = (Utc(2026, 10, 24, 22), Utc(2026, 10, 25, 23));

        var today = EconomicsEndpoint.Build(Config(Economics), noon, Warsaw, null, Snapshot(buckets, noon)).Savings!.Today;
        var range = EconomicsEndpoint.Build(Config(Economics), after, Warsaw, null, Snapshot(buckets, after), day).Savings!.Range!;

        Assert.Equal(day, (today.From, today.To));
        Assert.Equal(TimeSpan.FromHours(25), today.To - today.From);
        Assert.Equal(25, range.Periods);
        Assert.Equal(25 * 0.225, range.Savings, 9);
    }

    [Fact]
    public void The_23_hour_day_is_a_23_hour_day_with_23_hourly_periods()
    {
        // Why: 2026-03-29 in Warsaw runs from 23:00Z on the 28th to 22:00Z on the 29th: 23 hours, and 23 hourly periods, not 24.
        var buckets = Enumerable.Range(0, 23).SelectMany(h => Hour(Utc(2026, 3, 28, 23).AddHours(h))).ToList();
        var noon = Utc(2026, 3, 29, 11);
        var after = Utc(2026, 3, 30, 6);
        var day = (Utc(2026, 3, 28, 23), Utc(2026, 3, 29, 22));

        var today = EconomicsEndpoint.Build(Config(Economics), noon, Warsaw, null, Snapshot(buckets, noon)).Savings!.Today;
        var range = EconomicsEndpoint.Build(Config(Economics), after, Warsaw, null, Snapshot(buckets, after), day).Savings!.Range!;

        Assert.Equal(day, (today.From, today.To));
        Assert.Equal(TimeSpan.FromHours(23), today.To - today.From);
        Assert.Equal(23, range.Periods);
        Assert.Equal(23 * 0.225, range.Savings, 9);
    }

    [Fact]
    public void A_requested_range_sums_only_the_periods_that_start_in_it_and_is_absent_otherwise()
    {
        // Why: the range works as api/statistics' does, by period start; a period straddling nothing can fall on both sides of it.
        var now = Utc(2026, 7, 15, 14);
        var buckets = Enumerable.Range(10, 4).SelectMany(h => Hour(Utc(2026, 7, 15, h)));
        var config = Config(Economics);

        var without = EconomicsEndpoint.Build(config, now, Warsaw, null, Snapshot(buckets, now)).Savings!;
        var with = EconomicsEndpoint.Build(config, now, Warsaw, null, Snapshot(buckets, now), (Utc(2026, 7, 15, 11), Utc(2026, 7, 15, 13))).Savings!;

        Assert.Null(without.Range);
        Assert.Equal(2, with.Range!.Periods);
        Assert.Equal(2 * 0.225, with.Range.Savings, 9);
        Assert.Equal(4, with.Retained.Periods);
    }

    [Fact]
    public void The_devices_add_up_to_the_total_and_a_negative_device_is_shown()
    {
        // Why: per-device figures are shown beside the total. An hour exporting 3.0 with the heater (1.5 kWh worth 0.75) and the car (0.6 kWh worth only
        // 0.04, a mis-set value): the extra cost 2.1 x 0.35 = 0.735 is split by energy, the car carries 0.21 against 0.04 of value, and the negative
        // figure is shown rather than hidden.
        var now = Utc(2026, 7, 15, 11);
        var buckets = Enumerable.Range(0, 4).Select(i => Bucket(Utc(2026, 7, 15, 10) + (i * Quarter), 0.75, 0, 0.375, 0.1875) with
        {
            Devices = ImmutableDictionary<DeviceName, DeviceEnergy>.Empty
                .Add(Heater, new DeviceEnergy(new KilowattHours(0.375), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, 0.1875))
                .Add(Car, new DeviceEnergy(new KilowattHours(0.15), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, 0.01)),
        });

        var today = EconomicsEndpoint.Build(Config(Economics), now, Warsaw, null, Snapshot(buckets, now)).Savings!.Today;

        var heater = today.Devices.Single(d => d.Id == "heater_l1");
        var car = today.Devices.Single(d => d.Id == "ev_charger");
        Assert.Equal(today.Savings, today.Devices.Sum(d => d.Savings), 9);
        Assert.Equal(today.ExtraCost, today.Devices.Sum(d => d.ExtraCost), 9);
        Assert.True(car.Savings < 0, "a device whose value does not cover its share of the cost shows a negative figure");
        Assert.True(heater.Savings > car.Savings);
    }

    [Fact]
    public void A_price_at_fallback_flags_the_totals_and_the_running_hour_is_apart()
    {
        // Why: the flag must reach the user; and the hour in progress is a preview that is in no total.
        var now = Utc(2026, 7, 15, 12, 30);
        var buckets = Hour(Utc(2026, 7, 15, 10), PriceBasis.Fallback).Concat(Hour(Utc(2026, 7, 15, 11))).Concat(Hour(Utc(2026, 7, 15, 12)).Take(2));

        var savings = EconomicsEndpoint.Build(Config(Economics), now, Warsaw, null, Snapshot(buckets, now)).Savings!;

        Assert.True(savings.Retained.Fallback);
        Assert.Equal((2, 1), (savings.Retained.Periods, savings.Retained.FallbackPeriods));
        Assert.Equal(Utc(2026, 7, 15, 12), savings.Running!.Start);
        Assert.Equal(0.375 * 2 * 0.5, savings.Running.ValueGained, 9);
    }

    [Fact]
    public void Without_an_economics_section_the_savings_are_null_even_with_statistics()
    {
        // Why: the dashboard's "not available - needs a tariff" depends on this null; a zero here would be shown as "saved nothing".
        var now = Utc(2026, 7, 15, 11);
        var response = EconomicsEndpoint.Build(TestConfigurations.Parse(TestConfigurations.ThreePhase()), now, Warsaw, null, Snapshot(Hour(Utc(2026, 7, 15, 10)), now));

        Assert.False(response.Configured);
        Assert.Null(response.Savings);
        Assert.Equal(JsonValueKind.Null, Json(response).GetProperty("savings").ValueKind);
    }

    [Fact]
    public void The_savings_answer_has_exactly_the_documented_keys()
    {
        // Why: the contract the card of the next slice will parse; a renamed key would blank it.
        var now = Utc(2026, 7, 15, 12, 30);
        var buckets = Hour(Utc(2026, 7, 15, 11)).Concat(Hour(Utc(2026, 7, 15, 12)).Take(2));

        var savings = Json(EconomicsEndpoint.Build(Config(Economics), now, Warsaw, null, Snapshot(buckets, now), (Utc(2026, 7, 15, 0), Utc(2026, 7, 16, 0))).Savings!);

        Assert.Equal(["basis", "currency", "model", "range", "retained", "running", "since", "today"], Keys(savings));
        Assert.Equal(["devices", "extraCost", "fallback", "fallbackPeriods", "from", "incompletePeriods", "periods", "savings", "to", "unpricedPeriods", "valueGained"], Keys(savings.GetProperty("today")));
        Assert.Equal(["energyKwh", "extraCost", "id", "savings", "valueGained"], Keys(savings.GetProperty("today").GetProperty("devices")[0]));
        Assert.Equal(["devices", "end", "extraCost", "fallback", "savings", "start", "valueGained"], Keys(savings.GetProperty("running")));
    }

    [Fact]
    public void Statistics_totals_carry_the_time_weighted_prices_and_null_where_none_were_recorded()
    {
        // Why: api/statistics is where the prices of a bucket are visible. A price with its basis, as time-weighted means over priced time; and a
        // bucket with no price data says null, never zeros.
        var now = Utc(2026, 7, 15, 11);
        var buckets = new[] { Bucket(Utc(2026, 7, 15, 10), 0.75, 0, 0.375, 0.1875, PriceBasis.Fallback), Bucket(Utc(2026, 7, 15, 10, 15), 0.75, 0, 0.375, 0.1875, prices: false) };

        var response = StatisticsEndpoint.Build(Snapshot(buckets, now), PresentationFileReport.Fresh, now, null);

        var first = response.Buckets[0].Totals.Prices!;
        Assert.Equal(1.10, first.Import!.PerKwh, 9);
        Assert.Equal(["fallback"], first.Import.Bases);
        Assert.True(first.Import.Fallback);
        Assert.Equal(["flat"], first.Export!.Bases);
        Assert.False(first.Export.Fallback);
        Assert.Equal(900, first.Import.PricedS, 6);
        Assert.Null(response.Buckets[1].Totals.Prices);
        Assert.Equal(1.10, response.LastHour.Totals.Prices!.Import!.PerKwh, 9);
        Assert.Equal(900, response.LastHour.Totals.Prices!.Import!.PricedS, 6);
    }

    [Fact]
    public async Task Through_the_host_a_fresh_install_answers_savings_null_and_a_bad_range_is_refused()
    {
        // Why: the route must be wired with the statistics and the range, and refuse a range the way api/statistics does.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var ok = await PresentationEndpointTests.SendAsync(factory, "GET", "/api/economics");
        var bad = await PresentationEndpointTests.SendAsync(factory, "GET", "/api/economics?from=2026-07-15T00:00:00Z");

        Assert.Equal(StatusCodes.Status200OK, ok.Response.StatusCode);
        using var json = JsonDocument.Parse(ok.Response.Body);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("savings").ValueKind);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.Response.StatusCode);
    }

    private static string[] Keys(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
}
