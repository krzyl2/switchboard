using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// statistics.json schema 2 (ECO-11) carries the prices of each bucket. A file of schema 1 (every 0.9 install) must still load, with its
/// buckets reading as "no price data" and nothing back-filled; a newer or malformed file is set aside as before; and the bounds keep every
/// sum finite, so api/statistics can always serialise what the file holds.
/// </summary>
public sealed class StatisticsPricesFileTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Heater = DeviceName.Parse("heater_l1");
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    private StatisticsFile File(StatisticsOptions? options = null) => new(_directory, options ?? TestStatistics.Options(), NullLogger<StatisticsFile>.Instance);

    private static EnergyBucket Bucket(int index, BucketPrices? prices) => EnergyBucket.Empty(Start + (index * Five)) with
    {
        Observed = Five,
        GridImport = new KilowattHours(0.1),
        Devices = ImmutableDictionary<DeviceName, DeviceEnergy>.Empty.Add(Heater, new DeviceEnergy(new KilowattHours(0.125), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, 0.0625)),
        Prices = prices,
    };

    private static BucketPrices Prices(double import, PriceBasis basis, double export = 0.35) => new(
        PriceTrack.Zero.Held(new PricePoint(import, basis, null), Five),
        PriceTrack.Zero.Held(new PricePoint(export, PriceBasis.Flat, null), Five),
        new KilowattHours(0.05));

    private static StatisticsState State(params EnergyBucket[] buckets) =>
        new(Start.AddHours(1), Five, Start.AddHours(1), [.. buckets], LifetimeCounters.Zero);

    private JsonNode Written(StatisticsFile file) => JsonNode.Parse(System.IO.File.ReadAllText(file.FilePath))!;

    [Fact]
    public void The_prices_of_a_bucket_survive_a_restart_with_their_basis_and_weighting()
    {
        // Why: after a restart the savings of the retained hours must come back as they were, including which part of a bucket ran at a fallback.
        var file = File();
        file.Save(State(Bucket(0, Prices(0.90, PriceBasis.Live)), Bucket(1, Prices(0.60, PriceBasis.Fallback)), Bucket(2, null)));

        var loaded = file.Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        var buckets = loaded.State!.Buckets;
        Assert.Equal(0.90, buckets[0].Prices!.Import.Mean!.Value, 9);
        Assert.Equal(Five, buckets[0].Prices!.Import.Live);
        Assert.True(buckets[1].Prices!.Import.AnyFallback);
        Assert.Equal(0.05, buckets[1].Prices!.ControlledOffsettingImport.Value, 12);
        Assert.Null(buckets[2].Prices);
        Assert.Equal(0.0625, buckets[0].Devices[Heater].Value, 12);
        Assert.Equal(2, Written(file)["schema_version"]!.GetValue<int>());
    }

    [Fact]
    public void A_schema_1_file_loads_and_its_buckets_have_no_price_data()
    {
        // Why: every 0.9 install has such a file. Updating must neither set it aside (the history would be lost) nor invent prices for it
        // ("never back-filled"): the buckets read, their energy intact, and savings start later.
        var file = File();
        file.Save(State(Bucket(0, null), Bucket(1, null)));
        var node = Written(file);
        node["schema_version"] = 1;
        foreach (var bucket in node["buckets"]!.AsArray())
        {
            bucket!.AsObject().Remove("prices");
        }

        System.IO.File.WriteAllText(file.FilePath, node.ToJsonString());
        var loaded = file.Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Null(loaded.Warning);
        Assert.All(loaded.State!.Buckets, b => Assert.Null(b.Prices));
        Assert.Equal(0.1, loaded.State.Buckets[0].GridImport.Value, 12);
        Assert.Equal(0.125, loaded.State.Buckets[1].Devices[Heater].Measured.Value, 12);
        Assert.False(System.IO.File.Exists(file.FilePath + ".corrupt"));
    }

    [Fact]
    public void Row_statistics_file_with_an_older_schema_reads_with_no_price_data_and_upgrades_when_saved()
    {
        // Why: the savings of an upgraded install start empty ("since" later), not with zeros dressed as savings; and the next write is schema 2.
        var file = File();
        file.Save(State(Bucket(0, null)));
        var node = Written(file);
        node["schema_version"] = 1;
        node["buckets"]![0]!.AsObject().Remove("prices");
        System.IO.File.WriteAllText(file.FilePath, node.ToJsonString());
        var loaded = file.Load().State!;

        var snapshot = new StatisticsSnapshot(Start.AddHours(1), Five, false, [], loaded.Buckets.ToImmutableSortedDictionary(b => b.Start, b => b), loaded.Lifetime, loaded.IntegratedTo, 0);
        var periods = SavingsAccounting.Periods(snapshot, new NetBillingModel(new Watts(200), new Watts(-100), TimeSpan.FromSeconds(60)), TimeZoneInfo.Utc, Start.AddHours(1));
        file.Save(loaded);

        Assert.All(periods, p => Assert.Equal(PeriodStatus.Unpriced, p.Status));
        Assert.Null(SavingsAccounting.Since(periods));
        Assert.Equal(2, Written(file)["schema_version"]!.GetValue<int>());
        Assert.Equal(StateFileStatus.Loaded, file.Load().Status);
    }

    public static TheoryData<string, Action<JsonNode>> Damage() => new()
    {
        { "a schema 2 bucket without prices", n => n["buckets"]![0]!.AsObject().Remove("prices") },
        { "prices that are not an object", n => n["buckets"]![0]!["prices"] = 3 },
        { "an unknown key in prices", n => n["buckets"]![0]!["prices"]!["extra"] = 1 },
        { "an unknown key in a track", n => n["buckets"]![0]!["prices"]!["import"]!["extra"] = 1 },
        { "a negative price-weighted time", n => n["buckets"]![0]!["prices"]!["import"]!["price_seconds"] = -1 },
        { "a negative seconds at a basis", n => n["buckets"]![0]!["prices"]!["import"]!["live_s"] = -1 },
        { "more priced time than the bucket observed", n => n["buckets"]![0]!["prices"]!["import"]!["zone_s"] = 200 },
        { "a price no tariff reaches", n => n["buckets"]![0]!["prices"]!["export"]!["price_seconds"] = 3e9 },
        { "an offset that is negative", n => n["buckets"]![0]!["prices"]!["controlled_import_offset_kwh"] = -0.1 },
        { "an offset no bucket can hold", n => n["buckets"]![0]!["prices"]!["controlled_import_offset_kwh"] = 1e308 },
        { "a missing track", n => n["buckets"]![0]!["prices"]!.AsObject().Remove("export") },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void A_damaged_price_block_sets_the_file_aside_like_any_other_corruption(string why, Action<JsonNode> damage)
    {
        // Why: the file is untrusted; a price block out of shape must never reach the savings (or stop the start), so it is set aside as .corrupt.
        var file = File();
        file.Save(State(Bucket(0, Prices(0.90, PriceBasis.Live)), Bucket(1, null)));
        var node = Written(file);
        damage(node);
        System.IO.File.WriteAllText(file.FilePath, node.ToJsonString());

        var loaded = file.Load();

        Assert.True(loaded.Status == StateFileStatus.ResetCorrupt, why);
        Assert.Null(loaded.State);
        Assert.True(System.IO.File.Exists(file.FilePath + ".corrupt"), why);
    }

    [Fact]
    public void A_schema_1_bucket_that_carries_prices_is_not_a_schema_1_bucket()
    {
        // Why: the schemas are exact. A schema 1 file with prices in it was not written by this add-on and is not trusted.
        var file = File();
        file.Save(State(Bucket(0, Prices(0.90, PriceBasis.Live))));
        var node = Written(file);
        node["schema_version"] = 1;
        System.IO.File.WriteAllText(file.FilePath, node.ToJsonString());

        Assert.Equal(StateFileStatus.ResetCorrupt, file.Load().Status);
    }

    [Fact]
    public void Every_bucket_at_the_bounds_still_sums_to_finite_numbers_and_serialises()
    {
        // Why: the bounds exist so that api/statistics can always serialise what the file holds. Eight days of buckets with the largest price, the
        // largest energy and the largest device value that load must add up (range totals, price means, savings) without reaching Infinity.
        var options = TestStatistics.Options(retention: TimeSpan.FromDays(8), maxFileBytes: 4 * 1024 * 1024);
        var count = EnergyAccumulator.MaxBuckets(options) - 2;
        var maximum = new PriceTrack(StatisticsFile.MaxPricePerKwh * Five.TotalSeconds, Five, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        var full = new BucketPrices(maximum, maximum, new KilowattHours(StatisticsFile.MaxBucketKwh));
        var buckets = Enumerable.Range(0, count).Select(i => Bucket(i, full) with
        {
            GridImport = new KilowattHours(StatisticsFile.MaxBucketKwh),
            GridExport = new KilowattHours(StatisticsFile.MaxBucketKwh),
            Devices = ImmutableDictionary<DeviceName, DeviceEnergy>.Empty.Add(Heater, new DeviceEnergy(new KilowattHours(StatisticsFile.MaxBucketKwh), KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, StatisticsFile.MaxBucketValue)),
        });
        var state = new StatisticsState(Start.AddDays(9), Five, Start.AddDays(9), [.. buckets], LifetimeCounters.Zero);
        var file = File(options);
        file.Save(state);

        var loaded = file.Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(count, loaded.State!.Buckets.Length);
        var snapshot = new StatisticsSnapshot(Start.AddDays(9), Five, false, [], loaded.State!.Buckets.ToImmutableSortedDictionary(b => b.Start, b => b), LifetimeCounters.Zero, null, 0);
        var response = StatisticsEndpoint.Build(snapshot, PresentationFileReport.Fresh, Start.AddDays(8), null);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
        var prices = response.LastHour.Totals.Prices;
        Assert.True(double.IsFinite(prices!.Import!.PerKwh));
        var all = snapshot.Buckets.Values.Aggregate(EnergyBucket.Empty(Start), (sum, b) => sum.Add(b));
        Assert.True(double.IsFinite(all.Prices!.Import.PriceSeconds));
        Assert.True(double.IsFinite(all.Devices[Heater].Value));
    }
}
