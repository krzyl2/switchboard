using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;
using static Switchboard.Infrastructure.Persistence.PresentationJson;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/statistics.json</c>: the runtime UI's energy buckets and counters (schema 1). Untrusted
/// on read: another schema or bucket length, a misaligned, repeated or out-of-order bucket, a
/// bucket after the high-water mark, a non-finite or negative number, more observed and unobserved
/// time than a bucket holds, a lifetime longer than a century, a bad device key, more devices or
/// buckets than the bounds, or a file above MaxFileBytes sets it aside. A downgrade to 0.4.x ignores the file.
/// </summary>
public class StatisticsFile : PresentationFile<StatisticsState>
{
    public const string Name = "statistics.json";
    public const int SchemaVersion = 1;

    // Bound on untrusted content, like state.json's: far above what a valid configuration declares (64 devices).
    internal const int MaxDevices = 256;

    // Bound on untrusted content: a century of voltage measurement. Far below TimeSpan's range, so
    // reading it cannot overflow and the counter can keep adding for as long as the add-on runs.
    internal static readonly double MaxLifetimeSeconds = TimeSpan.FromDays(36_525).TotalSeconds;

    // Bounds on untrusted content: 1000 kWh in one bucket is far beyond any household connection,
    // and the value bound leaves room for value_per_kwh in any currency unit; the sum of every
    // retained bucket stays finite, so GET api/statistics can always serialise it.
    internal const double MaxBucketKwh = 1_000;
    internal const double MaxBucketValue = 1e12;

    // Seconds written as doubles do not round-trip every tick; a bucket may be over by this much.
    private const double SecondsTolerance = 1e-3;

    // A trimmed file aims a little below the cap, so one pass usually suffices.
    private const double TrimMargin = 0.95;

    private static readonly string[] RootKeys = ["schema_version", "saved_at", "bucket_minutes", "integrated_to", "buckets", "lifetime"];
    private static readonly string[] BucketKeys =
    [
        "start", "observed_s", "unobserved_s", "grid_import_kwh", "grid_export_kwh", "generation_kwh", "load_kwh", "uncontrolled_kwh",
        "storage_charge_kwh", "storage_discharge_kwh", "pv", "devices", "sheds", "would_sheds", "voltage_losses", "supply_outages", "voltage_observed_s",
    ];

    private static readonly string[] PvKeys = ["house_kwh", "storage_kwh", "export_kwh", "unattributed_kwh"];
    private static readonly string[] DeviceKeys = ["measured_kwh", "model_kwh", "unknown_s", "pv_kwh", "value"];
    private static readonly string[] LifetimeKeys = ["since", "sheds", "would_sheds", "voltage_losses", "supply_outages", "voltage_observed_s"];

    private readonly StatisticsOptions _options;

    public StatisticsFile(string dataDirectory, StatisticsOptions options, ILogger<StatisticsFile> logger)
        : base(dataDirectory, Name, (options ?? throw new ArgumentNullException(nameof(options))).MaxFileBytes, logger)
    {
        _options = options;
    }

    protected override byte[] Serialize(StatisticsState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            WriteTime(writer, "saved_at", state.SavedAt);
            writer.WriteNumber("bucket_minutes", state.BucketLength.TotalMinutes);
            WriteTime(writer, "integrated_to", state.IntegratedTo);
            writer.WriteStartArray("buckets");
            foreach (var bucket in state.Buckets)
            {
                writer.WriteStartObject();
                writer.WriteString("start", Time(bucket.Start));
                writer.WriteNumber("observed_s", bucket.Observed.TotalSeconds);
                writer.WriteNumber("unobserved_s", bucket.Unobserved.TotalSeconds);
                writer.WriteNumber("grid_import_kwh", bucket.GridImport.Value);
                writer.WriteNumber("grid_export_kwh", bucket.GridExport.Value);
                writer.WriteNumber("generation_kwh", bucket.Generation.Value);
                writer.WriteNumber("load_kwh", bucket.Load.Value);
                writer.WriteNumber("uncontrolled_kwh", bucket.Uncontrolled.Value);
                writer.WriteNumber("storage_charge_kwh", bucket.StorageCharge.Value);
                writer.WriteNumber("storage_discharge_kwh", bucket.StorageDischarge.Value);
                writer.WriteStartObject("pv");
                writer.WriteNumber("house_kwh", bucket.Pv.House.Value);
                writer.WriteNumber("storage_kwh", bucket.Pv.Storage.Value);
                writer.WriteNumber("export_kwh", bucket.Pv.Export.Value);
                writer.WriteNumber("unattributed_kwh", bucket.Pv.Unattributed.Value);
                writer.WriteEndObject();
                writer.WriteStartObject("devices");
                foreach (var (device, energy) in bucket.Devices.OrderBy(d => d.Key))
                {
                    writer.WriteStartObject(device.Value);
                    writer.WriteNumber("measured_kwh", energy.Measured.Value);
                    writer.WriteNumber("model_kwh", energy.Model.Value);
                    writer.WriteNumber("unknown_s", energy.Unknown.TotalSeconds);
                    writer.WriteNumber("pv_kwh", energy.Pv.Value);
                    writer.WriteNumber("value", energy.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteNumber("sheds", bucket.Sheds);
                writer.WriteNumber("would_sheds", bucket.WouldSheds);
                writer.WriteNumber("voltage_losses", bucket.VoltageLosses);
                writer.WriteNumber("supply_outages", bucket.SupplyOutages);
                writer.WriteNumber("voltage_observed_s", bucket.VoltageObserved.TotalSeconds);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("lifetime");
            WriteTime(writer, "since", state.Lifetime.Since);
            writer.WriteNumber("sheds", state.Lifetime.Sheds);
            writer.WriteNumber("would_sheds", state.Lifetime.WouldSheds);
            writer.WriteNumber("voltage_losses", state.Lifetime.VoltageLosses);
            writer.WriteNumber("supply_outages", state.Lifetime.SupplyOutages);
            writer.WriteNumber("voltage_observed_s", state.Lifetime.VoltageObserved.TotalSeconds);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Drops the oldest buckets, keeping about <paramref name="ratio"/> of them (at least one fewer); the counters stay.</summary>
    protected override StatisticsState? Trim(StatisticsState state, double ratio)
    {
        if (state.Buckets.IsEmpty)
        {
            return null;
        }

        var keep = Math.Min(state.Buckets.Length - 1, (int)(state.Buckets.Length * ratio * TrimMargin));
        return state with { Buckets = state.Buckets[(state.Buckets.Length - keep)..] };
    }

    protected override StatisticsState Parse(JsonElement root)
    {
        var keys = Keys(root, "the document", RootKeys);
        if (keys["schema_version"].ValueKind != JsonValueKind.Number || !keys["schema_version"].TryGetInt32(out var version) || version != SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version is not {SchemaVersion}");
        }

        var minutes = Number(keys["bucket_minutes"], "bucket_minutes");
        var length = _options.BucketLength;
        if (Math.Abs(minutes - length.TotalMinutes) > SecondsTolerance / 60)
        {
            throw new InvalidPresentationFileException(string.Create(CultureInfo.InvariantCulture, $"bucket_minutes is {minutes}, not the configured {length.TotalMinutes}"));
        }

        var savedAt = Time(keys["saved_at"], "saved_at")!.Value;
        var integratedTo = Time(keys["integrated_to"], "integrated_to", nullable: true);
        var latest = integratedTo is { } mark && mark > savedAt ? mark : savedAt;
        var buckets = ImmutableArray.CreateBuilder<EnergyBucket>();
        DateTimeOffset? previous = null;
        var items = Array(keys["buckets"], "buckets", EnergyAccumulator.MaxBuckets(_options));
        for (var i = 0; i < items.Count; i++)
        {
            var bucket = Bucket(items[i], $"buckets[{i}]", length);
            if (EnergyAccumulator.BucketOf(bucket.Start, length) != bucket.Start)
            {
                throw new InvalidPresentationFileException($"buckets[{i}].start is not aligned to the bucket length");
            }

            if (previous is { } before && bucket.Start <= before)
            {
                throw new InvalidPresentationFileException($"buckets[{i}].start does not follow the previous bucket");
            }

            if (bucket.Start > latest)
            {
                throw new InvalidPresentationFileException($"buckets[{i}].start lies after the time the file was saved");
            }

            previous = bucket.Start;
            buckets.Add(bucket);
        }

        var lifetime = Keys(keys["lifetime"], "lifetime", LifetimeKeys);
        return new StatisticsState(
            savedAt,
            length,
            integratedTo,
            buckets.ToImmutable(),
            new LifetimeCounters(
                Time(lifetime["since"], "lifetime.since", nullable: true),
                LongCount(lifetime["sheds"], "lifetime.sheds"),
                LongCount(lifetime["would_sheds"], "lifetime.would_sheds"),
                LongCount(lifetime["voltage_losses"], "lifetime.voltage_losses"),
                LongCount(lifetime["supply_outages"], "lifetime.supply_outages"),
                TimeSpan.FromSeconds(LifetimeSeconds(lifetime["voltage_observed_s"], "lifetime.voltage_observed_s"))));
    }

    private static double LifetimeSeconds(JsonElement element, string path) =>
        NonNegative(element, path) is var seconds && seconds <= MaxLifetimeSeconds
            ? seconds
            : throw new InvalidPresentationFileException($"{path} is longer than a century");

    private static double Amount(JsonElement element, string path, double max = MaxBucketKwh) =>
        NonNegative(element, path) is var amount && amount <= max
            ? amount
            : throw new InvalidPresentationFileException($"{path} is more than one bucket can hold");

    private static EnergyBucket Bucket(JsonElement element, string path, TimeSpan length)
    {
        var keys = Keys(element, path, BucketKeys);
        var kwh = (string key) => new KilowattHours(Amount(keys[key], $"{path}.{key}"));
        var seconds = (string key) => NonNegative(keys[key], $"{path}.{key}");
        var observed = seconds("observed_s");
        var unobserved = seconds("unobserved_s");
        var voltage = seconds("voltage_observed_s");
        if (observed + unobserved > length.TotalSeconds + SecondsTolerance || voltage > length.TotalSeconds + SecondsTolerance)
        {
            throw new InvalidPresentationFileException($"{path} holds more time than one bucket");
        }

        var pv = Keys(keys["pv"], $"{path}.pv", PvKeys);
        var pvKwh = (string key) => new KilowattHours(Amount(pv[key], $"{path}.pv.{key}"));
        var devices = ImmutableDictionary.CreateBuilder<DeviceName, DeviceEnergy>();
        foreach (var property in Object(keys["devices"], $"{path}.devices", MaxDevices))
        {
            if (!DeviceName.TryParse(property.Name, out var device))
            {
                throw new InvalidPresentationFileException($"{path}.devices.{property.Name} is not a device name");
            }

            var devicePath = $"{path}.devices.{property.Name}";
            var entry = Keys(property.Value, devicePath, DeviceKeys);
            var unknown = NonNegative(entry["unknown_s"], $"{devicePath}.unknown_s");
            if (unknown > length.TotalSeconds + SecondsTolerance)
            {
                throw new InvalidPresentationFileException($"{devicePath}.unknown_s is longer than one bucket");
            }

            devices.Add(device.Value, new DeviceEnergy(
                new KilowattHours(Amount(entry["measured_kwh"], $"{devicePath}.measured_kwh")),
                new KilowattHours(Amount(entry["model_kwh"], $"{devicePath}.model_kwh")),
                TimeSpan.FromSeconds(unknown),
                new KilowattHours(Amount(entry["pv_kwh"], $"{devicePath}.pv_kwh")),
                Amount(entry["value"], $"{devicePath}.value", MaxBucketValue)));
        }

        return new EnergyBucket(
            Time(keys["start"], $"{path}.start")!.Value,
            TimeSpan.FromSeconds(observed),
            TimeSpan.FromSeconds(unobserved),
            kwh("grid_import_kwh"),
            kwh("grid_export_kwh"),
            kwh("generation_kwh"),
            kwh("load_kwh"),
            kwh("uncontrolled_kwh"),
            kwh("storage_charge_kwh"),
            kwh("storage_discharge_kwh"),
            new PvAttribution(pvKwh("house_kwh"), pvKwh("storage_kwh"), pvKwh("export_kwh"), pvKwh("unattributed_kwh")),
            devices.ToImmutable(),
            Count(keys["sheds"], $"{path}.sheds"),
            Count(keys["would_sheds"], $"{path}.would_sheds"),
            Count(keys["voltage_losses"], $"{path}.voltage_losses"),
            Count(keys["supply_outages"], $"{path}.supply_outages"),
            TimeSpan.FromSeconds(voltage));
    }
}
