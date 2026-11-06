using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Domain;

/// <summary>
/// <see cref="Switchboard.Domain.Goals.GoalRequest.Asap"/> (2026-11-06): a manual "charge now" is placed in the earliest slots its sources allow, whatever they
/// cost. Why it matters: the maintainer asks for a charge now from Home Assistant; a goal placed in the cheapest hours before its 12-hour deadline would start at
/// 21:00 and the car would not charge when he asked.
/// </summary>
public sealed class PlannerAsapTests
{
    private static readonly DateTimeOffset Evening = Local(2026, 11, 5, 17);

    private static PlanSnapshot Plan(bool asap)
    {
        // Afternoon peak until 21:00, then off-peak; no PV.
        var inputs = Custom(Evening, 12, i => i < 4 ? ("afternoon_peak", 1.45, 0.35, 0) : ("offpeak", 0.60, 0.35, 0));
        var goal = Goal(Car, 2.0, Evening + TimeSpan.FromHours(12), sources: SourceSet.Any) with { Asap = asap };
        return Solve(Input(Evening, inputs, [Charger(SourceSet.PvOnly)], [goal], Capacity(5000)));
    }

    [Fact]
    public void An_asap_goal_runs_from_the_first_slot_even_in_the_dearest_zone()
    {
        var plan = Plan(asap: true);

        var first = plan.EntryAt(Car, Evening)!;
        Assert.Equal(PlanWant.Run, first.Want);
        Assert.IsType<EnergySource.Grid>(first.Class);
        Assert.Equal(2.0, first.EnergyKwh, 6);
        Assert.Equal(2990, first.MaxPowerW);
    }

    [Fact]
    public void The_same_goal_without_asap_waits_for_the_cheapest_hours()
    {
        // The contrast that makes the flag matter: cost order puts the whole goal at 21:00.
        var plan = Plan(asap: false);

        Assert.NotEqual(PlanWant.Run, plan.EntryAt(Car, Evening)!.Want);
        Assert.Equal(2.0, plan.EntryAt(Car, Local(2026, 11, 5, 21))!.EnergyKwh, 6);
    }
}
