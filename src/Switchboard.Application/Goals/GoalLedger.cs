using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;

namespace Switchboard.Application.Goals;

/// <summary>The progress of the energy goals a source needs to read; the ledger is the one implementation.</summary>
public interface IGoalProgress
{
    /// <summary>The energy ledger entry of a device's goal; null when none was begun.</summary>
    GoalLedgerEntry? Entry(DeviceName device);
}

/// <summary>Every ledger entry at one moment; <see cref="Version"/> grows with each change, so persistence applies a change once and in order.</summary>
public sealed record GoalLedgerSnapshot(long Version, ImmutableDictionary<DeviceName, GoalLedgerEntry> Entries);

/// <summary>
/// The goal ledger (PLAN-03, 15-PLANNER): for each energy goal the controlled energy its device drew since the goal began, integrated
/// per dispatch step by the <see cref="GoalTracker"/>. It lives in memory, is handed to state.json by the coordinator and is
/// restored at start, so a restart does not forget what was delivered. A SoC goal has no entry: its progress is the SoC signal.
/// Nothing here commands anything.
/// </summary>
public sealed class GoalLedger(ILogger<GoalLedger> logger) : IGoalProgress
{
    private readonly object _gate = new();
    private GoalLedgerSnapshot _current = new(0, ImmutableDictionary<DeviceName, GoalLedgerEntry>.Empty);

    /// <summary>Raised after every change, outside the lock; for persistence. Handlers must not block.</summary>
    public event EventHandler<GoalLedgerSnapshot>? Changed;

    public GoalLedgerSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public GoalLedgerEntry? Entry(DeviceName device) => Current.Entries.GetValueOrDefault(device);

    /// <summary>Takes over what state.json held; call before the first step. Raises no event: it is what was already persisted.</summary>
    public void Restore(ImmutableDictionary<DeviceName, GoalLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            _current = new GoalLedgerSnapshot(_current.Version + 1, entries);
        }
    }

    /// <summary>Starts (or replaces) the entry of a device's goal.</summary>
    public void Begin(DeviceName device, GoalKind kind, DateTimeOffset since, DateTimeOffset? deadline)
    {
        Apply(entries => entries.SetItem(device, new GoalLedgerEntry(kind, since, deadline, 0, since)));
        logger.LogInformation("Goal ledger: {Device} began a {Kind} goal", device, kind);
    }

    /// <summary>Adds energy drawn over a step; a device with no entry counts nothing.</summary>
    public void AddDelivered(DeviceName device, double kwh, DateTimeOffset at)
    {
        if (!double.IsFinite(kwh) || kwh < 0)
        {
            return;
        }

        Apply(entries => entries.TryGetValue(device, out var entry) ? entries.SetItem(device, entry with { DeliveredKwh = entry.DeliveredKwh + kwh, UpdatedAt = at }) : entries);
    }

    /// <summary>The deadline moved before it passed: the energy delivered so far stays.</summary>
    public void Retarget(DeviceName device, DateTimeOffset? deadline) =>
        Apply(entries => entries.TryGetValue(device, out var entry) && entry.Deadline != deadline ? entries.SetItem(device, entry with { Deadline = deadline }) : entries);

    /// <summary>The goal ended (its want gate went off, or it was removed): its progress goes with it.</summary>
    public void Remove(DeviceName device)
    {
        if (Entry(device) is null)
        {
            return;
        }

        Apply(entries => entries.Remove(device));
        logger.LogInformation("Goal ledger: {Device} no longer has a goal", device);
    }

    private void Apply(Func<ImmutableDictionary<DeviceName, GoalLedgerEntry>, ImmutableDictionary<DeviceName, GoalLedgerEntry>> change)
    {
        GoalLedgerSnapshot snapshot;
        lock (_gate)
        {
            var entries = change(_current.Entries);
            if (ReferenceEquals(entries, _current.Entries))
            {
                return;
            }

            snapshot = new GoalLedgerSnapshot(_current.Version + 1, entries);
            _current = snapshot;
        }

        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<GoalLedgerSnapshot>)handler)(this, snapshot);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "A listener of the goal ledger failed");
            }
        }
    }
}
