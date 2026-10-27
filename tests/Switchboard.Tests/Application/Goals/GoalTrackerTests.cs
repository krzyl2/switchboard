using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Goals;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.Goals;

/// <summary>
/// PLAN-03: the goal ledger. An energy goal's progress is the controlled energy its device drew since the goal began, integrated per
/// dispatch step from the measured (else rated-model) power, and it has to survive a restart. Everything runs on a virtual clock; the
/// power comes from the same power pictures the statistics integrate.
/// </summary>
public sealed class GoalTrackerTests : IDisposable
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    private const string EnergyGoal = "      kind: energy\n      target_kwh: 12\n      loss_fraction: 0.10\n      deadline: { at: \"06:30\" }\n";
    private const string GatedGoal = "      kind: energy\n      target_kwh: 12\n      want: { entity: input_boolean.charge_wanted }\n";

    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    private static ConfigurationResult Config(string goal) =>
        TestConfigurations.Valid(TestDevices.Document(
            "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    goal:\n" + goal));

    private sealed class Inputs(ConfigurationResult? configuration) : IGoalInputSource
    {
        public ConfigurationResult? Configuration { get; set; } = configuration;

        public GoalInputs Samples { get; set; } = GoalInputs.Empty;

        public GoalInputView Current => new(Configuration, Samples);

        public void Want(string state) =>
            Samples = new GoalInputs(ImmutableDictionary<SignalName, GoalInputSample>.Empty.Add(
                SignalName.Parse("device_ev_charger_goal_want"),
                new GoalInputSample(SignalState.Fresh, SignalDetail.None, new StateValue(state))));
    }

    private sealed class Rig
    {
        public Rig(string goal, GoalLedger? ledger = null, DateTimeOffset? start = null)
        {
            Time = new FakeTimeProvider(start ?? Start);
            Time.SetLocalTimeZone(Warsaw);
            Source = new Inputs(Config(goal));
            Ledger = ledger ?? new GoalLedger(NullLogger<GoalLedger>.Instance);
            Tracker = new GoalTracker(Source, new GoalEvaluator([new ConfigGoalSource()]), Ledger, new SignalClock(Time), MsOptions.Create(TestStatistics.Options()), NullLogger<GoalTracker>.Instance);
        }

        public FakeTimeProvider Time { get; }

        public Inputs Source { get; }

        public GoalLedger Ledger { get; }

        public GoalTracker Tracker { get; }

        public DateTimeOffset Now => Time.GetUtcNow();

        /// <summary>One control step <paramref name="after"/> the last: the charger draws <paramref name="watts"/>.</summary>
        public void Step(TimeSpan after, double? watts = 2300, PowerSource source = PowerSource.Measured)
        {
            Time.Advance(after);
            Tracker.Observe(TestSteps.View(Now, watts ?? 0, 0, ("ev_charger", watts, source)));
        }

        /// <summary>A step stamped <paramref name="at"/>, whatever the clock says (a clock that steps back).</summary>
        public void StepAt(DateTimeOffset at, double? watts = 2300) =>
            Tracker.Observe(TestSteps.View(at, watts ?? 0, 0, ("ev_charger", watts, PowerSource.Measured)));

        public GoalLedgerEntry? Entry => Ledger.Entry(Car);
    }


    [Fact]
    public void The_ledger_counts_the_energy_a_device_draws_between_steps_at_the_power_the_step_held()
    {
        // Why: the goal is met by what the device really drew. 2300 W held for a minute is 2300 x 60 / 3600 / 1000 kWh, counted from the step
        // that began the goal (nothing before it belongs to it).
        var rig = new Rig(EnergyGoal);

        rig.Step(TimeSpan.Zero);
        Assert.Equal(0, rig.Entry!.DeliveredKwh);
        rig.Step(TimeSpan.FromMinutes(1));
        rig.Step(TimeSpan.FromMinutes(1));

        Assert.Equal(2 * 2300.0 / 60 / 1000, rig.Entry!.DeliveredKwh, 9);
        Assert.Equal(rig.Now, rig.Entry.UpdatedAt);
        var request = Assert.Single(rig.Tracker.Evaluate(rig.Now).Goals).Request;
        Assert.Equal((12 - rig.Entry.DeliveredKwh) * 1.1, request.RemainingKwh, 9);
    }

    [Fact]
    public void A_rated_model_power_counts_like_a_measured_one_and_an_unknown_power_counts_nothing()
    {
        // Why: the statistics count measured plus rated-model power as controlled energy; an unknown power is no energy (the ledger can only
        // run behind, which plans a little more rather than a goal that is falsely met).
        var rig = new Rig(EnergyGoal);
        rig.Step(TimeSpan.Zero, 1380, PowerSource.RatedModel);
        rig.Step(TimeSpan.FromMinutes(1), null);
        var modelled = rig.Entry!.DeliveredKwh;
        rig.Step(TimeSpan.FromMinutes(1), 1380, PowerSource.RatedModel);

        Assert.Equal(1380.0 / 60 / 1000, modelled, 9);
        Assert.Equal(modelled, rig.Entry!.DeliveredKwh, 9);
    }

    [Fact]
    public void A_gap_between_steps_longer_than_the_integration_gap_counts_nothing()
    {
        // Why: the same rule the statistics follow: a stretch nobody observed is never filled with a guess.
        var rig = new Rig(EnergyGoal);
        rig.Step(TimeSpan.Zero);

        rig.Step(TimeSpan.FromMinutes(3));

        Assert.Equal(0, rig.Entry!.DeliveredKwh);
    }

    [Fact]
    public void A_clock_that_steps_back_counts_nothing()
    {
        var rig = new Rig(EnergyGoal);
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        var counted = rig.Entry!.DeliveredKwh;

        rig.StepAt(rig.Now.AddMinutes(-5));

        Assert.Equal(counted, rig.Entry!.DeliveredKwh);
    }

    [Fact]
    public void The_energy_of_a_cycle_is_forgotten_when_its_deadline_has_passed_and_the_next_cycle_begins()
    {
        // Why: a recurring 06:30 goal is a new goal every morning. Yesterday's charge must not count toward today's, or tomorrow's goal would
        // start half met.
        var rig = new Rig(EnergyGoal);
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.True(rig.Entry!.DeliveredKwh > 0);
        var firstDeadline = rig.Entry.Deadline;

        // 05:29 UTC is 06:29 in Warsaw (CET): one minute before the deadline, still the same cycle; two minutes later it has passed.
        rig.Time.Advance(new DateTimeOffset(2026, 10, 27, 5, 28, 0, TimeSpan.Zero) - rig.Now);
        rig.Step(TimeSpan.FromMinutes(1), 0);
        Assert.Equal(firstDeadline, rig.Entry!.Deadline);
        rig.Step(TimeSpan.FromMinutes(2), 0);

        Assert.Equal(0, rig.Entry!.DeliveredKwh);
        Assert.True(rig.Entry.Deadline > firstDeadline);
        Assert.Equal(rig.Now, rig.Entry.Since);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("false")]
    public void A_goal_whose_want_gate_goes_off_ends_and_its_progress_goes_with_it(string off)
    {
        // Why: want is the user's "I no longer want this charge". If the ledger kept the energy, switching want on again later would resume
        // a goal the user had cancelled, half met.
        var rig = new Rig(GatedGoal);
        rig.Source.Want("on");
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.True(rig.Entry!.DeliveredKwh > 0);

        rig.Source.Want(off);
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.Null(rig.Entry);

        rig.Source.Want("on");
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.Equal(0, rig.Entry!.DeliveredKwh);
    }

    [Fact]
    public void A_goal_input_that_is_only_missing_does_not_end_the_goal_and_the_energy_keeps_counting()
    {
        // Why: a want entity that is briefly unavailable is not "no longer wanted". The device keeps drawing, and what it draws is still
        // delivered energy; only an explicit off ends the goal.
        var rig = new Rig(GatedGoal);
        rig.Source.Want("on");
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        var before = rig.Entry!.DeliveredKwh;

        rig.Source.Samples = GoalInputs.Empty;
        rig.Step(TimeSpan.FromMinutes(1));

        Assert.True(rig.Entry!.DeliveredKwh > before);
        Assert.Equal(GoalInactiveReason.InputMissing, Assert.Single(rig.Tracker.Evaluate(rig.Now).Inactive).Reason);
    }

    [Fact]
    public void A_goal_removed_from_a_valid_configuration_ends_but_an_invalid_one_forgets_nothing()
    {
        // Why: "forgotten only once a valid configuration says so" (the coordinator's rule for devices). A typo in the file must not wipe a
        // night's charge; a goal the user really removed must not linger forever.
        var rig = new Rig(EnergyGoal);
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        var kept = rig.Entry;

        rig.Source.Configuration = ConfigurationResult.Failed(ConfigurationStatus.Invalid, [], "/config/switchboard.yaml", rig.Now);
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.NotNull(rig.Entry);

        rig.Source.Configuration = TestConfigurations.Valid(TestDevices.Document());
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.Null(rig.Entry);
        Assert.NotNull(kept);
    }

    [Fact]
    public void A_deadline_put_back_before_it_passes_keeps_the_energy_already_delivered()
    {
        // Why: moving a departure from 07:00 to 07:30 is the same trip; the ledger must follow the deadline, not restart.
        var rig = new Rig(EnergyGoal);
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        var delivered = rig.Entry!.DeliveredKwh;

        rig.Ledger.Retarget(Car, rig.Entry.Deadline!.Value.AddMinutes(30));
        rig.Step(TimeSpan.FromMinutes(1));

        Assert.True(rig.Entry!.DeliveredKwh > delivered);
    }

    [Fact]
    public void A_step_without_a_power_picture_changes_nothing_and_a_failure_never_reaches_the_caller()
    {
        // Why: the tracker is a read-only listener of the control loop; whatever goes wrong in it must not reach the guard.
        var rig = new Rig(EnergyGoal);
        rig.Tracker.Observe(null);
        Assert.Null(rig.Entry);

        rig.Source.Configuration = null;
        rig.Tracker.Observe(TestSteps.View(rig.Now, 0, 0));

        Assert.Null(rig.Entry);
    }

    [Fact]
    public async Task The_ledger_survives_a_restart_and_goes_on_from_what_state_json_held()
    {
        // Why: PLAN-03: a restart in the middle of a night's charge must not forget what was delivered, or the planner would charge the whole
        // target again. Run end to end on the virtual clock: count, write state.json at the clean stop, read it in a fresh process, go on.
        var file = new RuntimeStateFile(_data.Path, TestDecisions.Options(), NullLogger<RuntimeStateFile>.Instance);
        var first = new Rig(EnergyGoal);
        var coordinator = new RuntimeStateCoordinator(file, file.Load(), () => null, MsOptions.Create(TestDecisions.Options()), TimeProvider.System, NullLogger<RuntimeStateCoordinator>.Instance);
        coordinator.Attach(first.Ledger);
        first.Step(TimeSpan.Zero);
        for (var i = 0; i < 10; i++)
        {
            first.Step(TimeSpan.FromSeconds(30));
        }

        var counted = first.Entry!.DeliveredKwh;
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Equal(10 * 30 * 2300.0 / 3600 / 1000, counted, 9);

        var loaded = file.Load();
        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        var restoredEntry = loaded.State.Goals[Car];
        Assert.Equal(counted, restoredEntry.DeliveredKwh, 9);
        Assert.Equal(first.Entry.Since, restoredEntry.Since);
        Assert.Equal(first.Entry.Deadline, restoredEntry.Deadline);

        var restartedLedger = new GoalLedger(NullLogger<GoalLedger>.Instance);
        restartedLedger.Restore(loaded.State.Goals);
        var second = new Rig(EnergyGoal, restartedLedger, first.Now.AddMinutes(10));
        Assert.Equal((12 - counted) * 1.1, Assert.Single(second.Tracker.Evaluate(second.Now).Goals).Request.RemainingKwh, 9);

        // The first step after a start has no previous step to integrate from: it counts nothing and keeps what was restored.
        second.Step(TimeSpan.Zero);
        Assert.Equal(counted, second.Entry!.DeliveredKwh, 9);
        second.Step(TimeSpan.FromSeconds(30));
        Assert.Equal(counted + 30 * 2300.0 / 3600 / 1000, second.Entry!.DeliveredKwh, 9);
    }

    [Fact]
    public async Task A_goal_that_begins_or_ends_is_written_at_once_and_progress_goes_with_the_next_save()
    {
        // Why: a crash right after a goal began must not forget it exists, but writing state.json at every step would wear the disk: only
        // the shape of the ledger is immediate, progress rides the interval (and the clean stop).
        var store = new RecordingStore();
        var rig = new Rig(EnergyGoal);
        var coordinator = new RuntimeStateCoordinator(store, new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.Fresh, null), () => null, MsOptions.Create(TestDecisions.Options()), TimeProvider.System, NullLogger<RuntimeStateCoordinator>.Instance);
        coordinator.Attach(rig.Ledger);

        rig.Step(TimeSpan.Zero);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Single(store.Saved);
        Assert.Equal(0, store.Saved[0].Goals[Car].DeliveredKwh);

        rig.Step(TimeSpan.FromMinutes(1));
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.Single(store.Saved);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(2, store.Saved.Count);
        Assert.Equal(2 * 2300.0 / 60 / 1000, store.Saved[1].Goals[Car].DeliveredKwh, 9);
    }

    [Fact]
    public void The_ledger_ignores_a_snapshot_older_than_the_one_applied()
    {
        // Why: two changes from different threads may arrive out of order; state.json must never keep an older ledger.
        var store = new RecordingStore();
        var coordinator = new RuntimeStateCoordinator(store, new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.Fresh, null), () => null, MsOptions.Create(TestDecisions.Options()), TimeProvider.System, NullLogger<RuntimeStateCoordinator>.Instance);
        var newer = new GoalLedgerSnapshot(5, ImmutableDictionary<DeviceName, GoalLedgerEntry>.Empty.Add(Car, new GoalLedgerEntry(GoalKind.Energy, Start, null, 3, Start)));
        var older = new GoalLedgerSnapshot(4, ImmutableDictionary<DeviceName, GoalLedgerEntry>.Empty);

        coordinator.OnGoalsChanged(newer);
        coordinator.OnGoalsChanged(older);

        Assert.Equal(3, coordinator.Current.Goals[Car].DeliveredKwh);
    }

    private sealed class RecordingStore : IRuntimeStateStore
    {
        public List<RuntimeState> Saved { get; } = [];

        public RuntimeStateLoad Load() => new(RuntimeState.Fresh, StateFileStatus.Fresh, null);

        public void Save(RuntimeState state) => Saved.Add(state);
    }
}
