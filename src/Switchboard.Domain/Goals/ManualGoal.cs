using System.Globalization;
using System.Text;
using Switchboard.Domain.Devices;

namespace Switchboard.Domain.Goals;

/// <summary>
/// A goal the user stored in the add-on's own state with "want now" (15-PLANNER, Goal sources; PLAN-19): one device wants an amount of energy
/// or a state of charge by a deadline. It is data the add-on keeps in state.json; it names no entity and commands nothing. It expires at its
/// deadline, and a request for it is stated by the manual store as a <see cref="GoalOrigin.Manual"/> <see cref="GoalRequest"/>.
/// </summary>
/// <param name="Kind">Energy: <paramref name="Target"/> is kWh to deliver. Soc: it is the percent to reach (the device's <c>goal:</c> says where the SoC is read and the capacity).</param>
/// <param name="Target">kWh for an energy goal, percent for a SoC goal.</param>
/// <param name="Deadline">When the goal is due and lapses; a manual goal always has one.</param>
/// <param name="Hard">A hard deadline is met without a value test and may escalate; a soft one lapses at the deadline.</param>
/// <param name="Since">When the user asked.</param>
/// <param name="DeliveredKwh">An energy goal's progress: the controlled energy the device drew since <paramref name="Since"/>, counted by the goal tracker. Zero for a SoC goal.</param>
/// <param name="UpdatedAt">The last step that counted.</param>
public sealed record ManualGoal(GoalKind Kind, double Target, DateTimeOffset Deadline, bool Hard, DateTimeOffset Since, double DeliveredKwh, DateTimeOffset UpdatedAt)
{
    /// <summary>The largest energy target accepted: far above anything one device draws, and the bound state.json holds for a ledger entry.</summary>
    public const double MaxTargetKwh = 1_000_000;

    /// <summary>A SoC target is a percent of capacity above zero.</summary>
    public const double MaxTargetPercent = 100;

    /// <summary>Whether <paramref name="target"/> is a usable target for <paramref name="kind"/>.</summary>
    public static bool TargetInRange(GoalKind kind, double target) =>
        double.IsFinite(target) && target > 0 && target <= (kind == GoalKind.Soc ? MaxTargetPercent : MaxTargetKwh);

    /// <summary>Whether this is the same wish as <paramref name="other"/> (progress and time of asking do not matter).</summary>
    public bool SameWish(ManualGoal other) => Kind == other.Kind && Target == other.Target && Deadline == other.Deadline && Hard == other.Hard;

    /// <summary>
    /// A fingerprint of what the goal is (FNV-1a over its canonical text), so a plan entry made for another goal is not applied. It does not
    /// move with the progress.
    /// </summary>
    public long Revision(DeviceName device)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"manual\n{device.Value}\n{Kind}\n{Target:R}\n{Deadline.UtcTicks}\n{(Hard ? "hard" : "soft")}\n");
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * prime;
        }

        return unchecked((long)hash);
    }
}
