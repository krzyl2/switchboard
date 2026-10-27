using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Planning;
using Switchboard.Application.Presentation;
using Switchboard.Application.State;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Persistence;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// /data/load-profile.json and the keeper that feeds it. Why it matters: the profile is the only memory of the house's weekly load (the statistics keep 26
/// hours), so it has to survive a restart exactly; and since it is read back from disk it is untrusted input, so a damaged file must start the forecast over
/// from the fallback, never feed a corrupt number into a plan.
/// </summary>
public sealed class LoadProfileFileTests : IDisposable
{
    private const string EmptyBin = """{"mean_kw":0,"weight":0,"samples":0,"last_at":null}""";

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly ForecastParameters Parameters = new(TimeSpan.FromMinutes(60), 0.85, TimeSpan.FromDays(14), 0.8, 400);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));

    private LoadProfileFile File() => new(_directory, NullLogger<LoadProfileFile>.Instance);

    private string FilePath => Path.Combine(_directory, LoadProfileFile.Name);

    private static LoadProfile Sample()
    {
        var bins = Enumerable.Repeat(LoadBin.Empty, LoadProfile.BinCount).ToArray();
        bins[34] = new LoadBin(1.2345678901234, 3.5, 4, new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        bins[167] = new LoadBin(0.4, 1, 1, new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero));
        return new LoadProfile([.. bins], new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>A valid document of identical bins; each test changes one thing.</summary>
    private static string Json(string bin = EmptyBin, int bins = LoadProfile.BinCount, string fed = "\"2026-10-27T12:00:00.0000000Z\"", int schema = 1) =>
        $$"""{"schema_version":{{schema}},"fed_through":{{fed}},"bins":[{{string.Join(',', Enumerable.Repeat(bin, bins))}}]}""";

    private LoadProfile? LoadText(string content)
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(FilePath, content);
        return File().Load().State;
    }

    [Fact]
    public void The_profile_survives_a_restart_to_the_last_digit_and_leaves_no_temporary_file()
    {
        // Why: the weekly shape is built over weeks; a rounding on every restart would erode it, and a leftover .tmp would mean the write was not atomic.
        var original = Sample();

        File().Save(original);
        var loaded = File().Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(original.Bins, loaded.State!.Bins);
        Assert.Equal(original.FedThrough, loaded.State.FedThrough);
        Assert.False(System.IO.File.Exists(FilePath + PresentationFile<LoadProfile>.TemporarySuffix));
        Assert.True(new FileInfo(FilePath).Length < LoadProfileFile.MaxFileBytes / 3);
    }

    [Fact]
    public void An_absent_file_is_a_fresh_start()
    {
        // Why: first run is the normal case, not an error; the keeper seeds from the retained statistics.
        var loaded = File().Load();

        Assert.Null(loaded.State);
        Assert.Equal(StateFileStatus.Fresh, loaded.Status);
    }

    [Fact]
    public void A_valid_empty_profile_loads()
    {
        // Why: the shape the test builder writes is the shape the reader accepts; every corruption case below differs from it by one thing.
        var profile = LoadText(Json());

        Assert.NotNull(profile);
        Assert.Equal(0, profile.FilledBins);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void A_file_that_is_not_a_profile_document_is_set_aside(string content)
    {
        // Why: untrusted on read. The damaged file is kept as .corrupt for the maintainer and the forecast starts over.
        Assert.Null(LoadText(content));
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
        Assert.False(System.IO.File.Exists(FilePath));
    }

    [Fact]
    public void Another_schema_is_set_aside()
    {
        // Why: a profile written by a different schema cannot be trusted to mean the same thing by its numbers.
        Assert.Null(LoadText(Json(schema: 2)));
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
    }

    [Theory]
    [InlineData(167)]
    [InlineData(169)]
    [InlineData(0)]
    public void Other_than_168_bins_is_set_aside(int count)
    {
        // Why: the bin index is the hour of the week; a file with a different count would shift every hour.
        Assert.Null(LoadText(Json(bins: count)));
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void An_unknown_or_missing_key_is_set_aside()
    {
        // Why: exact keys, as the other presentation files; an extra key is a file from somewhere else.
        Assert.Null(LoadText(Json().Replace("\"bins\"", "\"extra\":1,\"bins\"", StringComparison.Ordinal)));
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
        System.IO.File.Delete(FilePath + ".corrupt");

        Assert.Null(LoadText(Json().Replace("\"fed_through\":\"2026-10-27T12:00:00.0000000Z\",", string.Empty, StringComparison.Ordinal)));
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
    }

    [Theory]
    [InlineData("""{"mean_kw":-1,"weight":1,"samples":1,"last_at":"2026-10-27T09:00:00.0000000Z"}""")]
    [InlineData("""{"mean_kw":5000,"weight":1,"samples":1,"last_at":"2026-10-27T09:00:00.0000000Z"}""")]
    [InlineData("""{"mean_kw":1,"weight":-1,"samples":1,"last_at":"2026-10-27T09:00:00.0000000Z"}""")]
    [InlineData("""{"mean_kw":1,"weight":1,"samples":-1,"last_at":"2026-10-27T09:00:00.0000000Z"}""")]
    [InlineData("""{"mean_kw":1,"weight":1,"samples":1.5,"last_at":"2026-10-27T09:00:00.0000000Z"}""")]
    [InlineData("""{"mean_kw":1,"weight":1,"samples":1,"last_at":"yesterday"}""")]
    [InlineData("""{"mean_kw":"1","weight":1,"samples":1,"last_at":"2026-10-27T09:00:00.0000000Z"}""")]
    [InlineData("""{"mean_kw":1,"weight":0,"samples":1,"last_at":"2026-10-27T09:00:00.0000000Z"}""")]
    [InlineData("""{"mean_kw":1,"weight":1,"samples":0,"last_at":null}""")]
    [InlineData("""{"mean_kw":1,"weight":1,"samples":1,"last_at":null}""")]
    [InlineData("""{"mean_kw":1,"weight":1,"samples":1,"last_at":"2026-10-27T12:00:00.0000000Z"}""")]
    public void A_bad_bin_sets_the_file_aside(string bin)
    {
        // Why: a negative or absurd load, a bin that is filled in some fields and empty in others, or a sample from an hour that was not fed yet is not something the
        // writer produces; reading it would put a made-up number into every plan.
        Assert.Null(LoadText(Json(bin)));
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void A_file_above_the_size_cap_is_set_aside_without_being_parsed()
    {
        // Why: the cap bounds what an untrusted file can cost; 168 bins are about 10 KB, so anything over 64 KiB is not this file.
        var big = new string(' ', LoadProfileFile.MaxFileBytes + 1);

        Assert.Null(LoadText(Json() + big));
        Assert.True(System.IO.File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void A_failed_write_leaves_the_previous_file_whole()
    {
        // Why: a crash or a full disk mid-write must not lose the profile built over weeks: the previous file stays until the new one is fully flushed and moved over it.
        File().Save(Sample());
        var before = System.IO.File.ReadAllBytes(FilePath);
        var failing = new FailingMove(_directory);

        Assert.Throws<IOException>(() => failing.Save(LoadProfile.Empty));

        Assert.Equal(before, System.IO.File.ReadAllBytes(FilePath));
    }

    private sealed class FailingMove(string directory) : LoadProfileFile(directory, NullLogger<LoadProfileFile>.Instance)
    {
        protected override void Move(string temporary, string target) => throw new IOException("disk full");
    }

    // ----- the keeper: seeding and the hourly feed -----

    private sealed class MemoryStore : IPresentationStore<LoadProfile>
    {
        public LoadProfile? Saved { get; private set; }

        public int Saves { get; private set; }

        public PresentationLoad<LoadProfile> Load() => new(Saved, Saved is null ? StateFileStatus.Fresh : StateFileStatus.Loaded, null);

        public void Save(LoadProfile state)
        {
            Saved = state;
            Saves++;
        }
    }

    private static EnergyBucket Bucket(DateTimeOffset start, double uncontrolledKwh) =>
        EnergyBucket.Empty(start) with
        {
            Observed = TimeSpan.FromMinutes(5),
            Uncontrolled = new KilowattHours(uncontrolledKwh),
        };

    private static StatisticsSnapshot Snapshot(IEnumerable<EnergyBucket> buckets, DateTimeOffset integratedTo) => new(
        integratedTo,
        TimeSpan.FromMinutes(5),
        false,
        [],
        buckets.ToImmutableSortedDictionary(b => b.Start, b => b),
        LifetimeCounters.Zero,
        integratedTo,
        1);

    private static LoadProfileKeeper Keeper(MemoryStore store) => new(store, Parameters, Warsaw, HolidayCalendar.Empty);

    [Fact]
    public void The_first_update_seeds_the_profile_from_the_retained_buckets_and_saves_it()
    {
        // Why: with no file the profile starts from the 26 hours the statistics still hold (about a day of bins), so a fresh install does not wait a week for any shape.
        // Two full hours of 0.1 kWh per 5 minutes = 1.2 kW, Monday 2026-10-26 10:00 and 11:00 local (UTC+1).
        var store = new MemoryStore();
        var keeper = Keeper(store);
        var start = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));
        var buckets = Enumerable.Range(0, 24).Select(i => Bucket(start.AddMinutes(5 * i), 0.1));

        var moved = keeper.Update(Snapshot(buckets, start.AddHours(2)), start.AddHours(2).AddMinutes(1));

        Assert.True(moved);
        Assert.Equal(2, keeper.Current.FilledBins);
        Assert.Equal(1.2, keeper.Current.Bins[10].MeanKw, 9);
        Assert.Equal(1.2, keeper.Current.Bins[11].MeanKw, 9);
        Assert.Same(keeper.Current, store.Saved);
    }

    [Fact]
    public void A_second_update_inside_the_same_hour_changes_and_saves_nothing()
    {
        // Why: the writer loop calls often; the file is written once per completed hour, not every call, and nothing is counted twice.
        var store = new MemoryStore();
        var keeper = Keeper(store);
        var start = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));
        var buckets = Enumerable.Range(0, 12).Select(i => Bucket(start.AddMinutes(5 * i), 0.1)).ToList();
        keeper.Update(Snapshot(buckets, start.AddHours(1)), start.AddHours(1));

        var again = keeper.Update(Snapshot(buckets, start.AddHours(1).AddMinutes(10)), start.AddHours(1).AddMinutes(10));

        Assert.False(again);
        Assert.Equal(1, store.Saves);
        Assert.Equal(1, keeper.Current.Bins[10].Samples);
    }

    [Fact]
    public void The_feed_stops_at_the_statistics_high_water_mark_even_when_the_clock_is_ahead()
    {
        // Why: now can be ahead of what the accumulator has integrated (a gap, a restart). An hour counts only when its data is in; otherwise the missing minutes
        // would be read as zero load.
        var store = new MemoryStore();
        var keeper = Keeper(store);
        var start = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));
        var buckets = Enumerable.Range(0, 6).Select(i => Bucket(start.AddMinutes(5 * i), 0.1));

        keeper.Update(Snapshot(buckets, start.AddMinutes(30)), start.AddHours(3));

        Assert.Equal(0, keeper.Current.FilledBins);
    }

    [Fact]
    public void A_restored_profile_is_not_fed_the_same_hours_again()
    {
        // Why: the whole point of the file. After a restart the statistics still hold the hours the saved profile already counted; they must not be counted a second time.
        var store = new MemoryStore();
        var start = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(1));
        var buckets = Enumerable.Range(0, 12).Select(i => Bucket(start.AddMinutes(5 * i), 0.1)).ToList();
        Keeper(store).Update(Snapshot(buckets, start.AddHours(1)), start.AddHours(1));
        var restarted = Keeper(store);

        restarted.Restore();
        var moved = restarted.Update(Snapshot(buckets, start.AddHours(1)), start.AddHours(1));

        Assert.False(moved);
        Assert.Equal(1, restarted.Current.Bins[10].Samples);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
