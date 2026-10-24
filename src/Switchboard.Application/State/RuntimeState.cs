using System.Collections.Immutable;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.State;

/// <summary>The last applied state the guard saw for one device stage; for display only, never a protection input.</summary>
public sealed record StageLastKnown(AppliedState Applied, DateTimeOffset At);

/// <summary>
/// What survives a restart (/data/state.json, schema version 1). Lockout timers are deliberately
/// absent: every lockout restarts from the restart instant, never from a persisted expiry (SAFE-07).
/// </summary>
/// <param name="CleanShutdown">True only when the previous run recorded its shutdown safe state; written false at every start.</param>
/// <param name="Budgets">Overload budget per phase with the moment it held that value.</param>
/// <param name="Devices">Last known applied state per device stage (index = stage number − 1; null when never seen).</param>
/// <param name="Discovered">MQTT discovery objects published so far (<c>component/object_id</c>), so removed ones are retracted.</param>
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
    public const int SchemaVersion = 1;

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

    public GuardMemory GuardMemory => new(Budgets, CleanShutdown);
}

/// <summary>How state.json was found at start.</summary>
public enum StateFileStatus
{
    Loaded,

    /// <summary>No file: a first start.</summary>
    Fresh,

    /// <summary>The file failed validation; it was kept as state.json.corrupt and the run started fresh.</summary>
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
