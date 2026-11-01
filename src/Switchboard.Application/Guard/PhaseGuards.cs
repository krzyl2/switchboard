using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Guard;

/// <summary>
/// The per-constraint guards of the declared setup (every main phase and every circuit-group
/// phase), what a restart restores into them and what is persisted from them, and the next moment
/// any of them needs an evaluation. Not thread safe: the engine's one caller drives it.
/// </summary>
internal sealed class PhaseGuards
{
    private readonly Dictionary<ConstraintKey, PhaseGuard> _phases = [];
    private GuardMemory? _restored;
    private DateTimeOffset _restoredAt;

    public DateTimeOffset RestoredAt => _restoredAt;

    public PhaseGuard this[ConstraintKey key] => _phases[key];

    public IEnumerable<PhaseGuard> All => _phases.Values;

    public IEnumerable<ConstraintKey> Declared => _phases.Keys;

    /// <summary>Every main phase, then every circuit-group phase, of a setup.</summary>
    public static IEnumerable<ConstraintKey> Keys(ProtectionSetup setup) =>
        setup.Phases.Select(ConstraintKey.Main).Concat(setup.Groups.SelectMany(g => g.Phases.Select(p => new ConstraintKey(g.Id, p))));

    public void Restore(GuardMemory memory, DateTimeOffset now)
    {
        _restored = memory;
        _restoredAt = now;
    }

    /// <summary>
    /// Brings the guards in line with a new setup: those no longer declared go (returned, so the
    /// engine can forget what it recorded for them), declared ones are reconfigured, new ones start
    /// from the restored budget.
    /// </summary>
    public List<ConstraintKey> Apply(ProtectionSetup setup)
    {
        var keys = Keys(setup).ToHashSet();
        var removed = _phases.Keys.Where(k => !keys.Contains(k)).ToList();
        foreach (var key in removed)
        {
            _phases.Remove(key);
        }

        foreach (var key in keys)
        {
            var breaker = key.Group is { } id ? setup.Group(id)!.Breaker : setup.Breaker;
            if (_phases.TryGetValue(key, out var guard))
            {
                guard.Reconfigure(breaker);
            }
            else
            {
                _phases[key] = new PhaseGuard(key.Phase, RestoredBudget(key, setup.Budget.RecoveryTime), breaker);
            }
        }

        return removed;
    }

    public void OnClockStepBack(DateTimeOffset last, TimeSpan recovery)
    {
        foreach (var phase in _phases.Values)
        {
            phase.OnClockStepBack(last, recovery);
        }
    }

    private double RestoredBudget(ConstraintKey key, TimeSpan recovery)
    {
        var saved = _restored is not { } memory ? null
            : key.Group is { } group ? memory.GroupBudgets.GetValueOrDefault(group)?.GetValueOrDefault(key.Phase)
            : memory.Budgets.GetValueOrDefault(key.Phase);
        if (saved is null)
        {
            return 1;
        }

        var budget = Math.Clamp(saved.Budget, 0, 1);
        return _restored!.CleanShutdown && _restoredAt >= saved.At
            ? Math.Min(1, budget + ((double)(_restoredAt - saved.At).Ticks / recovery.Ticks))
            : budget;
    }

    /// <summary>What to persist now (budgets at <paramref name="at"/>); a phase not seen in this process keeps what was restored.</summary>
    public GuardMemory Export(DateTimeOffset at, TimeSpan recovery, bool cleanShutdown)
    {
        var budgets = (_restored?.Budgets ?? ImmutableDictionary<Phase, BudgetMemory>.Empty)
            .SetItems(_phases.Where(p => p.Key.Group is null).Select(p => KeyValuePair.Create(p.Key.Phase, new BudgetMemory(p.Value.Budget(at, recovery), at))));
        var groups = _restored?.GroupBudgets ?? ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>>.Empty;
        foreach (var group in _phases.Where(p => p.Key.Group is not null).GroupBy(p => p.Key.Group!.Value))
        {
            groups = groups.SetItem(
                group.Key,
                (groups.GetValueOrDefault(group.Key) ?? ImmutableDictionary<Phase, BudgetMemory>.Empty)
                    .SetItems(group.Select(p => KeyValuePair.Create(p.Key.Phase, new BudgetMemory(p.Value.Budget(at, recovery), at)))));
        }

        return new GuardMemory(budgets, cleanShutdown) { GroupBudgets = groups };
    }

    /// <summary>The next moment a guard must evaluate without new input: a band timer, a budget threshold, never later than the next cycle boundary.</summary>
    public DateTimeOffset NextDeadline(ProtectionSetup setup, DateTimeOffset now)
    {
        var cycle = setup.Timing.Cycle.Ticks;
        var next = DateTimeOffset.UnixEpoch + TimeSpan.FromTicks((((now - DateTimeOffset.UnixEpoch).Ticks / cycle) + 1) * cycle);
        void Consider(DateTimeOffset? candidate)
        {
            if (candidate is { } at && at > now && at < next)
            {
                next = at;
            }
        }

        var budget = setup.Budget;
        foreach (var guard in _phases.Values.Where(g => g.Valid && !g.ChainBroken))
        {
            Consider(guard.NextTimerExpiry());
            var at = guard.CommittedAt!.Value;
            var b = guard.CommittedBudget;
            var m = guard.HeldMultiple;
            var profile = guard.Profile;
            var recovery = budget.RecoveryTime;
            foreach (var crossing in new[]
            {
                OverloadBudget.TimeUntilBelow(b, budget.StopIncreasesBelow, m, profile, recovery),
                OverloadBudget.TimeUntilBelow(b, budget.ShedOneBelow, m, profile, recovery),
                OverloadBudget.TimeUntilAtOrBelow(b, budget.TopTierAtOrBelow, m, profile, recovery),
                OverloadBudget.TimeUntilNotBelow(b, budget.StopIncreasesBelow, m, profile, recovery),
                OverloadBudget.TimeUntilNotBelow(b, budget.ShedOneBelow, m, profile, recovery),
                OverloadBudget.TimeUntilNotBelow(b, 1, m, profile, recovery),
            })
            {
                if (crossing is { } dt && dt <= guard.MaxAge)
                {
                    Consider(at + dt);
                }
            }
        }

        return next;
    }
}
