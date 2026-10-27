namespace Switchboard.Domain.Goals;

/// <summary>
/// The progress of one energy goal: the controlled energy its device drew since the goal began (15-PLANNER, The goal model). Persisted in
/// state.json, so a restart does not forget what was already delivered.
/// </summary>
/// <param name="Kind">The kind of goal the energy was counted for; a goal of another kind starts afresh.</param>
/// <param name="Since">When the goal began.</param>
/// <param name="Deadline">The deadline the energy was counted toward; null for a goal without one.</param>
/// <param name="DeliveredKwh">Energy drawn since <paramref name="Since"/>, measured (else rated-model) power integrated per dispatch step.</param>
/// <param name="UpdatedAt">The last step that counted.</param>
public sealed record GoalLedgerEntry(GoalKind Kind, DateTimeOffset Since, DateTimeOffset? Deadline, double DeliveredKwh, DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Whether this entry is the progress of the goal that <paramref name="kind"/> and <paramref name="deadline"/> describe at
    /// <paramref name="now"/>. The progress is that of one cycle: a goal of another kind is another goal, and once the deadline the
    /// energy was counted toward has passed, a different deadline is the next cycle (a recurring goal, the next trip) and starts at
    /// zero. A deadline that merely moved before it passed (a departure put back) keeps the energy already delivered.
    /// </summary>
    public bool Continues(GoalKind kind, DateTimeOffset? deadline, DateTimeOffset now) =>
        Kind == kind && !(Deadline is { } counted && now >= counted && deadline != counted);
}
