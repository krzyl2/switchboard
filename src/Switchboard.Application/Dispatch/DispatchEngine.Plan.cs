using Switchboard.Application.Planning;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The plan as a narrowing of the dispatch loop (15-PLANNER, PLAN-13 to 15), applied to the step's plan of a device before the
/// allocation. What the plan says and what a device drew in its slot is the <see cref="PlanReader"/>'s; the narrowing itself only
/// ever lowers a device's ceiling.
/// </summary>
public sealed partial class DispatchEngine
{
    /// <summary>
    /// The plan only narrows here, before the allocation: a done goal that stays off (nothing starts or rises, a running device comes down a stage at a time
    /// through the usual persistence), the slot's power ceiling, and the slot's energy budget (no increase once it is delivered). What each does to the reason:
    /// <see cref="Plan.PlanStop"/> names a decrease, <see cref="Plan.PlanRefusal"/> an increase that was possible and is not made.
    /// </summary>
    private void NarrowByPlan(Plan plan, PlanEntryView entry, DispatchSetup setup)
    {
        if (entry.Want == PlanWant.Done)
        {
            plan.MaxAllowed = Math.Max(plan.MinAllowed, Math.Min(plan.MaxAllowed, plan.BasisIndex - 1));
            plan.PlanRefusal = ReasonCode.GoalMet;
            plan.PlanStop = plan.BasisIndex > 0 ? ReasonCode.GoalMet : null;
            return;
        }

        if (entry.Want is not (PlanWant.Run or PlanWant.Urgent))
        {
            return;
        }

        if (setup.Balance is { } config && Highest(plan, entry.MaxPowerW, config) is { } ceiling)
        {
            var narrowed = Math.Max(plan.MinAllowed, Math.Min(plan.MaxAllowed, ceiling));
            if (narrowed < plan.MaxAllowed)
            {
                plan.MaxAllowed = narrowed;
                plan.PlanRefusal = ReasonCode.PlanSlotTarget;
                plan.PlanStop = narrowed < plan.BasisIndex ? ReasonCode.PlanSlotTarget : null;
            }
        }

        // The budget bounds the energy of a run entry; an urgent one is a deadline at risk, which the budget does not hold back.
        if (entry.Want == PlanWant.Run && _planReader.Delivered(plan.Device.Id) >= entry.EnergyKwh && plan.MaxAllowed > plan.BasisIndex)
        {
            plan.MaxAllowed = Math.Max(plan.MinAllowed, plan.BasisIndex);
            plan.PlanRefusal = ReasonCode.PlanSlotTarget;
        }
    }

    /// <summary>The highest level whose power does not exceed the ceiling; null when the ceiling is not a usable number.</summary>
    private static int? Highest(Plan plan, double ceilingW, HourlyBalanceSetup config)
    {
        if (!double.IsFinite(ceilingW) || ceilingW < 0)
        {
            return null;
        }

        for (var i = plan.Levels.Length - 1; i > 0; i--)
        {
            if (PlannedPower(plan, i, config) <= ceilingW + 1e-9 * Math.Max(1, ceilingW))
            {
                return i;
            }
        }

        return 0;
    }
}
