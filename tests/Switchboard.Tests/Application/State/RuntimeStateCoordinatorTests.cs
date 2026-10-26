using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.State;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.State;

/// <summary>
/// When state.json is written decides what a crash can lose. A kill switch change, a budget
/// threshold crossing or a large budget drop are written at once; small drifts at most once per
/// StateSaveInterval. The guard and the kill switch never wait for the disk: they only hand the
/// coordinator a value, and a failed write is reported, not raised.
/// </summary>
public sealed class RuntimeStateCoordinatorTests
{
    private readonly GuardScenario _scenario = new(TestDevices.Document());
    private readonly MemoryStateStore _store = new();

    [Fact]
    public async Task First_step_after_start_writes_clean_shutdown_false()
    {
        // Written at every start: if this run crashes, the next start must not credit the downtime.
        var coordinator = Coordinator(RuntimeState.Fresh with { CleanShutdown = true });

        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.False(Assert.Single(_store.Saved).CleanShutdown);
    }

    [Fact]
    public async Task Kill_switch_change_is_written_at_once()
    {
        var coordinator = Coordinator();
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        coordinator.OnKillSwitchChanged(new KillSwitchStatus(true, _scenario.Now, KillSwitchSource.Api));
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.True(_store.Saved[^1].KillSwitch);
        Assert.Equal(_scenario.Now, _store.Saved[^1].KillSwitchChangedAt);
    }

    [Fact]
    public async Task A_save_is_reported_with_the_state_written_and_a_failed_one_is_not()
    {
        // Observers (the control endpoint test) wait for this event instead of polling the file.
        var coordinator = Coordinator();
        var reported = new List<RuntimeState>();
        coordinator.Saved += (_, written) => reported.Add(written);
        Step(coordinator);

        _store.Fail = true;
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Empty(reported);

        _store.Fail = false;
        coordinator.OnControlChanged(new ControlSwitchStatus(true, _scenario.Now, ControlSwitchSource.Api, false, null));
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.True(Assert.Single(reported).Control.Allowed);
        Assert.Equal(reported, _store.Saved);
    }

    [Fact]
    public async Task A_failing_listener_of_the_saved_state_does_not_stop_the_writer()
    {
        var coordinator = Coordinator();
        coordinator.Saved += (_, _) => throw new InvalidOperationException("listener");
        Step(coordinator);

        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Single(_store.Saved);
    }

    [Fact]
    public async Task Budget_threshold_crossing_is_written_at_once()
    {
        var coordinator = Coordinator();
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        // 1.32 × In consumes 1/30 per second from the previous report: below 0.5 after 15 s.
        _scenario.Time.Advance(TimeSpan.FromSeconds(1));
        _scenario.SampleMultiple(Phase.L1, 1.32);
        _scenario.Time.Advance(TimeSpan.FromSeconds(15));
        _scenario.Evaluate(GuardTrigger.Tick);
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.True(_store.Saved[^1].Budgets[Phase.L1].Budget < _scenario.Setup.Budget.StopIncreasesBelow);
    }

    [Fact]
    public async Task Budget_drop_of_a_save_step_is_written_at_once()
    {
        // 1.20 × In consumes 1/180 per second (above the continuous 1.13): 8 s cost less than
        // StateBudgetSaveStep (0.05), 10 s more, and no threshold is crossed on the way.
        var coordinator = Coordinator();
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);
        _scenario.Time.Advance(TimeSpan.FromSeconds(1));
        _scenario.SampleMultiple(Phase.L1, 1.20);

        _scenario.Time.Advance(TimeSpan.FromSeconds(7));
        _scenario.Evaluate(GuardTrigger.Tick);
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Single(_store.Saved);

        _scenario.Time.Advance(TimeSpan.FromSeconds(2));
        _scenario.Evaluate(GuardTrigger.Tick);
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Equal(2, _store.Saved.Count);
        Assert.Equal(1 - (10.0 / 180), _store.Saved[^1].Budgets[Phase.L1].Budget, 9);
    }

    [Fact]
    public async Task Circuit_group_budget_drop_or_crossing_is_written_at_once()
    {
        // CONN-03 groups use the main breaker's model, so they get its crash bound too: a group
        // budget that drops by StateBudgetSaveStep or crosses a threshold is written in the same
        // step (an unclean restart must not restore the fuller budget of before the overload),
        // even while every main budget is unchanged. Smaller drifts wait for the save interval.
        var coordinator = Coordinator();
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);
        var main = _scenario.Engine.Export().Budgets;
        var stop = _scenario.Setup.Budget.StopIncreasesBelow;

        GroupStep(coordinator, main, stop + 0.02);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Equal(2, _store.Saved.Count);

        GroupStep(coordinator, main, stop - 0.01);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Equal(3, _store.Saved.Count);
        Assert.Equal(stop - 0.01, _store.Saved[^1].GroupBudgets[GroupName.Parse("carport")][Phase.L1].Budget, 9);
        Assert.Equal(main, _store.Saved[^1].Budgets);

        GroupStep(coordinator, main, stop - 0.03);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Equal(3, _store.Saved.Count);
    }

    [Fact]
    public async Task An_older_override_snapshot_arriving_late_never_overwrites_a_newer_one()
    {
        // The MQTT select and the API set overrides on different threads; their notifications may
        // arrive out of order. The newer set is what holds in memory, so it is what must be saved.
        var coordinator = Coordinator();
        var at = _scenario.Now;
        var older = new OverrideSnapshot(5, ImmutableDictionary<DeviceName, DeviceOverride>.Empty
            .Add(DeviceName.Parse("heater_l2"), new DeviceOverride(OverrideMode.ForceOff, at, OverrideSource.Mqtt)));
        var newer = new OverrideSnapshot(6, older.Devices
            .Add(DeviceName.Parse("ev_charger"), new DeviceOverride(OverrideMode.ForceOn, at, OverrideSource.Api)));

        coordinator.OnOverridesChanged(newer);
        coordinator.OnOverridesChanged(older);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(newer.Devices, coordinator.Current.Overrides);
        Assert.Equal(newer.Devices, _store.Saved[^1].Overrides);
    }

    [Fact]
    public async Task Other_changes_wait_for_the_save_interval()
    {
        // A device state seen off is worth keeping for display, but not a write per change.
        var coordinator = Coordinator();
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        _scenario.SetState("heater_l1", new RelayApplied(false));
        _scenario.Evaluate();
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.Single(_store.Saved);

        _scenario.Time.Advance(TestDecisions.Options().StateSaveInterval);
        _scenario.Evaluate(GuardTrigger.Tick);
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(2, _store.Saved.Count);
        Assert.Equal(new RelayApplied(false), _store.Saved[^1].Devices[DeviceName.Parse("heater_l1")][0]!.Applied);
    }

    [Fact]
    public async Task Clean_shutdown_is_written_after_the_shutdown_safe_state()
    {
        var coordinator = Coordinator();
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        _scenario.Evaluate(GuardTrigger.Shutdown);
        Step(coordinator);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.True(_store.Saved[^1].CleanShutdown);
    }

    [Fact]
    public async Task Unwritable_state_file_is_reported_and_never_raised()
    {
        // Fail-safe row "state.json unwritable": the kill switch is already in force in memory;
        // the failed write shows in the report and is retried, control continues.
        var coordinator = Coordinator();
        _store.Fail = true;

        coordinator.OnKillSwitchChanged(new KillSwitchStatus(true, _scenario.Now, KillSwitchSource.Api));
        await coordinator.FlushAsync(CancellationToken.None);

        var report = coordinator.Report;
        Assert.False(report.Writable);
        Assert.Contains("No space left on device", report.Warning, StringComparison.Ordinal);
        Assert.True(coordinator.Current.KillSwitch);

        _store.Fail = false;
        await coordinator.FlushAsync(CancellationToken.None);
        Assert.True(coordinator.Report.Writable);
        Assert.True(_store.Saved[^1].KillSwitch);
    }

    [Fact]
    public void Last_known_states_follow_fresh_states_only()
    {
        // Display only: a stale "off" must not overwrite what was last really seen.
        var coordinator = Coordinator();
        _scenario.SetState("heater_l1", new RelayApplied(false));
        _scenario.Evaluate();
        Step(coordinator);
        var seenAt = _scenario.Now;

        _scenario.Time.Advance(TimeSpan.FromMinutes(1));
        _scenario.SetState("heater_l1", null);
        _scenario.Evaluate();
        Step(coordinator);

        var heater = Assert.Single(coordinator.LastKnown[DeviceName.Parse("heater_l1")])!;
        Assert.Equal(new StageLastKnown(new RelayApplied(false), seenAt), heater);
        Assert.Equal(new SetpointApplied(new Amperes(20)), coordinator.LastKnown[DeviceName.Parse("ev_charger")][0]!.Applied);
    }

    [Fact]
    public async Task Discovery_ledger_changes_are_written()
    {
        var coordinator = Coordinator();

        coordinator.Replace(["sensor/l1_headroom", "switch/kill_switch"]);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(["sensor/l1_headroom", "switch/kill_switch"], _store.Saved[^1].Discovered);
    }

    private RuntimeStateCoordinator Coordinator(RuntimeState? loaded = null) => new(
        _store,
        new RuntimeStateLoad(loaded ?? RuntimeState.Fresh, StateFileStatus.Loaded, null),
        () => _scenario.Setup.Budget,
        MsOptions.Create(TestDecisions.Options()),
        TimeProvider.System,
        NullLogger<RuntimeStateCoordinator>.Instance);

    private void Step(RuntimeStateCoordinator coordinator)
    {
        if (_scenario.Evaluations.Count == 0)
        {
            _scenario.Evaluate();
        }

        coordinator.OnStepped(new GuardStep(_scenario.Last, [], _scenario.Engine.Export()));
    }

    private void GroupStep(RuntimeStateCoordinator coordinator, ImmutableDictionary<Phase, BudgetMemory> main, double budget) =>
        coordinator.OnStepped(new GuardStep(_scenario.Last, [], new GuardMemory(main, false)
        {
            GroupBudgets = ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>>.Empty
                .Add(GroupName.Parse("carport"), ImmutableDictionary<Phase, BudgetMemory>.Empty.Add(Phase.L1, new BudgetMemory(budget, _scenario.Now))),
        }));

    /// <summary>Keeps every saved state; can fail like a full disk.</summary>
    private sealed class MemoryStateStore : IRuntimeStateStore
    {
        public List<RuntimeState> Saved { get; } = [];

        public bool Fail { get; set; }

        public RuntimeStateLoad Load() => new(RuntimeState.Fresh, StateFileStatus.Fresh, null);

        public void Save(RuntimeState state)
        {
            if (Fail)
            {
                throw new IOException("No space left on device");
            }

            Saved.Add(state);
        }
    }
}
