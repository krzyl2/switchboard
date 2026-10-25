using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Decisions;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// statistics.json and excursions.json are untrusted input like state.json: anything out of shape
/// is set aside as .corrupt and the run starts without history, never stops; writes are atomic; and
/// the writer trims to the size cap so the reader never refuses what the writer wrote.
/// </summary>
public sealed class StatisticsFileTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
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

    [Fact]
    public void Restart_restores_buckets_within_retention()
    {
        var file = File();
        var state = State(buckets: 24, devices: 4);
        file.Save(state);

        var loaded = file.Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(24, loaded.State!.Buckets.Length);
        Assert.Equal(state.Buckets[5].GridImport.Value, loaded.State.Buckets[5].GridImport.Value, 12);
        Assert.Equal(state.Buckets[5].Devices[DeviceName.Parse("device_1")].Model.Value, loaded.State.Buckets[5].Devices[DeviceName.Parse("device_1")].Model.Value, 12);
        Assert.Equal(state.Lifetime, loaded.State.Lifetime);
        Assert.Equal(state.IntegratedTo, loaded.State.IntegratedTo);
    }

    [Fact]
    public void A_missing_file_is_a_fresh_start()
    {
        Assert.Equal(StateFileStatus.Fresh, File().Load().Status);
    }

    public static TheoryData<string, Func<JsonNode, JsonNode>> Corruptions() => new()
    {
        { "bad JSON", _ => JsonValue.Create("{not json")! },
        { "another schema", n => Set(n, d => d["schema_version"] = 2) },
        { "another bucket length", n => Set(n, d => d["bucket_minutes"] = 15) },
        { "a misaligned bucket", n => Set(n, d => d["buckets"]![1]!["start"] = "2026-10-24T12:07:00.0000000Z") },
        { "buckets out of order", n => Set(n, d => d["buckets"]![1]!["start"] = "2026-10-24T11:55:00.0000000Z") },
        { "a negative energy", n => Set(n, d => d["buckets"]![0]!["grid_import_kwh"] = -0.1) },
        { "more time than a bucket", n => Set(n, d => d["buckets"]![0]!["unobserved_s"] = 300) },
        { "a bucket after the save", n => Set(n, d => { d["saved_at"] = "2026-10-24T12:01:00.0000000Z"; d["integrated_to"] = null; }) },
        { "a bad device key", n => Set(n, d => d["buckets"]![0]!["devices"]!.AsObject().Add("switch.heater", JsonNode.Parse("""{"measured_kwh":0,"model_kwh":0,"unknown_s":0,"pv_kwh":0,"value":0}"""))) },
        { "an unknown key", n => Set(n, d => d["extra"] = 1) },
        { "a missing key", n => Set(n, d => d["buckets"]![0]!.AsObject().Remove("sheds")) },
        { "a fraction of a shed", n => Set(n, d => d["buckets"]![0]!["sheds"] = 1.5) },

        // Valid JSON, finite, non-negative, schema 1: TimeSpan cannot hold it. Before the bound this
        // threw OverflowException out of the start and the add-on never reached the guard.
        { "a lifetime voltage time TimeSpan cannot hold", n => Set(n, d => d["lifetime"]!["voltage_observed_s"] = 1e300) },

        // Just inside TimeSpan: it loads, then every voltage-observed step overflows the counter.
        { "a lifetime voltage time at the end of TimeSpan", n => Set(n, d => d["lifetime"]!["voltage_observed_s"] = 9e11) },

        // Finite and non-negative, but two of them sum to Infinity, which GET api/statistics cannot
        // serialise and which stopped the statistics writer.
        { "an energy no bucket can hold", n => Set(n, d => d["buckets"]![0]!["grid_import_kwh"] = 1e308) },
        { "a device value no bucket can hold", n => Set(n, d => d["buckets"]![0]!["devices"]!.AsObject().First().Value!["value"] = 1e308) },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Corrupt_file_is_set_aside_and_start_is_fresh(string why, Func<JsonNode, JsonNode> corrupt)
    {
        var file = File();
        file.Save(State(buckets: 4, devices: 2));
        var node = JsonNode.Parse(System.IO.File.ReadAllText(file.FilePath))!;
        var damaged = corrupt(node);
        System.IO.File.WriteAllText(file.FilePath, damaged is JsonValue text && text.TryGetValue<string>(out var raw) ? raw : damaged.ToJsonString());

        var loaded = file.Load();

        Assert.True(loaded.Status == StateFileStatus.ResetCorrupt, why);
        Assert.Null(loaded.State);
        Assert.NotNull(loaded.Warning);
        Assert.True(System.IO.File.Exists(file.FilePath + ".corrupt"), why);
        Assert.False(System.IO.File.Exists(file.FilePath), why);
    }

    [Theory]
    [InlineData(typeof(OverflowException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(FormatException))]
    public void A_value_the_types_cannot_hold_is_set_aside_like_any_other_corruption(Type thrown)
    {
        // Whatever the strict checks miss and a conversion still refuses must never escape Load: the
        // presentation loads before the guard starts, so an exception here would stop the add-on.
        File().Save(State(buckets: 2, devices: 1));
        var file = new ThrowingStatisticsFile(_directory, (Exception)Activator.CreateInstance(thrown, "out of range")!);

        var loaded = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, loaded.Status);
        Assert.Null(loaded.State);
        Assert.True(System.IO.File.Exists(file.FilePath + ".corrupt"));
    }

    [Fact]
    public void An_oversized_file_is_not_read()
    {
        var file = File();
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(file.FilePath, new string(' ', TestStatistics.Options().MaxFileBytes + 1));

        Assert.Equal(StateFileStatus.ResetCorrupt, file.Load().Status);
    }

    [Fact]
    public void Too_many_buckets_are_refused()
    {
        var options = TestStatistics.Options();
        var writer = File(TestStatistics.Options(retention: TimeSpan.FromDays(8)));
        writer.Save(State(buckets: EnergyAccumulator.MaxBuckets(options) + 1, devices: 1));

        Assert.Equal(StateFileStatus.ResetCorrupt, File(options).Load().Status);
    }

    [Fact]
    public void Sixty_four_devices_eight_days_and_a_64_KiB_cap_still_reload()
    {
        // A valid configuration must never produce a file its own loader refuses: the writer drops
        // the oldest buckets until it fits, and the file loads as loaded, not reset_corrupt.
        var options = TestStatistics.Options(retention: TimeSpan.FromDays(8), maxFileBytes: 64 * 1024);
        var file = File(options);
        file.Save(State(buckets: EnergyAccumulator.MaxBuckets(options) - 2, devices: 64));

        var loaded = file.Load();

        Assert.True(new FileInfo(file.FilePath).Length <= options.MaxFileBytes);
        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.NotEmpty(loaded.State!.Buckets);
    }

    [Fact]
    public void Failed_rename_keeps_previous_file()
    {
        // The temporary file is written and only then moved over the old one: a failure in between
        // leaves the previous file whole.
        var file = File();
        file.Save(State(buckets: 3, devices: 1));
        var before = System.IO.File.ReadAllText(file.FilePath);
        var failing = new FailingStatisticsFile(_directory, TestStatistics.Options());

        Assert.Throws<IOException>(() => failing.Save(State(buckets: 6, devices: 1)));

        Assert.Equal(before, System.IO.File.ReadAllText(file.FilePath));
        Assert.Equal(StateFileStatus.Loaded, file.Load().Status);
    }

    internal static StatisticsState State(int buckets, int devices)
    {
        var length = TimeSpan.FromMinutes(5);
        var names = Enumerable.Range(0, devices).Select(i => DeviceName.Parse($"device_{i}")).ToList();
        var list = Enumerable.Range(0, buckets).Select(i => EnergyBucket.Empty(Start + (length * i)) with
        {
            Observed = TimeSpan.FromSeconds(290),
            Unobserved = TimeSpan.FromSeconds(10),
            GridImport = new KilowattHours(0.1 + (i * 0.001)),
            Load = new KilowattHours(0.12),
            Devices = names.ToImmutableDictionary(n => n, _ => new DeviceEnergy(new KilowattHours(0.01), new KilowattHours(0.02), TimeSpan.FromSeconds(3), new KilowattHours(0.004), 0.0105)),
            Sheds = 1,
        }).ToImmutableArray();
        var end = Start + (length * buckets);
        return new StatisticsState(end, length, end, list, new LifetimeCounters(Start, 3, 4, 1, 0, TimeSpan.FromHours(2)));
    }

    private static JsonNode Set(JsonNode node, Action<JsonNode> change)
    {
        change(node);
        return node;
    }

    private StatisticsFile File(StatisticsOptions? options = null) => new(_directory, options ?? TestStatistics.Options(), NullLogger<StatisticsFile>.Instance);

    private sealed class FailingStatisticsFile(string directory, StatisticsOptions options) : StatisticsFile(directory, options, NullLogger<StatisticsFile>.Instance)
    {
        protected override void Move(string temporary, string target) => throw new IOException("rename refused");
    }

    private sealed class ThrowingStatisticsFile(string directory, Exception thrown) : StatisticsFile(directory, TestStatistics.Options(), NullLogger<StatisticsFile>.Instance)
    {
        protected override StatisticsState Parse(JsonElement root) => throw thrown;
    }
}

public sealed class ExcursionFileTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
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

    [Fact]
    public void Restart_restores_the_last_and_worst_excursion()
    {
        var file = File();
        var state = State(samples: 30);
        file.Save(state);

        var loaded = file.Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        var l1 = loaded.State!.Phases.Single(p => p.Phase == Phase.L1);
        Assert.Equal(30, l1.Worst!.Samples.Length);
        Assert.Equal(30.35, l1.Worst.Peak.Value, 6);
        Assert.Equal(DecisionAction.SwitchOff, l1.Worst.Decisions[0].Action);
        Assert.Equal(ReasonCode.GuardTierExceeded, l1.Worst.Decisions[0].Reason);
        Assert.Null(l1.Last!.End);
    }

    public static TheoryData<string, Func<JsonNode, JsonNode>> Corruptions() => new()
    {
        { "bad JSON", _ => JsonValue.Create("[1,")! },
        { "another schema", n => Set(n, d => d["schema_version"] = 7) },
        { "a phase that does not exist", n => Set(n, d => d["phases"]!.AsObject().Add("L4", JsonNode.Parse("""{"last":null,"worst":null}"""))) },
        { "an unknown action", n => Set(n, d => d["phases"]!["L1"]!["worst"]!["decisions"]![0]!["action"] = "turn_off") },
        { "a reason outside the list", n => Set(n, d => d["phases"]!["L1"]!["worst"]!["decisions"]![0]!["reason"] = "kettle") },
        { "samples out of order", n => Set(n, d => d["phases"]!["L1"]!["worst"]!["samples"]![0]![0] = "2026-10-24T10:00:00.0000000Z") },
        { "a sample that is not a pair", n => Set(n, d => d["phases"]!["L1"]!["worst"]!["samples"]![0] = JsonNode.Parse("[\"2026-10-24T09:00:00.0000000Z\"]")) },
        { "a negative peak", n => Set(n, d => d["phases"]!["L1"]!["worst"]!["peak_a"] = -3) },
        { "an end before its start", n => Set(n, d => d["phases"]!["L1"]!["worst"]!["end"] = "2026-10-24T08:00:00.0000000Z") },
        { "a bad device", n => Set(n, d => d["phases"]!["L1"]!["worst"]!["decisions"]![0]!["device"] = "switch.heater") },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Corrupt_file_is_set_aside_and_start_is_fresh(string why, Func<JsonNode, JsonNode> corrupt)
    {
        var file = File();
        file.Save(State(samples: 10));
        var node = JsonNode.Parse(System.IO.File.ReadAllText(file.FilePath))!;
        var damaged = corrupt(node);
        System.IO.File.WriteAllText(file.FilePath, damaged is JsonValue text && text.TryGetValue<string>(out var raw) ? raw : damaged.ToJsonString());

        var loaded = file.Load();

        Assert.True(loaded.Status == StateFileStatus.ResetCorrupt, why);
        Assert.True(System.IO.File.Exists(file.FilePath + ".corrupt"), why);
    }

    [Fact]
    public void More_samples_than_the_cap_are_refused()
    {
        File(TestStatistics.Options(maxSamples: 1000)).Save(State(samples: 800));

        Assert.Equal(StateFileStatus.ResetCorrupt, File(TestStatistics.Options(maxSamples: 720)).Load().Status);
    }

    [Fact]
    public void Writer_thins_samples_to_fit_the_size_cap_and_keeps_the_peak()
    {
        var options = TestStatistics.Options(maxFileBytes: 64 * 1024, maxSamples: 10_000);
        var file = File(options);
        file.Save(State(samples: 5000));

        var loaded = file.Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        var worst = loaded.State!.Phases.Single(p => p.Phase == Phase.L1).Worst!;
        Assert.True(worst.Decimated);
        Assert.Contains(worst.Samples, s => s.Current.Value == 30.35);
    }

    [Fact]
    public void Failed_rename_keeps_previous_file()
    {
        var file = File();
        file.Save(State(samples: 5));
        var before = System.IO.File.ReadAllText(file.FilePath);

        Assert.Throws<IOException>(() => new FailingExcursionFile(_directory, TestStatistics.Options()).Save(State(samples: 9)));

        Assert.Equal(before, System.IO.File.ReadAllText(file.FilePath));
    }

    private static ExcursionState State(int samples)
    {
        var list = Enumerable.Range(0, samples).Select(i => new ExcursionSample(Start + TimeSpan.FromSeconds(10 * i), new Amperes(i == samples / 2 ? 30.35 : 16 + (i % 7)))).ToImmutableArray();
        var decision = new ExcursionDecision("abc-000000001", Start + TimeSpan.FromSeconds(10 * (samples / 2)), DecisionAction.SwitchOff, ReasonCode.GuardTierExceeded, DeviceName.Parse("heater_l1"), false);
        var worst = new Excursion(Phase.L1, Start, Start + TimeSpan.FromSeconds(10 * samples), new Amperes(30.35), list[samples / 2].At, new Amperes(14.13), new Amperes(25), new Amperes(28.25), false, list, [decision]);
        var last = worst with { Start = Start + TimeSpan.FromHours(1), End = null, Samples = [.. list.Take(3).Select(s => s with { At = s.At + TimeSpan.FromHours(1) })], Decisions = [], Peak = new Amperes(16), PeakAt = Start + TimeSpan.FromHours(1) };
        return new ExcursionState(Start + TimeSpan.FromHours(2), [new PhaseExcursions(Phase.L1, last, worst), new PhaseExcursions(Phase.L2, null, null)]);
    }

    private static JsonNode Set(JsonNode node, Action<JsonNode> change)
    {
        change(node);
        return node;
    }

    private ExcursionFile File(StatisticsOptions? options = null) => new(_directory, options ?? TestStatistics.Options(), NullLogger<ExcursionFile>.Instance);

    private sealed class FailingExcursionFile(string directory, StatisticsOptions options) : ExcursionFile(directory, options, NullLogger<ExcursionFile>.Instance)
    {
        protected override void Move(string temporary, string target) => throw new IOException("rename refused");
    }
}
