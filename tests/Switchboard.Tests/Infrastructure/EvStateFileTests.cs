using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Presentation;
using Switchboard.Application.State;
using Switchboard.Domain.Ev;
using Switchboard.Infrastructure.Persistence;
using static Switchboard.Tests.Domain.Ev.EvTestParameters;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// /data/ev.json. Why it matters: the learned kWh/km is weeks of data, so it must survive a restart exactly; and the file is read back as untrusted input,
/// so a damaged one starts the module over from its defaults, set aside, and (unlike state.json) never engages the kill switch.
/// </summary>
public sealed class EvStateFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));

    private EvStateFile File() => new(_directory, NullLogger<EvStateFile>.Instance);

    private string FilePath => Path.Combine(_directory, EvStateFile.Name);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static EvState Sample() => new(
        EvMode.PvOnly,
        new EvChargeNow(T0, 85.5, T0.AddHours(12)),
        new KwhPerKmState(0.2134567890123, [new KwhPerKmSample(T0.AddDays(-2), 0.19), new KwhPerKmSample(T0.AddDays(-1), 0.2345678901234)]),
        new EvLastTrip("2026-10-31T06:00:00+01:00|42", 91.25, 105.5555555555556, true, true, T0));

    private const string Valid =
        """{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":0.2,"samples":[{"at":"2026-10-28T08:00:00.0000000Z","value":0.2}]},"last_trip":null}""";

    private PresentationLoad<EvState> LoadText(string content)
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(FilePath, content);
        return File().Load();
    }

    [Fact]
    public void The_state_survives_a_restart_to_the_last_digit_and_leaves_no_temporary_file()
    {
        // Why: a rounding on every restart would erode the learned value; a leftover .tmp would mean the write was not atomic.
        var original = Sample();

        File().Save(original);
        var loaded = File().Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(original.Mode, loaded.State!.Mode);
        Assert.Equal(original.ChargeNow, loaded.State.ChargeNow);
        Assert.Equal(original.Learned.Smoothed, loaded.State.Learned.Smoothed);
        Assert.Equal(original.Learned.Samples, loaded.State.Learned.Samples);
        Assert.Equal(original.LastTrip, loaded.State.LastTrip);
        Assert.False(System.IO.File.Exists(FilePath + PresentationFile<EvState>.TemporarySuffix));
    }

    [Fact]
    public void The_learned_value_still_steers_the_plan_after_a_restart()
    {
        // Why: the point of persisting. After a restart the consumption is the learned one, not the fallback.
        var learned = KwhPerKmLearner.Learn(KwhPerKmState.Empty, new EvConsumptionObservation(60, T0, 36, T0, false), CapacityKwh, Defaults.Learn).State;
        File().Save(EvState.Default with { Learned = learned });

        var after = File().Load().State!;

        Assert.Equal(0.30, KwhPerKmLearner.Current(after.Learned, Defaults.FallbackKwhPerKm, Defaults.Learn), 9);
    }

    [Fact]
    public void An_absent_file_is_a_fresh_start()
    {
        var loaded = File().Load();

        Assert.Null(loaded.State);
        Assert.Equal(StateFileStatus.Fresh, loaded.Status);
    }

    [Fact]
    public void A_valid_document_loads()
    {
        // Why: the shape the corruption cases below differ from by one thing each.
        var loaded = LoadText(Valid);

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(EvMode.Auto, loaded.State!.Mode);
        Assert.Equal(0.2, loaded.State.Learned.Smoothed);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{"schema_version":0,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":null,"extra":1}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]}}""")]
    [InlineData("""{"schema_version":1,"mode":"turbo","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":0,"samples":[]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":50,"samples":[]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":"0.2","samples":[]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[{"at":"2026-10-28T08:00:00.0000000Z","value":0.2}]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":0.2,"samples":[{"at":"2026-10-28T08:00:00.0000000Z","value":-0.2}]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":0.2,"samples":[{"at":"yesterday","value":0.2}]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":{"started_at":"2026-10-28T08:00:00.0000000Z","target_percent":90,"expires_at":"2026-10-28T07:00:00.0000000Z"},"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":{"started_at":"2026-10-28T08:00:00.0000000Z","target_percent":140,"expires_at":"2026-10-28T09:00:00.0000000Z"},"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":null}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":{"trip_key":"","target_percent":90,"trip_soc_percent":50,"exceeds_electric_range":false,"clamped":false,"frozen_at":"2026-10-28T08:00:00.0000000Z"}}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":{"trip_key":"a","target_percent":-1,"trip_soc_percent":50,"exceeds_electric_range":false,"clamped":false,"frozen_at":"2026-10-28T08:00:00.0000000Z"}}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":{"trip_key":"a","target_percent":90,"exceeds_electric_range":false,"frozen_at":"2026-10-28T08:00:00.0000000Z"}}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":{"trip_key":"a","target_percent":90,"trip_soc_percent":-1,"exceeds_electric_range":false,"clamped":false,"frozen_at":"2026-10-28T08:00:00.0000000Z"}}""")]
    [InlineData("""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":null,"samples":[]},"last_trip":{"trip_key":"a","target_percent":90,"trip_soc_percent":50,"exceeds_electric_range":"no","clamped":false,"frozen_at":"2026-10-28T08:00:00.0000000Z"}}""")]
    public void A_file_that_is_not_an_ev_document_is_set_aside_and_the_module_starts_from_defaults(string content)
    {
        // Why: untrusted on read. The damaged file is kept as .corrupt for the maintainer; nothing in it may reach a plan.
        var loaded = LoadText(content);

        Assert.Null(loaded.State);
        Assert.Equal(StateFileStatus.ResetCorrupt, loaded.Status);
        Assert.NotNull(loaded.Warning);
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
        Assert.False(System.IO.File.Exists(FilePath));
    }

    [Fact]
    public void A_corrupt_ev_file_leaves_state_json_alone()
    {
        // Why: EV.md section 3 point 6. A bad ev.json is a warning and defaults; it must never touch state.json, whose own rejection engages the latched kill switch.
        Directory.CreateDirectory(_directory);
        var statePath = Path.Combine(_directory, "state.json");
        System.IO.File.WriteAllText(statePath, "{\"schema_version\":6}");

        LoadText("{ not json");

        Assert.Equal("{\"schema_version\":6}", System.IO.File.ReadAllText(statePath));
        Assert.False(System.IO.File.Exists(statePath + ".corrupt"));
    }

    [Fact]
    public void Too_many_samples_in_a_file_is_set_aside()
    {
        // Why: the history is bounded; a file with far more samples than the module keeps is not one it wrote.
        var sample = """{"at":"2026-10-28T08:00:00.0000000Z","value":0.2}""";
        var many = string.Join(',', Enumerable.Repeat(sample, EvStateFile.MaxSamplesOnRead + 1));

        var loaded = LoadText($$"""{"schema_version":1,"mode":"auto","charge_now":null,"kwh_per_km":{"smoothed":0.2,"samples":[{{many}}]},"last_trip":null}""");

        Assert.Equal(StateFileStatus.ResetCorrupt, loaded.Status);
    }

    [Fact]
    public void A_file_above_the_size_cap_is_set_aside_without_being_parsed()
    {
        var loaded = LoadText(Valid + new string(' ', EvStateFile.MaxFileBytes + 1));

        Assert.Equal(StateFileStatus.ResetCorrupt, loaded.Status);
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void The_file_keeps_the_newest_200_samples_whatever_the_state_holds()
    {
        // Why: bounded size. The learner may be asked for at most 200 samples (the configuration bound is the file's), and even a state with more is written as the
        // newest 200 so the reader never refuses it. 400 in, exactly 200 out, the last one intact.
        var samples = Enumerable.Range(0, 400).Select(i => new KwhPerKmSample(T0.AddHours(i), 0.2 + (i * 1e-9))).ToImmutableArray();

        File().Save(EvState.Default with { Learned = new KwhPerKmState(0.2123456789, samples) });
        var loaded = File().Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(0.2123456789, loaded.State!.Learned.Smoothed);
        Assert.Equal(EvStateFile.MaxSamplesOnRead, loaded.State.Learned.Samples.Length);
        Assert.Equal(samples[^200], loaded.State.Learned.Samples[0]);
        Assert.Equal(samples[^1], loaded.State.Learned.Samples[^1]);
    }

    [Fact]
    public void The_last_full_charge_survives_a_restart_and_a_file_without_it_reads_as_never()
    {
        // Why: the weekly full charge is once a week; forgetting it on restart would offer a second one. And the field is optional, so a schema 1 file
        // written before it existed still loads (no schema bump, no set-aside).
        File().Save(Sample() with { LastFullAt = T0.AddDays(-1) });
        Assert.Equal(T0.AddDays(-1), File().Load().State!.LastFullAt);

        var old = LoadText(Valid);
        Assert.Equal(StateFileStatus.Loaded, old.Status);
        Assert.Null(old.State!.LastFullAt);

        File().Save(Sample());
        Assert.DoesNotContain("last_full_at", System.IO.File.ReadAllText(FilePath));
    }

    [Fact]
    public void The_worst_honest_document_fits_the_size_cap_with_room_to_spare()
    {
        // Why: the trim path is gone because it cannot be reached; this is the proof. 200 samples with full-length numbers and times, a trip key of 200 characters
        // that all need escaping, and the longest numbers: if a field is ever added or a limit raised so this stops fitting, the test fails instead of the writer.
        var samples = Enumerable.Range(0, EvStateFile.MaxSamplesOnRead).Select(i => new KwhPerKmSample(T0.AddTicks(i + 1), 0.123456789012345 + i * 1e-3)).ToImmutableArray();
        var key = new string('"', EvStateFile.MaxTripKeyLength);
        var state = new EvState(
            EvMode.PvOnly,
            new EvChargeNow(T0, 99.99999999999999, T0.AddDays(7)),
            new KwhPerKmState(9.999999999999998, samples),
            new EvLastTrip(key, 99.99999999999999, 1234567.123456789, true, true, T0));

        File().Save(state);

        var size = new FileInfo(FilePath).Length;
        Assert.True(size < EvStateFile.MaxFileBytes / 2, $"{size} bytes");
        Assert.Equal(StateFileStatus.Loaded, File().Load().Status);
    }

    [Fact]
    public void Whatever_the_state_the_module_never_writes_a_file_it_would_set_aside()
    {
        // Why: the writer and the reader must agree. A state the reader would refuse (NaN, an empty or overlong trip key, a target of 140, a charge that ends before
        // it starts, a smoothed value of zero, a sample of 50 kWh/km) is cleaned up on the way out, so the next start loads it instead of discarding the file.
        var absurd = new EvState(
            (EvMode)99,
            new EvChargeNow(T0, 140, T0.AddHours(-1)),
            new KwhPerKmState(0, [new KwhPerKmSample(T0, 50), new KwhPerKmSample(T0, double.NaN)]),
            new EvLastTrip(new string('k', 5000), 140, -5, false, false, T0));

        File().Save(absurd);
        var loaded = File().Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(EvMode.Auto, loaded.State!.Mode);
        Assert.Null(loaded.State.ChargeNow);
        Assert.Null(loaded.State.Learned.Smoothed);
        Assert.Empty(loaded.State.Learned.Samples);
        Assert.Equal(EvStateFile.MaxTripKeyLength, loaded.State.LastTrip!.TripKey.Length);
        Assert.Equal(100, loaded.State.LastTrip.TargetPercent);
        Assert.Equal(0, loaded.State.LastTrip.TripSocPercent);
    }

    [Fact]
    public void Values_at_the_edge_of_what_configuration_allows_round_trip()
    {
        // Why: configuration may allow a consumption up to MaxKwhPerKmLimit and 200 samples; a learned value at those limits must survive, not be dropped.
        var edge = new KwhPerKmState(EvLearnParameters.MaxKwhPerKmLimit, [new KwhPerKmSample(T0, EvLearnParameters.MaxKwhPerKmLimit), new KwhPerKmSample(T0.AddHours(1), double.Epsilon * 1e300)]);

        File().Save(EvState.Default with { Learned = edge, ChargeNow = new EvChargeNow(T0, 0, T0.AddTicks(1)) });
        var loaded = File().Load().State!;

        Assert.Equal(edge.Smoothed, loaded.Learned.Smoothed);
        Assert.Equal(edge.Samples, loaded.Learned.Samples);
        Assert.Equal(0, loaded.ChargeNow!.TargetPercent);
        Assert.Equal(EvLearnParameters.MaxSamplesLimit, EvStateFile.MaxSamplesOnRead);
    }

    [Fact]
    public void A_file_of_a_newer_schema_is_moved_aside_by_its_version_and_never_destroyed()
    {
        // Why: after a rollback the older version must not throw away what the newer one learned, nor overwrite an earlier .corrupt: it moves the file to
        // ev.json.schema<N>, starts from defaults, and a second such file does not replace the first.
        const string newer = """{"schema_version":2,"anything":"a newer version wrote"}""";

        var loaded = LoadText(newer);

        Assert.Null(loaded.State);
        Assert.Equal(StateFileStatus.ResetCorrupt, loaded.Status);
        Assert.Equal(newer, System.IO.File.ReadAllText(FilePath + ".schema2"));
        Assert.False(System.IO.File.Exists(FilePath));
        Assert.False(System.IO.File.Exists(FilePath + ".corrupt"));

        LoadText(newer.Replace("anything", "other", StringComparison.Ordinal));

        Assert.Equal(newer, System.IO.File.ReadAllText(FilePath + ".schema2"));
        Assert.Contains("other", System.IO.File.ReadAllText(FilePath + ".schema2.1"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_schema_does_not_replace_an_earlier_corrupt_copy()
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(FilePath + ".corrupt", "earlier damage");

        LoadText("""{"schema_version":3}""");

        Assert.Equal("earlier damage", System.IO.File.ReadAllText(FilePath + ".corrupt"));
        Assert.True(System.IO.File.Exists(FilePath + ".schema3"));
    }

    [Fact]
    public void A_failed_write_leaves_the_previous_file_whole()
    {
        // Why: a crash or a full disk mid-write must not lose the learned value: the old file stays until the new one is flushed and moved over it.
        File().Save(Sample());
        var before = System.IO.File.ReadAllBytes(FilePath);

        Assert.Throws<IOException>(() => new FailingMove(_directory).Save(EvState.Default));

        Assert.Equal(before, System.IO.File.ReadAllBytes(FilePath));
    }

    private sealed class FailingMove(string directory) : EvStateFile(directory, NullLogger<EvStateFile>.Instance)
    {
        protected override void Move(string temporary, string target) => throw new IOException("disk full");
    }
}
