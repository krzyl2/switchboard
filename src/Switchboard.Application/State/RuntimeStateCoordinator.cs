using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Goals;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.State;

/// <summary>
/// Keeps the runtime state in memory and decides when it is written: at once on a kill switch or
/// control switch change, a budget threshold crossing, a budget drop of at least
/// StateBudgetSaveStep, the clean shutdown flag, a changed restore baseline or a changed discovery
/// ledger; otherwise at most every StateSaveInterval, and only when something changed. Writes go through a one-slot queue (a newer state supersedes an older
/// one) drained by <see cref="RunAsync"/>: the guard thread and the kill switch only ever touch
/// memory, so a slow or full disk can delay neither. A failed write is reported in
/// <see cref="Report"/> and retried after WriteRetryInterval; control continues.
/// </summary>
public sealed class RuntimeStateCoordinator : IDiscoveryLedger, ILastKnownStates
{
    private readonly IRuntimeStateStore _store;
    private readonly Func<BudgetParameters?> _thresholds;
    private readonly PersistenceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RuntimeStateCoordinator> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private RuntimeState _current;
    private RuntimeState? _lastQueued;
    private DateTimeOffset? _lastQueuedAt;
    private Pending? _pending;
    private long _version;
    private long _savedVersion;
    private long _overridesVersion = -1;
    private long _goalsVersion = -1;
    private long _manualGoalsVersion = -1;
    private string? _writeError;

    /// <param name="thresholds">The budget thresholds in force (null before a valid configuration), for crossing detection.</param>
    public RuntimeStateCoordinator(
        IRuntimeStateStore store,
        RuntimeStateLoad loaded,
        Func<BudgetParameters?> thresholds,
        IOptions<PersistenceOptions> options,
        TimeProvider time,
        ILogger<RuntimeStateCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        Loaded = loaded;
        _thresholds = thresholds;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _current = loaded.State;
    }

    /// <summary>What was found at start; applied to the guard, the kill switch and the mode before the first evaluation.</summary>
    public RuntimeStateLoad Loaded { get; }

    public RuntimeState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Raised on the writer's thread after a state was written to the store, with the state written
    /// (never on the guard thread, never under a lock). It lets an observer wait for a save without
    /// polling the file or a clock: a test of "written at once", or a diagnostic. A failing handler is
    /// logged and never stops the writer.
    /// </summary>
    public event EventHandler<RuntimeState>? Saved;

    public StateFileReport Report
    {
        get
        {
            lock (_gate)
            {
                return _writeError is { } error
                    ? new StateFileReport(Loaded.Status, $"state.json could not be written ({error}); the kill switch and the budgets hold in memory only until it can.", false)
                    : new StateFileReport(Loaded.Status, Loaded.Warning, true);
            }
        }
    }

    public ImmutableSortedSet<string> Discovered => Current.Discovered;

    public ImmutableDictionary<DeviceName, ImmutableArray<StageLastKnown?>> LastKnown => Current.Devices;

    /// <summary>Follows the guard and the kill switch. Call once, before the guard's first step.</summary>
    public void Attach(GuardRunner runner, KillSwitchState killSwitch)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(killSwitch);
        runner.Stepped += (_, step) => OnStepped(step);
        killSwitch.Changed += (_, status) => OnKillSwitchChanged(status);
    }

    /// <summary>Called on the guard thread after every step; touches memory only.</summary>
    public void OnStepped(GuardStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var at = step.Evaluation.At;
        lock (_gate)
        {
            var errors = step.Decisions.Where(d => d is { Scope: DecisionScope.System, Action: DecisionAction.SafeStateEntered }
                && d.Inputs.SafeStateCause is SafeStateCause.ConfigurationInvalid or SafeStateCause.ConfigurationMissing).ToList();
            var observation = _current.Observation is { } observed && errors.Count > 0
                ? observed with { ConfigurationErrors = observed.ConfigurationErrors + errors.Count, LastConfigurationErrorAt = errors[^1].At }
                : _current.Observation;
            var next = _current with
            {
                Budgets = step.Memory.Budgets,
                GroupBudgets = step.Memory.GroupBudgets,
                CleanShutdown = step.Memory.CleanShutdown,
                Devices = MergeLastKnown(_current.Devices, step.Evaluation.Status, at),
                StartBaselines = SameBaselines(_current.StartBaselines, step.Baselines) ? _current.StartBaselines : step.Baselines,
                SelfStarted = _current.SelfStarted.Count == step.SelfStarted.Count && _current.SelfStarted.All(s => step.SelfStarted.TryGetValue(s.Key, out var t) && t == s.Value) ? _current.SelfStarted : step.SelfStarted,
                Observation = observation,
            };
            _current = next;

            var baseline = _lastQueued;
            var immediate = baseline is null
                || baseline.CleanShutdown != next.CleanShutdown
                || !ReferenceEquals(baseline.StartBaselines, next.StartBaselines) && !SameBaselines(baseline.StartBaselines, next.StartBaselines)
                || !ReferenceEquals(baseline.SelfStarted, next.SelfStarted) && baseline.SelfStarted.Count != next.SelfStarted.Count
                || ThresholdCrossed(baseline.Budgets, next.Budgets)
                || BudgetDropped(baseline.Budgets, next.Budgets)
                || GroupBudgetAlarm(baseline.GroupBudgets, next.GroupBudgets);
            var due = baseline is not null
                && Changed(baseline, next)
                && (_lastQueuedAt is not { } last || at < last || at - last >= _options.StateSaveInterval);
            if (immediate || due)
            {
                Enqueue(next, at);
            }
        }
    }

    /// <summary>Follows the control switch (MODE-02): written at once, like the kill switch, so a crash after turning control off cannot restart with it on. Call once, before the first step.</summary>
    public void Attach(ControlSwitchState control)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Changed += (_, status) => OnControlChanged(status);
    }

    /// <summary>Called after the control switch changed in memory; the write is best effort.</summary>
    public void OnControlChanged(ControlSwitchStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            if (_current.Control == status)
            {
                return;
            }

            _current = _current with { Control = status };
            Enqueue(_current, status.ChangedAt ?? _lastQueuedAt);
        }
    }

    /// <summary>
    /// The first start of observing (a fresh install, or a state.json written before 0.7.0): when it
    /// began. Written with the next save; a value already known is kept.
    /// </summary>
    public void BeginObservation(DateTimeOffset firstStartedAt)
    {
        lock (_gate)
        {
            if (_current.Observation is not null)
            {
                return;
            }

            _current = _current with { Observation = new ObservationRecord(firstStartedAt, 0, null) };
            Enqueue(_current, _lastQueuedAt);
        }
    }

    /// <summary>Follows the manual overrides. Call once, before the first step.</summary>
    public void Attach(DeviceOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        overrides.Changed += (_, snapshot) => OnOverridesChanged(snapshot);
    }

    /// <summary>
    /// Called by the overrides after the change is in force; written at once, best effort. Two
    /// changes from different threads (the MQTT select and the API) may arrive out of order: a
    /// snapshot older than the last one applied is ignored, so state.json never keeps a stale set.
    /// </summary>
    public void OnOverridesChanged(OverrideSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (snapshot.Version <= _overridesVersion)
            {
                return;
            }

            _overridesVersion = snapshot.Version;
            _current = _current with { Overrides = snapshot.Devices };
            Enqueue(_current, snapshot.Devices.Values.Select(o => o.ChangedAt).Max() ?? _lastQueuedAt);
        }
    }

    /// <summary>Follows the goal ledger (PLAN-03). Call once, before the first step.</summary>
    public void Attach(GoalLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ledger.Changed += (_, snapshot) => OnGoalsChanged(snapshot);
    }

    /// <summary>
    /// Called by the ledger after a change. A goal that began, ended or moved its deadline is written at once (a restart must not
    /// forget a goal exists); progress alone goes to memory and is written with the next interval save, or at shutdown. A snapshot
    /// older than the last one applied is ignored.
    /// </summary>
    public void OnGoalsChanged(GoalLedgerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (snapshot.Version <= _goalsVersion)
            {
                return;
            }

            _goalsVersion = snapshot.Version;
            var structural = !SameGoalShape(_current.Goals, snapshot.Entries);
            _current = _current with { Goals = snapshot.Entries };
            if (structural)
            {
                Enqueue(_current, _lastQueuedAt);
            }
        }
    }

    /// <summary>Follows the manual goals (PLAN-19). Call once, before the first step.</summary>
    public void Attach(ManualGoalStore manualGoals)
    {
        ArgumentNullException.ThrowIfNull(manualGoals);
        manualGoals.Changed += (_, snapshot) => OnManualGoalsChanged(snapshot);
    }

    /// <summary>
    /// Called by the manual goal store after a change. A goal that begins or ends (or is replaced by another) is written at once, so a
    /// restart does not forget what the user asked for or bring back what was cancelled; delivered energy alone goes to memory and is written
    /// with the next interval save, or at shutdown. A snapshot older than the last one applied is ignored.
    /// </summary>
    public void OnManualGoalsChanged(ManualGoalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (snapshot.Version <= _manualGoalsVersion)
            {
                return;
            }

            _manualGoalsVersion = snapshot.Version;
            var structural = !SameManualGoalShape(_current.ManualGoals, snapshot.Goals);
            _current = _current with { ManualGoals = snapshot.Goals };
            if (structural)
            {
                Enqueue(_current, _lastQueuedAt);
            }
        }
    }

    private static bool SameManualGoalShape(
        ImmutableDictionary<Domain.Devices.DeviceName, Domain.Goals.ManualGoal> a,
        ImmutableDictionary<Domain.Devices.DeviceName, Domain.Goals.ManualGoal> b) =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var y) && x.Value.SameWish(y) && x.Value.Since == y.Since);

    private static bool SameGoalShape(
        ImmutableDictionary<Domain.Devices.DeviceName, Domain.Goals.GoalLedgerEntry> a,
        ImmutableDictionary<Domain.Devices.DeviceName, Domain.Goals.GoalLedgerEntry> b) =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var y) && (x.Value.Kind, x.Value.Since, x.Value.Deadline) == (y.Kind, y.Since, y.Deadline));

    /// <summary>Called by the kill switch after it is already in force; the write is best effort.</summary>
    public void OnKillSwitchChanged(KillSwitchStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _current = _current with { KillSwitch = status.Engaged, KillSwitchChangedAt = status.ChangedAt };
            Enqueue(_current, status.ChangedAt ?? _lastQueuedAt);
        }
    }

    public void Replace(IEnumerable<string> discovered)
    {
        var set = ImmutableSortedSet.CreateRange(StringComparer.Ordinal, discovered);
        lock (_gate)
        {
            if (set.SetEquals(_current.Discovered))
            {
                return;
            }

            _current = _current with { Discovered = set };
            Enqueue(_current, _lastQueuedAt);
        }
    }

    /// <summary>The writer: one write per wake-up, the latest state only; after a failure, retries on its own clock.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (WriteFailing)
                {
                    await Task.Delay(_options.WriteRetryInterval, _time, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }

                await WritePendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping; the final state is written by FlushAsync.
        }
    }

    /// <summary>Writes whatever is queued now, once; used at shutdown after the shutdown safe state.</summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Goal progress moves every step but is saved on the interval: the last of it is queued here, so a clean stop loses none.
            if (_lastQueued is { } queued && (!ReferenceEquals(queued.Goals, _current.Goals) || !ReferenceEquals(queued.ManualGoals, _current.ManualGoals)))
            {
                Enqueue(_current, _lastQueuedAt);
            }
        }

        return WritePendingAsync(cancellationToken);
    }

    private bool WriteFailing
    {
        get
        {
            lock (_gate)
            {
                return _writeError is not null;
            }
        }
    }

    private async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Pending? pending;
            lock (_gate)
            {
                pending = _pending;
            }

            if (pending is null || pending.Version <= _savedVersion)
            {
                return;
            }

            try
            {
                _store.Save(pending.State);
                lock (_gate)
                {
                    _savedVersion = pending.Version;
                    if (_pending?.Version == pending.Version)
                    {
                        _pending = null;
                    }

                    if (_writeError is not null)
                    {
                        _logger.LogInformation("state.json is writable again");
                        _writeError = null;
                    }
                }

                NotifySaved(pending.State);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    if (_writeError is null)
                    {
                        // Once per failure streak: a full disk must not flood the log every retry.
                        _logger.LogError("state.json could not be written: {Reason}; retrying every {Interval}", ex.Message, _options.WriteRetryInterval);
                    }

                    _writeError = ex.Message;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void NotifySaved(RuntimeState state)
    {
        foreach (var handler in Saved?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<RuntimeState>)handler)(this, state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A listener of the saved state failed");
            }
        }
    }

    private void Enqueue(RuntimeState state, DateTimeOffset? at)
    {
        var stamped = state with { SavedAt = at ?? state.SavedAt };
        _pending = new Pending(stamped, ++_version);
        _lastQueued = stamped;
        _lastQueuedAt = at ?? _lastQueuedAt;
        _wake.Writer.TryWrite(true);
    }

    private bool ThresholdCrossed(ImmutableDictionary<Phase, BudgetMemory> before, ImmutableDictionary<Phase, BudgetMemory> after)
    {
        if (_thresholds() is not { } thresholds)
        {
            return false;
        }

        foreach (var (phase, now) in after)
        {
            var was = before.TryGetValue(phase, out var saved) ? saved.Budget : 1;
            if (Category(was, thresholds) != Category(now.Budget, thresholds))
            {
                return true;
            }
        }

        return false;
    }

    private static int Category(double budget, BudgetParameters thresholds) =>
        OverloadBudget.IsAtOrBelow(budget, thresholds.TopTierAtOrBelow) ? 3
        : OverloadBudget.IsBelow(budget, thresholds.ShedOneBelow) ? 2
        : OverloadBudget.IsBelow(budget, thresholds.StopIncreasesBelow) ? 1
        : 0;

    private bool BudgetDropped(ImmutableDictionary<Phase, BudgetMemory> before, ImmutableDictionary<Phase, BudgetMemory> after) =>
        after.Any(p => (before.TryGetValue(p.Key, out var saved) ? saved.Budget : 1) - p.Value.Budget >= _options.StateBudgetSaveStep - OverloadBudget.Tolerance);

    /// <summary>The immediate-save rules of the main budgets applied to every circuit-group phase (CONN-03: the same model, the same crash bound).</summary>
    private bool GroupBudgetAlarm(
        ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>> before,
        ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>> after) =>
        after.Any(g =>
        {
            var saved = before.GetValueOrDefault(g.Key) ?? ImmutableDictionary<Phase, BudgetMemory>.Empty;
            return ThresholdCrossed(saved, g.Value) || BudgetDropped(saved, g.Value);
        });

    private static bool SameBaselines(ImmutableDictionary<DeviceName, Effector.StartBaseline> a, ImmutableDictionary<DeviceName, Effector.StartBaseline> b) =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var y) && y == x.Value);

    private static bool Changed(RuntimeState before, RuntimeState after) =>
        before.KillSwitch != after.KillSwitch
        || before.Observation != after.Observation
        || before.Budgets.Count != after.Budgets.Count
        || after.Budgets.Any(p => !before.Budgets.TryGetValue(p.Key, out var saved) || saved.Budget != p.Value.Budget)
        || GroupBudgetsChanged(before, after)
        || !ReferenceEquals(before.Devices, after.Devices)
        || !ReferenceEquals(before.Goals, after.Goals)
        || !ReferenceEquals(before.ManualGoals, after.ManualGoals);

    private static bool GroupBudgetsChanged(RuntimeState before, RuntimeState after) =>
        before.GroupBudgets.Count != after.GroupBudgets.Count
        || after.GroupBudgets.Any(g => !before.GroupBudgets.TryGetValue(g.Key, out var saved)
            || saved.Count != g.Value.Count
            || g.Value.Any(p => !saved.TryGetValue(p.Key, out var b) || b.Budget != p.Value.Budget));

    /// <summary>
    /// Records each stage's latest fresh applied state with the moment it was first seen (so an
    /// unchanged state changes nothing). Devices no longer declared are forgotten only once a valid
    /// configuration says so.
    /// </summary>
    private static ImmutableDictionary<DeviceName, ImmutableArray<StageLastKnown?>> MergeLastKnown(
        ImmutableDictionary<DeviceName, ImmutableArray<StageLastKnown?>> known,
        GuardStatus status,
        DateTimeOffset at)
    {
        var result = known;
        if (status.Breaker is not null)
        {
            foreach (var gone in known.Keys.Where(k => status.Devices.All(d => d.Id != k)).ToList())
            {
                result = result.Remove(gone);
            }
        }

        foreach (var device in status.Devices)
        {
            var previous = result.TryGetValue(device.Id, out var stages) ? stages : [];
            var merged = new StageLastKnown?[device.Stages.Length];
            var changed = previous.Length != merged.Length;
            for (var i = 0; i < merged.Length; i++)
            {
                var before = i < previous.Length ? previous[i] : null;
                var stage = device.Stages[i];
                merged[i] = stage is { Fresh: true, Applied: { } applied } && before?.Applied != applied
                    ? new StageLastKnown(applied, at)
                    : before;
                changed |= !Equals(merged[i], before);
            }

            if (changed)
            {
                result = result.SetItem(device.Id, [.. merged]);
            }
        }

        return result;
    }

    private sealed record Pending(RuntimeState State, long Version);
}
