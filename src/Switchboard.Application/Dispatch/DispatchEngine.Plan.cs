using Switchboard.Application.Planning;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The ceilings outside the guard, applied to the step's plan of a device before the allocation: the plan (15-PLANNER, PLAN-13 to 15) and the device's power
/// band (2026-11-03). What the plan says and what a device drew in its slot is the <see cref="PlanReader"/>'s; a narrowing only ever lowers a device's ceiling.
/// What each does to the reason: <see cref="Plan.CeilingStop"/> names a decrease, <see cref="Plan.CeilingRefusal"/> an increase that was possible and is not made.
/// </summary>
public sealed partial class DispatchEngine
{
    /// <summary>
    /// A fresh plan entry narrows as the plan says; one that decides the device's ceiling (done, or a run or urgent slot with a usable power ceiling) also
    /// stands for its power band, which the plan kept or, for a deadline, lifted. Otherwise the power band of this local hour caps what the device takes
    /// from the grid, so a stale, suspended or changing plan, a restart or a plan-less slot never lets it run above the band; surplus no other device draws
    /// comes on top of the band, which limits grid power only.
    /// </summary>
    private void NarrowByCeilings(Plan plan, DispatchSetup setup, DateTimeOffset now)
    {
        if (plan.Entry is { } entry && NarrowByPlan(plan, entry, setup))
        {
            return;
        }

        if (setup.Balance is { } config
            && setup.PowerBands.TryGetValue(plan.Device.Id, out var bands)
            && PowerBands.CeilingAt(bands, now, _time.LocalTimeZone) is { } bandW)
        {
            NarrowToCeiling(plan, bandW + SurplusFor(plan.Device.Id), ReasonCode.PowerBand, config);
        }
    }

    /// <summary>
    /// A device that cannot take energy now (2026-11-09: a charger with no car plugged in) does not start or rise, and a running one comes down to its
    /// lowest level (or what a min_on lockout holds) through the usual persistence. No plan entry or power band applies on top: it is at the bottom already.
    /// The increase it would have taken is not one: its persistence starts again, so a car plugged in later starts on its own agreeing boundaries.
    /// </summary>
    private static void NarrowNotReady(Plan plan)
    {
        plan.MaxAllowed = Math.Max(plan.MinAllowed, Math.Min(plan.MaxAllowed, 0));
        plan.CeilingRefusal = ReasonCode.DeviceNotReady;
        plan.CeilingStop = plan.MaxAllowed < plan.BasisIndex ? ReasonCode.DeviceNotReady : null;
        plan.BalanceAllows = false;
    }

    /// <summary>
    /// The plan's narrowing: a done goal that stays off (nothing starts or rises, a running device comes down through the usual persistence), the slot's power
    /// ceiling, and the slot's energy budget (no increase once it is delivered).
    /// </summary>
    /// <returns>Whether the entry decided the device's ceiling, so no power band applies on top of it.</returns>
    private bool NarrowByPlan(Plan plan, PlanEntryView entry, DispatchSetup setup)
    {
        if (entry.Want == PlanWant.Done)
        {
            plan.MaxAllowed = Math.Max(plan.MinAllowed, Math.Min(plan.MaxAllowed, plan.BasisIndex - 1));
            plan.CeilingRefusal = ReasonCode.GoalMet;
            plan.CeilingStop = plan.BasisIndex > 0 ? ReasonCode.GoalMet : null;
            return true;
        }

        if (entry.Want is not (PlanWant.Run or PlanWant.Urgent))
        {
            return false;
        }

        var ceilingSet = setup.Balance is { } config && NarrowToCeiling(plan, entry.MaxPowerW, ReasonCode.PlanSlotTarget, config);

        // The budget bounds the energy of a run entry; an urgent one is a deadline at risk, which the budget does not hold back.
        if (entry.Want == PlanWant.Run && _planReader.Delivered(plan.Device.Id) >= entry.EnergyKwh && plan.MaxAllowed > plan.BasisIndex)
        {
            plan.MaxAllowed = Math.Max(plan.MinAllowed, plan.BasisIndex);
            plan.CeilingRefusal = ReasonCode.PlanSlotTarget;
        }

        return ceilingSet;
    }

    /// <summary>
    /// Caps the device at the highest level whose power fits <paramref name="ceilingW"/>, naming <paramref name="reason"/> for the refused increase and,
    /// below the level it holds, for the decrease.
    /// </summary>
    /// <returns>False when the ceiling is not a usable number (nothing is narrowed).</returns>
    private static bool NarrowToCeiling(Plan plan, double ceilingW, ReasonCode reason, HourlyBalanceSetup config)
    {
        if (Highest(plan, ceilingW, config) is not { } ceiling)
        {
            return false;
        }

        var narrowed = Math.Max(plan.MinAllowed, Math.Min(plan.MaxAllowed, ceiling));
        if (narrowed < plan.MaxAllowed)
        {
            plan.MaxAllowed = narrowed;
            plan.CeilingRefusal = reason;
            plan.CeilingStop = narrowed < plan.BasisIndex ? reason : null;
        }

        return true;
    }

    /// <summary>The free power no other controlled device draws (W, never below 0): what a device may take beyond its power band. Unknown free power is none.</summary>
    private double SurplusFor(DeviceName device) =>
        Surplus(_balanceTracker.View).Free is { } free
            ? Math.Max(0, free.Value - _devicePowers.Where(p => p.Device != device).Sum(p => p.Power?.Value ?? 0))
            : 0;

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
