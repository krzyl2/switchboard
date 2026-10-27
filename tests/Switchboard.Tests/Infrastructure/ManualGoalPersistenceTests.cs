using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// PLAN-19: the manual goals in state.json (schema 5). A goal the user asked for must survive a restart (a "charge now" must not be forgotten when the
/// add-on updates at night) and a cancelled one must not come back; a file of the earlier schemas must still read, as "no manual goals"; and a file a person
/// edited or a crash cut must be set aside as a whole, like every other part of state.json, never half trusted (an invented goal would start a charge nobody asked for).
/// </summary>
public sealed class ManualGoalPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");
    private static readonly DeviceName Boiler = DeviceName.Parse("boiler");

    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    private RuntimeStateFile File() => new(_data.Path, TestDecisions.Options(), NullLogger<RuntimeStateFile>.Instance);

    private static RuntimeState WithManual() => RuntimeState.Fresh with
    {
        SavedAt = At,
        ManualGoals = ImmutableDictionary<DeviceName, ManualGoal>.Empty
            .Add(Car, new ManualGoal(GoalKind.Energy, 8.5, At.AddHours(10), true, At.AddMinutes(-5), 2.25, At))
            .Add(Boiler, new ManualGoal(GoalKind.Soc, 60, At.AddHours(3), false, At.AddMinutes(-1), 0, At)),
    };

    [Fact]
    public void Manual_goals_round_trip_and_the_file_is_schema_5()
    {
        var file = File();

        file.Save(WithManual());
        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.Equal(WithManual().ManualGoals.OrderBy(g => g.Key), load.State.ManualGoals.OrderBy(g => g.Key));
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(file.FilePath));
        Assert.Equal(5, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(["kind", "target", "deadline", "hard", "since", "delivered_kwh", "updated_at"], json.RootElement.GetProperty("manual_goals").GetProperty("ev_charger").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_schema_4_file_reads_as_no_manual_goals_with_the_ledger_and_the_rest_intact()
    {
        // Why: the upgrade must not reset the kill switch, the budgets or the goal ledger because manual goals did not exist yet.
        var file = File();
        file.Save(WithManual() with
        {
            KillSwitch = true,
            Goals = ImmutableDictionary<DeviceName, GoalLedgerEntry>.Empty.Add(Car, new GoalLedgerEntry(GoalKind.Energy, At.AddHours(-1), null, 3, At)),
        });
        var schema4 = System.Text.RegularExpressions.Regex.Replace(
            System.IO.File.ReadAllText(file.FilePath).Replace("\"schema_version\":5", "\"schema_version\":4", StringComparison.Ordinal),
            ",\"manual_goals\":\\{.*\\}\\}$",
            "}");
        Assert.DoesNotContain("manual_goals", schema4, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, schema4);

        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.Empty(load.State.ManualGoals);
        Assert.Equal(3, load.State.Goals[Car].DeliveredKwh);
        Assert.True(load.State.KillSwitch);
    }

    [Fact]
    public void A_schema_4_file_cannot_carry_manual_goals()
    {
        // Why: the key set of each schema is exact; manual goals in a file that claims the older schema are an edited file.
        var file = File();
        file.Save(WithManual());
        System.IO.File.WriteAllText(file.FilePath, System.IO.File.ReadAllText(file.FilePath).Replace("\"schema_version\":5", "\"schema_version\":4", StringComparison.Ordinal));

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
        Assert.Empty(load.State.ManualGoals);
    }

    [Fact]
    public void A_schema_5_file_is_not_a_schema_4_file_to_an_older_reader_by_construction()
    {
        // Why: rollback safety. The earlier build accepts exactly the schema 4 key set, and the schema 5 file has one more key, so that build sets it
        // aside (as it would any untrusted file) instead of half-reading it: the key sets differ by exactly manual_goals.
        var file = File();
        file.Save(WithManual());

        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(file.FilePath));
        var keys = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal("manual_goals", Assert.Single(keys, k => k is not ("schema_version" or "saved_at" or "clean_shutdown" or "kill_switch" or "kill_switch_changed_at" or "phases" or "devices" or "mqtt" or "overrides" or "group_budgets" or "control" or "observation" or "start_baselines" or "self_started" or "goals")));
    }

    [Theory]
    [InlineData("\"kind\":\"energy\",\"target\":8.5", "\"kind\":\"watts\",\"target\":8.5")]
    [InlineData("\"target\":8.5", "\"target\":0")]
    [InlineData("\"target\":8.5", "\"target\":-3")]
    [InlineData("\"target\":8.5", "\"target\":2000000")]
    [InlineData("\"target\":8.5", "\"target\":\"8.5\"")]
    [InlineData("\"target\":8.5", "\"target\":1e999")]
    [InlineData("\"target\":60", "\"target\":160")]
    [InlineData("\"delivered_kwh\":2.25", "\"delivered_kwh\":-1")]
    [InlineData("\"delivered_kwh\":2.25", "\"delivered_kwh\":2000000")]
    [InlineData("\"hard\":true", "\"hard\":\"yes\"")]
    [InlineData("\"hard\":true", "\"hard\":null")]
    [InlineData("\"ev_charger\":{\"kind\"", "\"switch.charger\":{\"kind\"")]
    [InlineData("\"ev_charger\":{\"kind\"", "\"EV Charger\":{\"kind\"")]
    [InlineData("\"delivered_kwh\":2.25,", "\"delivered_kwh\":2.25,\"extra\":1,")]
    [InlineData("\"since\":\"2026-10-26T17:55:00.0000000Z\"", "\"since\":\"earlier\"")]
    [InlineData("\"deadline\":\"2026-10-27T04:00:00.0000000Z\"", "\"deadline\":null")]
    public void Untrusted_manual_goal_content_refuses_the_whole_file(string valid, string tampered)
    {
        // Why: an edited goal must never half apply: a made-up target or deadline would start a charge nobody asked for. Anything out of shape resets the
        // whole state, as for every other key.
        var file = File();
        file.Save(WithManual());
        var text = System.IO.File.ReadAllText(file.FilePath);
        Assert.Contains(valid, text, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, text.Replace(valid, tampered, StringComparison.Ordinal));

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
        Assert.Empty(load.State.ManualGoals);
        Assert.True(System.IO.File.Exists(file.FilePath + RuntimeStateFile.CorruptSuffix));
    }

    [Fact]
    public void A_soc_goal_cannot_carry_delivered_energy()
    {
        // Why: a SoC goal's progress is the SoC signal; a delivered figure in the file is not something this build wrote.
        var file = File();
        file.Save(WithManual());
        var text = System.IO.File.ReadAllText(file.FilePath);
        var tampered = text.Replace("\"target\":60,\"deadline\":\"2026-10-26T21:00:00.0000000Z\",\"hard\":false,\"since\":\"2026-10-26T17:59:00.0000000Z\",\"delivered_kwh\":0", "\"target\":60,\"deadline\":\"2026-10-26T21:00:00.0000000Z\",\"hard\":false,\"since\":\"2026-10-26T17:59:00.0000000Z\",\"delivered_kwh\":1", StringComparison.Ordinal);
        Assert.NotEqual(text, tampered);
        System.IO.File.WriteAllText(file.FilePath, tampered);

        Assert.Equal(StateFileStatus.ResetCorrupt, file.Load().Status);
    }

    [Fact]
    public void The_manual_goals_key_must_be_an_object()
    {
        var file = File();
        file.Save(WithManual());
        var text = System.IO.File.ReadAllText(file.FilePath);
        var start = text.IndexOf("\"manual_goals\":", StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, text[..start] + "\"manual_goals\":[]}");

        Assert.Equal(StateFileStatus.ResetCorrupt, file.Load().Status);
    }

    private sealed class Inputs(StoreHarness harness) : IGoalInputSource
    {
        public GoalInputView Current => new(harness.Configuration, GoalInputs.From(harness.Snapshot()));
    }

    private sealed class Rig
    {
        public Rig(RuntimeStateFile file)
        {
            Harness = new StoreHarness(TestDevices.Document(
                "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n"));
            Store = new ManualGoalStore(new Inputs(Harness), new SignalClock(Harness.Time), NullLogger<ManualGoalStore>.Instance);
            Coordinator = new RuntimeStateCoordinator(file, file.Load(), () => null, MsOptions.Create(TestDecisions.Options()), Harness.Time, NullLogger<RuntimeStateCoordinator>.Instance);
            Coordinator.Attach(Store);
        }

        public StoreHarness Harness { get; }

        public ManualGoalStore Store { get; }

        public RuntimeStateCoordinator Coordinator { get; }
    }

    [Fact]
    public async Task A_manual_goal_survives_a_restart_and_a_cancelled_one_does_not_come_back()
    {
        // Why: PLAN-19 end to end on the real file: begin, write at once, read in a fresh process and restore; cancel, write, and the next process starts clean.
        var file = File();
        var first = new Rig(file);
        first.Store.Begin(Car, GoalKind.Energy, 8, first.Harness.Now.AddHours(10), hard: true);
        first.Store.AddDelivered(Car, 1.25, first.Harness.Now);
        await first.Coordinator.FlushAsync(CancellationToken.None);

        var loaded = file.Load();
        var restored = Assert.Contains(Car, loaded.State.ManualGoals);
        Assert.Equal((GoalKind.Energy, 8.0, true, 1.25), (restored.Kind, restored.Target, restored.Hard, restored.DeliveredKwh));

        var second = new Rig(file);
        second.Store.Restore(loaded.State.ManualGoals);
        Assert.Equal(1.25, second.Store.Current.Goals[Car].DeliveredKwh);
        second.Store.End(Car);
        await second.Coordinator.FlushAsync(CancellationToken.None);

        Assert.Empty(file.Load().State.ManualGoals);
    }

    [Fact]
    public async Task A_goal_that_begins_or_ends_is_written_at_once_and_progress_goes_with_the_next_save()
    {
        // Why: a crash right after "want now" must not forget it, but writing state.json at every step would wear the disk: only the shape is
        // immediate, delivered energy rides the interval (and the clean stop).
        var store = new RecordingStore();
        var harness = new StoreHarness(TestDevices.Document(
            "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n"));
        var goals = new ManualGoalStore(new Inputs(harness), new SignalClock(harness.Time), NullLogger<ManualGoalStore>.Instance);
        var coordinator = new RuntimeStateCoordinator(store, new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.Fresh, null), () => null, MsOptions.Create(TestDecisions.Options()), TimeProvider.System, NullLogger<RuntimeStateCoordinator>.Instance);
        coordinator.Attach(goals);

        goals.Begin(Car, GoalKind.Energy, 8, harness.Now.AddHours(5), hard: false);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Single(store.Saved);
        Assert.Equal(0, store.Saved[0].ManualGoals[Car].DeliveredKwh);

        goals.AddDelivered(Car, 1, harness.Now);
        goals.AddDelivered(Car, 1, harness.Now);
        Assert.Single(store.Saved);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Equal(2, store.Saved.Count);
        Assert.Equal(2, store.Saved[1].ManualGoals[Car].DeliveredKwh);

        goals.End(Car);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Empty(store.Saved[^1].ManualGoals);
    }

    [Fact]
    public void The_coordinator_ignores_a_manual_snapshot_older_than_the_one_applied()
    {
        // Why: two changes from different threads (the API and the tracker's expiry) may arrive out of order; state.json must never keep an older set.
        var store = new RecordingStore();
        var coordinator = new RuntimeStateCoordinator(store, new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.Fresh, null), () => null, MsOptions.Create(TestDecisions.Options()), TimeProvider.System, NullLogger<RuntimeStateCoordinator>.Instance);
        var newer = new ManualGoalSnapshot(5, ImmutableDictionary<DeviceName, ManualGoal>.Empty.Add(Car, new ManualGoal(GoalKind.Energy, 8, At.AddHours(2), false, At, 3, At)));
        var older = new ManualGoalSnapshot(4, ImmutableDictionary<DeviceName, ManualGoal>.Empty);

        coordinator.OnManualGoalsChanged(newer);
        coordinator.OnManualGoalsChanged(older);

        Assert.Equal(3, coordinator.Current.ManualGoals[Car].DeliveredKwh);
    }

    private sealed class RecordingStore : IRuntimeStateStore
    {
        public List<RuntimeState> Saved { get; } = [];

        public RuntimeStateLoad Load() => new(RuntimeState.Fresh, StateFileStatus.Fresh, null);

        public void Save(RuntimeState state) => Saved.Add(state);
    }
}
