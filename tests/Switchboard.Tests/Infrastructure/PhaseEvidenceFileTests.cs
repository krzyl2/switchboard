using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// /data/phase-evidence.json (maintainer survey 2026-10-25): the self-test's observations, passive and
/// active, survive a restart. The file is untrusted on read (a corrupt one is set aside and the store
/// starts empty, never a crash), and it is written from its own loop: adding an observation on the
/// guard's thread never touches the disk.
/// </summary>
public sealed class PhaseEvidenceFileTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Observations_survive_a_restart_with_every_field()
    {
        var file = File();
        var saved = new[] { Observation(At, 1, ObservationSource.Active), Observation(At.AddMinutes(6), -1, ObservationSource.Recorded) };

        ((IPhaseEvidenceFile)file).Save(new PhaseEvidenceState(At.AddMinutes(7), ImmutableDictionary<EntityId, ImmutableArray<PhaseObservation>>.Empty.Add(Entity, [.. saved])));
        var loaded = ((IPhaseEvidenceFile)File()).Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        var observations = loaded.State!.Observations[Entity];
        Assert.Equal(saved.Select(Describe), observations.Select(Describe));
    }

    [Fact]
    public void An_absent_file_starts_empty()
    {
        var loaded = ((IPhaseEvidenceFile)File()).Load();

        Assert.Null(loaded.State);
        Assert.NotEqual(StateFileStatus.ResetCorrupt, loaded.Status);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schema_version":2,"saved_at":"2026-10-25T09:00:00Z","entities":{}}""")]
    [InlineData("""{"schema_version":1,"saved_at":"2026-10-25T09:00:00Z","entities":{"switch.grzalka_1":[{"at":"x"}]}}""")]
    [InlineData("""{"schema_version":1,"saved_at":"2026-10-25T09:00:00Z","entities":{},"extra":1}""")]
    public void A_corrupt_file_is_set_aside_and_the_store_starts_empty(string content)
    {
        Directory.CreateDirectory(_directory);
        System.IO.File.WriteAllText(Path.Combine(_directory, PhaseEvidenceFile.Name), content);

        var loaded = ((IPhaseEvidenceFile)File()).Load();

        Assert.Equal((StateFileStatus.ResetCorrupt, (PhaseEvidenceState?)null), (loaded.Status, loaded.State));
        Assert.NotNull(loaded.Warning);
        Assert.True(System.IO.File.Exists(Path.Combine(_directory, PhaseEvidenceFile.Name + ".corrupt")));
    }

    [Fact]
    public async Task Adding_an_observation_never_writes_on_the_callers_thread_and_the_writer_keeps_it()
    {
        var store = new PhaseEvidenceStore(20);
        var file = new RecordingFile();
        var persistence = new PhaseEvidencePersistence(store, file, new SignalClock(new SteppableTime(At)), NullLogger<PhaseEvidencePersistence>.Instance);
        persistence.Load();

        store.Add(Observation(At, 1, ObservationSource.Active));
        Assert.Empty(file.Saved);

        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Single(file.Saved);
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Single(file.Saved);

        var restarted = new PhaseEvidenceStore(20);
        var load = new RecordingFile { Loaded = file.Saved[^1] };
        new PhaseEvidencePersistence(restarted, load, new SignalClock(new SteppableTime(At)), NullLogger<PhaseEvidencePersistence>.Instance).Load();
        Assert.Single(restarted.For(Entity));
    }

    [Fact]
    public async Task A_disk_that_refuses_is_retried_with_the_next_change_and_never_throws()
    {
        var store = new PhaseEvidenceStore(20);
        var file = new RecordingFile { Fail = true };
        var persistence = new PhaseEvidencePersistence(store, file, new SignalClock(new SteppableTime(At)), NullLogger<PhaseEvidencePersistence>.Instance);
        persistence.Load();

        store.Add(Observation(At, 1, ObservationSource.Active));
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Empty(file.Saved);

        file.Fail = false;
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Single(file.Saved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static readonly EntityId Entity = EntityId.Parse("switch.grzalka_1");

    private PhaseEvidenceFile File() => new(_directory, DiscoveryLinkTests.Options(), NullLogger<PhaseEvidenceFile>.Instance);

    private static PhaseObservation Observation(DateTimeOffset at, int direction, ObservationSource source)
    {
        var terms = new[] { ("a", Phase.L1), ("b", Phase.L2), ("c", Phase.L3) }.ToImmutableDictionary(t => t.Item2, t => new MeterTerm(EntityId.Parse($"sensor.solaredge_m1_ac_power_{t.Item1}"), "W", "positive_export"));
        var phases = terms.Keys.ToImmutableDictionary(p => p, p => new PhaseStepMeasurement(400, 410, p == Phase.L2 ? 400 + (direction * 1700) : 405, 25));
        return new PhaseObservation(at, direction, Entity, source, "W", false, true, terms, phases, true, source == ObservationSource.Active ? null : "seen at run time");
    }

    private static string Describe(PhaseObservation o) =>
        $"{o.At:O} {o.Direction} {o.StateEntity} {o.Source} {o.Unit} {o.Unsigned} {o.HouseLoad} {o.Isolated} {o.Note} "
        + string.Join(",", o.Meter.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}"))
        + " " + string.Join(",", o.Phases.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    private sealed class RecordingFile : IPhaseEvidenceFile
    {
        public List<PhaseEvidenceState> Saved { get; } = [];

        public PhaseEvidenceState? Loaded { get; init; }

        public bool Fail { get; set; }

        public PhaseEvidenceLoad Load() => new(Loaded, Loaded is null ? StateFileStatus.Fresh : StateFileStatus.Loaded, null);

        public void Save(PhaseEvidenceState state)
        {
            if (Fail)
            {
                throw new IOException("disk full");
            }

            Saved.Add(state);
        }
    }
}
