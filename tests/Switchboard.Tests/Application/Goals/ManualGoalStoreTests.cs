using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.Goals;

/// <summary>
/// PLAN-19: the manual goal ("want now"). Why it matters: it is the only goal source a person sets at run time, so it must be
/// refused for anything that is not a declared device, must lapse on its own at its deadline (a forgotten "charge now" must not keep a
/// car charging next week), must outrank the same-deadline goal of a mapped entity and the file (and show the loser, not apply it),
/// and must count its own progress without disturbing the ledger of the file's goal on the same device. It stores data in the
/// add-on's state and writes no entity: nothing here can reach a command port.
/// </summary>
public sealed class ManualGoalStoreTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");
    private static readonly DeviceName Heater = DeviceName.Parse("heater_l1");

    private const string Tariff = "economics:\n  currency: PLN\n  tariff:\n    default_zone: offpeak\n    zones:\n      - id: offpeak\n        price: 0.60\n        when: []\n  settlement:\n    model: hourly_netting\n    export_price: 0.35\n";

    private const string HeaterBlock = "  - id: heater_l1\n    type: onoff\n    actuator: switch.heater_1\n    phase: L1\n    rated_current_a: 6.6\n    rated_power_w: 1500\n    value_per_kwh: 0.35\n";

    private const string CarBlock = "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n";

    private const string FileEnergyGoal = "    goal:\n      kind: energy\n      target_kwh: 12\n      loss_fraction: 0.10\n      deadline: { at: \"06:30\" }\n      want: { entity: input_boolean.car_wanted, max_age: 30m }\n";

    private const string FileSocGoal = "    goal:\n      kind: soc\n      capacity_kwh: 18\n      loss_fraction: 0.10\n      soc: { entity: sensor.car_battery_level, max_age: 30m }\n      target: { value: 90 }\n";

    private sealed class Inputs(StoreHarness harness) : IGoalInputSource
    {
        public GoalInputView Current => new(harness.Configuration, GoalInputs.From(harness.Snapshot()));
    }

    private sealed class Rig
    {
        public Rig(string carGoal = "")
        {
            Harness = new StoreHarness(TestDevices.Document(HeaterBlock + CarBlock + carGoal, extra: Tariff));
            Assert.True(Harness.Configuration.Status == Switchboard.Application.Configuration.ConfigurationStatus.Valid, string.Join("; ", Harness.Configuration.Errors));
            Harness.Time.SetLocalTimeZone(Warsaw);
            Inputs = new Inputs(Harness);
            Store = new ManualGoalStore(Inputs, new SignalClock(Harness.Time), NullLogger<ManualGoalStore>.Instance);
            Store.Changed += (_, snapshot) => Changes.Add(snapshot);
        }

        public StoreHarness Harness { get; }

        public Inputs Inputs { get; }

        public ManualGoalStore Store { get; }

        public List<ManualGoalSnapshot> Changes { get; } = [];

        public DateTimeOffset Now => Harness.Now;

        public GoalContext Context() =>
            new(Now, Harness.Configuration.Configuration!, GoalInputs.From(Harness.Snapshot()), Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance));

        public GoalBoard Board(params IGoalSource[] sources) => new GoalEvaluator([.. sources, Store]).Evaluate(Context());
    }

    [Fact]
    public void An_energy_goal_is_stored_and_stated_as_a_manual_request_with_the_loss_of_the_default()
    {
        // Why: "want now" is a goal like any other to the planner: the remaining need in kWh drawn, due at the deadline, with origin manual so
        // the precedence merge can rank it. A device without a goal block takes control.planner.goal.default_loss_fraction (0).
        var rig = new Rig();
        var deadline = rig.Now.AddHours(10);

        Assert.Equal(ManualGoalResult.Begun, rig.Store.Begin(Car, GoalKind.Energy, 8, deadline, hard: true));

        var request = Assert.Single(rig.Store.Goals(rig.Context()).Requests);
        Assert.Equal((Car, GoalKind.Energy, 8.0, 8.0, true, GoalOrigin.Manual), (request.Device, request.Kind, request.RemainingKwh, request.TargetKwh, request.Hard, request.Origin));
        Assert.Equal((deadline, rig.Now, true), (request.Deadline, request.Since, request.Escalate));
        Assert.Single(rig.Changes);
    }

    [Fact]
    public void The_loss_fraction_of_the_devices_own_goal_applies_to_its_manual_goal()
    {
        // Why: the car loses energy to the charger whoever asked; the user's "8 kWh" is energy in the battery.
        var rig = new Rig(FileEnergyGoal);

        rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(10), hard: false);

        Assert.Equal(8.8, Assert.Single(rig.Store.Goals(rig.Context()).Requests).RemainingKwh, 9);
    }

    [Fact]
    public void A_device_the_configuration_does_not_declare_is_refused_and_nothing_is_stored()
    {
        // Why: hard rule, never address what the user has not declared. An entity id, a typo and a removed device are all "not declared".
        var rig = new Rig();

        var result = rig.Store.Begin(DeviceName.Parse("garage_door"), GoalKind.Energy, 8, rig.Now.AddHours(1), hard: false);

        Assert.Equal(ManualGoalResult.UnknownDevice, result);
        Assert.Empty(rig.Store.Current.Goals);
        Assert.Empty(rig.Changes);
    }

    [Fact]
    public void Without_a_valid_configuration_nothing_can_be_stored()
    {
        // Why: there is nothing to check the device against; the API answers 409 and the state stays clean.
        var rig = new Rig();
        var store = new ManualGoalStore(new NoConfiguration(), new SignalClock(rig.Harness.Time), NullLogger<ManualGoalStore>.Instance);

        Assert.Equal(ManualGoalResult.NoConfiguration, store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(1), hard: false));
        Assert.Empty(store.Current.Goals);
    }

    private sealed class NoConfiguration : IGoalInputSource
    {
        public GoalInputView Current => new(null, GoalInputs.Empty);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1_000_001.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_energy_target_that_is_not_above_zero_or_is_absurd_is_refused(double target)
    {
        var rig = new Rig();

        Assert.Equal(ManualGoalResult.TargetOutOfRange, rig.Store.Begin(Car, GoalKind.Energy, target, rig.Now.AddHours(1), hard: false));
        Assert.Empty(rig.Store.Current.Goals);
    }

    [Fact]
    public void A_deadline_that_is_not_in_the_future_or_beyond_the_planning_horizon_is_refused()
    {
        // Why: a goal that has already lapsed is nothing to plan, and one beyond control.planner.horizon (48h) is a goal the plan cannot see.
        var rig = new Rig();

        Assert.Equal(ManualGoalResult.DeadlinePassed, rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now, hard: false));
        Assert.Equal(ManualGoalResult.DeadlinePassed, rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddMinutes(-5), hard: false));
        Assert.Equal(ManualGoalResult.DeadlineBeyondHorizon, rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(48).AddSeconds(1), hard: false));
        Assert.Equal(ManualGoalResult.Begun, rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(48), hard: false));
    }

    [Fact]
    public void A_soc_goal_needs_the_devices_own_goal_to_say_where_the_soc_is_read()
    {
        // Why: the manual goal never invents an entity: a percent of capacity is meaningless without the SoC signal and the capacity the
        // configuration declares. Such a device can still be asked for energy.
        var rig = new Rig();

        Assert.Equal(ManualGoalResult.SocNotConfigured, rig.Store.Begin(Car, GoalKind.Soc, 80, rig.Now.AddHours(5), hard: true));
        Assert.Equal(ManualGoalResult.SocNotConfigured, rig.Store.Begin(Heater, GoalKind.Soc, 80, rig.Now.AddHours(5), hard: true));
        Assert.Equal(ManualGoalResult.Begun, rig.Store.Begin(Car, GoalKind.Energy, 10, rig.Now.AddHours(5), hard: true));
    }

    [Fact]
    public void A_soc_goal_is_computed_from_the_soc_signal_and_inactive_with_the_reason_while_it_cannot_be_read()
    {
        // Why: never guessed. A stale or missing SoC makes the goal inactive with why; once it arrives the need is (target - soc) of the
        // capacity, with the loss.
        var rig = new Rig(FileSocGoal);
        Assert.Equal(ManualGoalResult.Begun, rig.Store.Begin(Car, GoalKind.Soc, 90, rig.Now.AddHours(10), hard: true));

        var before = rig.Store.Goals(rig.Context());
        Assert.Empty(before.Requests);
        var inactive = Assert.Single(before.Inactive);
        Assert.Equal((Car, GoalInactiveReason.InputMissing, GoalInputKind.Soc, GoalOrigin.Manual), (inactive.Device, inactive.Reason, inactive.Input, inactive.Origin));

        rig.Harness.Push("sensor.car_battery_level", "40");
        var request = Assert.Single(rig.Store.Goals(rig.Context()).Requests);

        Assert.Equal(9.9, request.RemainingKwh, 9);
        Assert.Equal((GoalKind.Soc, 90.0, 40.0, GoalOrigin.Manual), (request.Kind, request.TargetSoc, request.CurrentSoc, request.Origin));

        rig.Harness.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(GoalInactiveReason.InputStale, Assert.Single(rig.Store.Goals(rig.Context()).Inactive).Reason);
    }

    [Fact]
    public void A_goal_expires_at_its_deadline_and_is_removed_from_the_state()
    {
        // Why: a manual goal must never outlive the wish: at the deadline it is no longer offered, and the tracker's Expire ends it and tells the
        // persistence (Changed), so a restart does not bring it back.
        var rig = new Rig();
        var deadline = rig.Now.AddHours(2);
        rig.Store.Begin(Car, GoalKind.Energy, 8, deadline, hard: true);
        rig.Changes.Clear();

        rig.Harness.AdvanceTo(deadline.AddSeconds(-1));
        rig.Store.Expire(rig.Now);
        Assert.Single(rig.Store.Goals(rig.Context()).Requests);
        Assert.Empty(rig.Changes);

        rig.Harness.AdvanceTo(deadline);
        Assert.Empty(rig.Store.Goals(rig.Context()).Requests);
        rig.Store.Expire(rig.Now);

        Assert.Empty(rig.Store.Current.Goals);
        Assert.Empty(Assert.Single(rig.Changes).Goals);
    }

    [Fact]
    public void A_goal_of_a_device_that_is_no_longer_declared_is_dropped()
    {
        // Why: the configuration is edited while goals exist; a goal for a device that is gone must not linger or be offered.
        var rig = new Rig();
        rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(5), hard: false);
        var without = new Inputs(new StoreHarness(TestDevices.Document(HeaterBlock, extra: Tariff)));
        var store = new ManualGoalStore(without, new SignalClock(rig.Harness.Time), NullLogger<ManualGoalStore>.Instance);
        store.Restore(rig.Store.Current.Goals);

        store.Expire(rig.Now);

        Assert.Empty(store.Current.Goals);
    }

    [Fact]
    public void Cancelling_ends_the_goal_and_cancelling_nothing_is_not_found()
    {
        var rig = new Rig();
        rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(5), hard: false);
        rig.Changes.Clear();

        Assert.Equal(ManualGoalResult.Ended, rig.Store.End(Car));
        Assert.Equal(ManualGoalResult.NotFound, rig.Store.End(Car));

        Assert.Empty(rig.Store.Current.Goals);
        Assert.Single(rig.Changes);
    }

    [Fact]
    public void Asking_for_the_same_goal_again_changes_nothing_and_keeps_its_progress()
    {
        // Why: a double click must not reset what was already delivered nor wake the planner for nothing.
        var rig = new Rig();
        var deadline = rig.Now.AddHours(5);
        rig.Store.Begin(Car, GoalKind.Energy, 8, deadline, hard: false);
        rig.Store.AddDelivered(Car, 1.5, rig.Now);
        rig.Changes.Clear();

        Assert.Equal(ManualGoalResult.Unchanged, rig.Store.Begin(Car, GoalKind.Energy, 8, deadline, hard: false));

        Assert.Equal(1.5, rig.Store.Current.Goals[Car].DeliveredKwh);
        Assert.Empty(rig.Changes);
    }

    [Fact]
    public void A_different_goal_replaces_the_old_one_and_starts_from_zero()
    {
        var rig = new Rig();
        rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(5), hard: false);
        rig.Store.AddDelivered(Car, 1.5, rig.Now);

        Assert.Equal(ManualGoalResult.Begun, rig.Store.Begin(Car, GoalKind.Energy, 12, rig.Now.AddHours(5), hard: false));

        Assert.Equal((12.0, 0.0), (rig.Store.Current.Goals[Car].Target, rig.Store.Current.Goals[Car].DeliveredKwh));
    }

    [Fact]
    public void Progress_reduces_the_remaining_need_and_a_met_goal_is_stated_as_met()
    {
        var rig = new Rig();
        rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(5), hard: false);

        rig.Store.AddDelivered(Car, 3, rig.Now);
        Assert.Equal(5, Assert.Single(rig.Store.Goals(rig.Context()).Requests).RemainingKwh, 9);

        rig.Store.AddDelivered(Car, 6, rig.Now);
        var board = rig.Board();
        Assert.Equal(GoalStatus.Met, Assert.Single(board.Goals).Status);
    }

    [Fact]
    public void A_manual_goal_outranks_the_same_deadline_goal_of_a_mapped_entity_and_the_loser_is_shown_not_applied()
    {
        // Why: PLAN-19 and the precedence manual > module > entity > config: the person at the card is the last word for that deadline. The
        // entity goal is not applied but listed as set aside, so the screen can say why the helper's value does not rule.
        var rig = new Rig(FileEnergyGoal);
        rig.Harness.Push("input_boolean.car_wanted", "on");
        var entityDeadline = Assert.Single(new ConfigGoalSource().Goals(rig.Context()).Requests).Deadline!.Value;

        rig.Store.Begin(Car, GoalKind.Energy, 3, entityDeadline, hard: true);
        var board = rig.Board(new ConfigGoalSource());

        var applied = Assert.Single(board.Goals).Request;
        Assert.Equal((GoalOrigin.Manual, 3.0, true), (applied.Origin, applied.TargetKwh, applied.Hard));
        Assert.Equal(GoalOrigin.Entity, Assert.Single(board.Superseded).Origin);
    }

    [Fact]
    public void A_manual_goal_with_another_deadline_stands_beside_the_entity_goal()
    {
        // Why: the merge is per device and deadline (two trips in one day); "want now" for tonight does not delete tomorrow's recurring goal.
        var rig = new Rig(FileEnergyGoal);
        rig.Harness.Push("input_boolean.car_wanted", "on");
        var entityDeadline = Assert.Single(new ConfigGoalSource().Goals(rig.Context()).Requests).Deadline!.Value;

        rig.Store.Begin(Car, GoalKind.Energy, 3, entityDeadline.AddHours(-2), hard: true);
        var board = rig.Board(new ConfigGoalSource());

        Assert.Equal([GoalOrigin.Manual, GoalOrigin.Entity], board.Goals.Select(g => g.Request.Origin));
        Assert.Empty(board.Superseded);
    }

    [Fact]
    public void The_revision_follows_what_the_goal_is_and_not_its_progress()
    {
        // Why: a plan entry made for another revision is not applied; delivering energy must not retire the plan, changing the target must.
        var rig = new Rig();
        var deadline = rig.Now.AddHours(5);
        rig.Store.Begin(Car, GoalKind.Energy, 8, deadline, hard: false);
        var first = Assert.Single(rig.Store.Goals(rig.Context()).Requests).Revision;

        rig.Store.AddDelivered(Car, 2, rig.Now);
        var progressed = Assert.Single(rig.Store.Goals(rig.Context()).Requests).Revision;
        rig.Store.Begin(Car, GoalKind.Energy, 9, deadline, hard: false);
        var changed = Assert.Single(rig.Store.Goals(rig.Context()).Requests).Revision;

        Assert.Equal(first, progressed);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void The_tracker_counts_the_manual_goal_on_its_own_and_leaves_the_ledger_of_the_files_goal_alone()
    {
        // Why: one device, two goals (the file's recurring goal and a manual one). The energy the device draws counts toward each, but the
        // manual goal keeps its own count: it must not begin, move or end the ledger entry of the file's goal, and it expires through the
        // tracker's control step.
        var rig = new Rig(FileEnergyGoal);
        rig.Harness.Push("input_boolean.car_wanted", "on");
        var ledger = new GoalLedger(NullLogger<GoalLedger>.Instance);
        var tracker = new GoalTracker(rig.Inputs, new GoalEvaluator([new ConfigGoalSource(), rig.Store]), ledger, new SignalClock(rig.Harness.Time), MsOptions.Create(TestStatistics.Options()), NullLogger<GoalTracker>.Instance, rig.Store);
        var fileDeadline = Assert.Single(new ConfigGoalSource().Goals(rig.Context()).Requests).Deadline!.Value;
        var manualDeadline = rig.Now.AddHours(1);
        rig.Store.Begin(Car, GoalKind.Energy, 8, manualDeadline, hard: true);

        void Step(TimeSpan after)
        {
            rig.Harness.Advance(after);
            rig.Harness.Push("input_boolean.car_wanted", "on");
            tracker.Observe(TestSteps.View(rig.Now, 2300, 0, ("ev_charger", 2300, Switchboard.Application.Dispatch.PowerSource.Measured)));
        }

        Step(TimeSpan.Zero);
        Step(TimeSpan.FromMinutes(1));
        Step(TimeSpan.FromMinutes(1));

        var counted = 2 * 2300.0 / 60 / 1000;
        Assert.Equal(counted, rig.Store.Current.Goals[Car].DeliveredKwh, 9);
        Assert.Equal(counted, ledger.Entry(Car)!.DeliveredKwh, 9);
        Assert.Equal(fileDeadline, ledger.Entry(Car)!.Deadline);

        Step(TimeSpan.FromHours(1));
        Assert.Empty(rig.Store.Current.Goals);
        Assert.Equal(fileDeadline, ledger.Entry(Car)!.Deadline);
    }

    [Fact]
    public void A_manual_energy_goal_on_a_device_without_a_file_goal_does_not_start_a_ledger_entry()
    {
        // Why: the ledger belongs to the file's goals; a manual goal on a plain device must not create an entry the tracker would then end.
        var rig = new Rig();
        var ledger = new GoalLedger(NullLogger<GoalLedger>.Instance);
        var tracker = new GoalTracker(rig.Inputs, new GoalEvaluator([new ConfigGoalSource(), rig.Store]), ledger, new SignalClock(rig.Harness.Time), MsOptions.Create(TestStatistics.Options()), NullLogger<GoalTracker>.Instance, rig.Store);
        rig.Store.Begin(Car, GoalKind.Energy, 8, rig.Now.AddHours(5), hard: false);

        tracker.Observe(TestSteps.View(rig.Now, 2300, 0, ("ev_charger", 2300, Switchboard.Application.Dispatch.PowerSource.Measured)));
        rig.Harness.Advance(TimeSpan.FromMinutes(1));
        tracker.Observe(TestSteps.View(rig.Now, 2300, 0, ("ev_charger", 2300, Switchboard.Application.Dispatch.PowerSource.Measured)));

        Assert.Empty(ledger.Current.Entries);
        Assert.True(rig.Store.Current.Goals[Car].DeliveredKwh > 0);
    }
}
