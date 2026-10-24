using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Guard;

/// <summary>A phase's overload budget and the moment it held that value.</summary>
public sealed record BudgetMemory(double Budget, DateTimeOffset At);

/// <summary>
/// What the guard carries across a restart (SAFE-07): the overload budgets and whether the run
/// ended cleanly (shutdown safe state recorded). Lockouts are deliberately absent: every lockout
/// restarts from the restart instant, never from a persisted expiry.
/// </summary>
/// <param name="CleanShutdown">
/// True only once the shutdown safe state was evaluated. After a crash the loads were uncontrolled
/// and possibly above continuous, so the downtime earns no budget recovery.
/// </param>
public sealed record GuardMemory(ImmutableDictionary<Phase, BudgetMemory> Budgets, bool CleanShutdown)
{
    public static GuardMemory Fresh { get; } = new(ImmutableDictionary<Phase, BudgetMemory>.Empty, false);

    /// <summary>The overload budget of every circuit group phase (CONN-03), independent of the main phases.</summary>
    public ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>> GroupBudgets { get; init; } =
        ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>>.Empty;
}
