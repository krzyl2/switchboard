using System.Collections.Immutable;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.State;

/// <summary>The last applied state the guard saw for one device stage; for display only, never a protection input.</summary>
public sealed record StageLastKnown(AppliedState Applied, DateTimeOffset At);

/// <summary>
/// When the installation started observing and how often its configuration was invalid: advice
/// for the UI ("observing for N days"), never enforced (maintainer survey 2026-10-25).
/// </summary>
/// <param name="ConfigurationErrors">System-wide safe states entered because the configuration was invalid or missing.</param>
public sealed record ObservationRecord(DateTimeOffset FirstStartedAt, int ConfigurationErrors, DateTimeOffset? LastConfigurationErrorAt);

/// <summary>
/// What survives a restart (/data/state.json, schema version 5; versions 1 and 2 of 0.3.x … 0.6.x
/// read with the control switch off, version 3 of 0.7.x … 0.10.x reads with no goal ledger, version 4 reads with no manual goals). Lockout timers are deliberately absent, and so are dispatch
/// targets, deferred intents, settling windows and unresponsive flags: every lockout restarts from
/// the restart instant, never from a persisted expiry (SAFE-07).
/// </summary>
/// <param name="CleanShutdown">True only when the previous run recorded its shutdown safe state; written false at every start.</param>
/// <param name="Budgets">Overload budget per phase with the moment it held that value.</param>
/// <param name="Devices">Last known applied state per device stage (index = stage number − 1; null when never seen).</param>
/// <param name="Discovered">MQTT discovery objects published so far (<c>component/object_id</c>), so removed ones are retracted.</param>
/// <param name="UiMode">The Stage 5 UI choice of a schema 1 or 2 file: read and ignored (the control switch replaced it, off after the upgrade); never written.</param>
public sealed record RuntimeState(
    DateTimeOffset? SavedAt,
    bool CleanShutdown,
    bool KillSwitch,
    DateTimeOffset? KillSwitchChangedAt,
    OperatingMode UiMode,
    ImmutableDictionary<Phase, BudgetMemory> Budgets,
    ImmutableDictionary<DeviceName, ImmutableArray<StageLastKnown?>> Devices,
    ImmutableSortedSet<string> Discovered)
{
    public const int SchemaVersion = 5;

    /// <summary>The schema of the goal ledger without manual goals (planner slices 1 to 6, unreleased): still read, no manual goals.</summary>
    public const int LedgerSchemaVersion = 4;

    /// <summary>The 0.7.x … 0.10.x schema: still read (no goal ledger).</summary>
    public const int GoalFreeSchemaVersion = 3;

    /// <summary>The 0.4.x … 0.6.x schema: still read (the control switch reads off).</summary>
    public const int PreviousSchemaVersion = 2;

    /// <summary>The 0.3.x schema: still read (no overrides, no group budgets, control off).</summary>
    public const int LegacySchemaVersion = 1;

    /// <summary>A first start, or a corrupt file: full budgets, kill switch off, UI choice shadow, nothing discovered.</summary>
    public static RuntimeState Fresh { get; } = new(
        null,
        false,
        false,
        null,
        OperatingMode.Shadow,
        ImmutableDictionary<Phase, BudgetMemory>.Empty,
        ImmutableDictionary<DeviceName, ImmutableArray<StageLastKnown?>>.Empty,
        ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal));

    /// <summary>The manual overrides (SAFE-04): they survive a restart; protection outranks them.</summary>
    public ImmutableDictionary<DeviceName, DeviceOverride> Overrides { get; init; } = ImmutableDictionary<DeviceName, DeviceOverride>.Empty;

    /// <summary>The overload budget of every circuit group phase (CONN-03).</summary>
    public ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>> GroupBudgets { get; init; } =
        ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>>.Empty;

    /// <summary>The global "control allowed" switch (default off) and the lock of a corrupt start.</summary>
    public ControlSwitchStatus Control { get; init; } = ControlSwitchStatus.Default;

    /// <summary>When observing began and how often the configuration was invalid; null until set (a first start, or a file written before 0.7.0).</summary>
    public ObservationRecord? Observation { get; init; }

    /// <summary>The commanded restore baselines (start: restore_only), so a restart does not forget what to restore.</summary>
    public ImmutableDictionary<DeviceName, StartBaseline> StartBaselines { get; init; } = ImmutableDictionary<DeviceName, StartBaseline>.Empty;

    /// <summary>The devices dispatch switched on under start: headroom (only these get a final off when removed).</summary>
    public ImmutableDictionary<DeviceName, DateTimeOffset> SelfStarted { get; init; } = ImmutableDictionary<DeviceName, DateTimeOffset>.Empty;

    /// <summary>The goal ledger (PLAN-03): the energy each energy goal's device has drawn since the goal began; empty in a schema 3 file.</summary>
    public ImmutableDictionary<DeviceName, GoalLedgerEntry> Goals { get; init; } = ImmutableDictionary<DeviceName, GoalLedgerEntry>.Empty;

    /// <summary>The manual goals (PLAN-19, "want now"): per device what the user asked for and how much has been delivered; empty in a schema 3 or 4 file.</summary>
    public ImmutableDictionary<DeviceName, ManualGoal> ManualGoals { get; init; } = ImmutableDictionary<DeviceName, ManualGoal>.Empty;

    public GuardMemory GuardMemory => new(Budgets, CleanShutdown) { GroupBudgets = GroupBudgets };
}

/// <summary>How state.json was found at start.</summary>
public enum StateFileStatus
{
    Loaded,

    /// <summary>No file: a first start.</summary>
    Fresh,

    /// <summary>
    /// The file failed validation or could not be read; it was kept as state.json.corrupt and the run
    /// started fresh with control locked off and the kill switch on until the user confirms.
    /// </summary>
    ResetCorrupt,
}

public sealed record RuntimeStateLoad(RuntimeState State, StateFileStatus Status, string? Warning);

/// <summary>Where runtime state lives. Only the state writer task calls it; the guard never does.</summary>
public interface IRuntimeStateStore
{
    /// <summary>Reads and validates the file as untrusted input; never throws for a bad or missing file.</summary>
    RuntimeStateLoad Load();

    /// <summary>Writes atomically (the old file stays whole until the new one is complete); throws on I/O failure.</summary>
    void Save(RuntimeState state);
}

/// <summary>
/// The MQTT discovery objects announced so far, persisted with the runtime state, so an object a
/// valid configuration no longer declares is retracted even across a restart (07-PITFALLS).
/// </summary>
public interface IDiscoveryLedger
{
    /// <summary><c>component/object_id</c> of every announced object.</summary>
    ImmutableSortedSet<string> Discovered { get; }

    void Replace(IEnumerable<string> discovered);
}

/// <summary>The last known applied states, for display.</summary>
public interface ILastKnownStates
{
    ImmutableDictionary<DeviceName, ImmutableArray<StageLastKnown?>> LastKnown { get; }
}

/// <param name="Warning">The load warning, or the current write failure: kill switch and budgets then hold in memory only.</param>
public sealed record StateFileReport(StateFileStatus Status, string? Warning, bool Writable);
