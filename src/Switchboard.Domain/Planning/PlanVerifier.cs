using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;

namespace Switchboard.Domain.Planning;

/// <summary>What a verified plan got wrong (15-PLANNER, "When the plan is missing, stale or wrong"): a plan with any of these is not adopted.</summary>
public enum PlanViolationCode
{
    /// <summary>A number that is not finite, or an energy or power below zero.</summary>
    InvalidNumber,

    /// <summary>An entry outside the plan's slots (before now, past the horizon, not a slot of the grid) or a slot twice.</summary>
    OutsideHorizon,

    /// <summary>A device the plan was not asked to plan, or one missing from the plan, or a device with a different number of entries than slots.</summary>
    UnknownDevice,

    /// <summary>The planned energy of a slot exceeds the guard's static limit on a constraint (main phase or circuit group phase).</summary>
    CapacityExceeded,

    /// <summary>A device's slot energy or power ceiling is above what the device can draw.</summary>
    DevicePowerExceeded,

    /// <summary>A device is planned for more energy than its goals need, or a goal is planned for more than it needs.</summary>
    EnergyExceedsNeed,

    /// <summary>The energy planned on surplus in a slot exceeds the surplus.</summary>
    SurplusExceeded,

    /// <summary>A run or urgent entry on a device that has no goal, cannot be started (restore only) or is storage, or one with a want that does not fit its energy.</summary>
    NotPlannable,

    /// <summary>An entry's class is outside the goal's sources, and it is neither urgent nor a hard goal that may escalate.</summary>
    ClassOutsideSources,

    /// <summary>A hard goal that can be met has no run entry, or its plan does not add up (planned plus shortfall is not the need, or the entries do not sum to the goals).</summary>
    HardGoalUnmet,

    /// <summary>A hard goal is called infeasible though its device could deliver the need alone.</summary>
    InfeasibleClaimed,
}

/// <summary>One thing wrong with a plan.</summary>
public sealed record PlanViolation(PlanViolationCode Code, DeviceName? Device, DateTimeOffset? Slot, string Message);

/// <summary>
/// Checks a <see cref="PlanSnapshot"/> against the <see cref="PlanInput"/> it was made for, independently of the planner: it reads the raw input and the
/// raw plan, shares no placement code with <see cref="Planner"/>, and trusts nothing the planner says about itself. The service runs it before a plan
/// is published; a plan with violations is dropped and the last plan stays. This is the planner's own safety net: the static limits are also the
/// guard's, so a plan that passes can never ask for more than the envelope could ever allow.
/// </summary>
public static class PlanVerifier
{
    /// <summary>Tolerance of the energy sums in kWh (float noise), not a control parameter.</summary>
    private const double Tolerance = 1e-6;

    public static ImmutableArray<PlanViolation> Verify(PlanInput input, PlanSnapshot plan)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plan);
        var found = new List<PlanViolation>();
        void Add(PlanViolationCode code, DeviceName? device, DateTimeOffset? slot, string message) => found.Add(new PlanViolation(code, device, slot, message));

        var slots = input.Inputs.Slots;
        var devices = input.Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var goalsByDevice = input.Goals.GroupBy(g => g.Device).ToDictionary(g => g.Key, g => g.ToList());

        if (plan.Devices.Length != devices.Count || plan.Devices.Select(d => d.Device).Distinct().Count() != plan.Devices.Length)
        {
            Add(PlanViolationCode.UnknownDevice, null, null, "the plan's devices are not the input's devices");
        }

        var capacity = new Dictionary<(ConstraintKey Key, int Slot), double>();
        var surplusUsed = new double[slots.Length];
        foreach (var devicePlan in plan.Devices)
        {
            if (!devices.TryGetValue(devicePlan.Device, out var device))
            {
                Add(PlanViolationCode.UnknownDevice, devicePlan.Device, null, "the plan names a device that was not asked for");
                continue;
            }

            var goals = goalsByDevice.GetValueOrDefault(device.Id) ?? [];
            var needTotal = device.CanStart && device.Kind != PlanDeviceKind.Storage
                ? goals.Where(g => !GoalMath.IsMet(g.RemainingKwh) && !Expired(g, input.Now)).Sum(g => g.RemainingKwh)
                : 0;
            var entries = devicePlan.Entries;
            if (entries.Length != slots.Length)
            {
                Add(PlanViolationCode.OutsideHorizon, device.Id, null, $"{entries.Length} entries for {slots.Length} slots");
            }

            var energyTotal = 0.0;
            var ceilingKw = Planner.CeilingW(device, input.CapacityW) / 1000;
            var seen = new HashSet<DateTimeOffset>();
            foreach (var entry in entries)
            {
                var index = IndexOf(slots, entry.Start, entry.End);
                if (index < 0 || entry.Start < input.Now - TimeSpan.FromTicks(1) || entry.End > input.Inputs.Grid.To || !seen.Add(entry.Start))
                {
                    Add(PlanViolationCode.OutsideHorizon, device.Id, entry.Start, "the entry is not one of the plan's slots, or is repeated");
                    continue;
                }

                if (!double.IsFinite(entry.EnergyKwh) || !double.IsFinite(entry.MaxPowerW) || entry.EnergyKwh < 0 || entry.MaxPowerW < 0)
                {
                    Add(PlanViolationCode.InvalidNumber, device.Id, entry.Start, "an entry has a negative or non-finite energy or power");
                    continue;
                }

                var hours = slots[index].Slot.Hours;
                var active = entry.Want is PlanWant.Run or PlanWant.Urgent;
                if (!active)
                {
                    if (entry.EnergyKwh > Tolerance || entry.Class is not null)
                    {
                        Add(PlanViolationCode.NotPlannable, device.Id, entry.Start, $"a {entry.Want} entry carries energy or a class");
                    }

                    continue;
                }

                energyTotal += entry.EnergyKwh;
                if (!device.CanStart || device.Kind == PlanDeviceKind.Storage || needTotal <= Tolerance)
                {
                    Add(PlanViolationCode.NotPlannable, device.Id, entry.Start, "a run entry for a device the plan may not start or that has no goal to serve");
                }

                if (entry.MaxPowerW > device.MaxW + Tolerance || entry.EnergyKwh > (device.MaxW / 1000 * hours) + Tolerance)
                {
                    Add(PlanViolationCode.DevicePowerExceeded, device.Id, entry.Start, "the entry asks for more than the device can draw");
                }

                if (entry.MaxPowerW > (ceilingKw * 1000) + Tolerance || entry.EnergyKwh > (ceilingKw * hours) + Tolerance)
                {
                    Add(PlanViolationCode.CapacityExceeded, device.Id, entry.Start, "the entry asks for more than the static limits leave this device");
                }

                foreach (var key in device.Keys)
                {
                    capacity[(key.Key, index)] = capacity.GetValueOrDefault((key.Key, index)) + (entry.EnergyKwh * key.Share);
                }

                if (PlanClasses.IsPv(entry.Class))
                {
                    surplusUsed[index] += entry.EnergyKwh;
                }

                if (!ClassAllowed(entry, slots[index], device, goals))
                {
                    Add(PlanViolationCode.ClassOutsideSources, device.Id, entry.Start, "the entry's class is outside the goal's sources");
                }
            }

            if (energyTotal > needTotal + Tolerance)
            {
                Add(PlanViolationCode.EnergyExceedsNeed, device.Id, null, "the device is planned for more energy than its goals need");
            }

            CheckGoals(input, plan, devices, device, goals, devicePlan, energyTotal, Add);
        }

        foreach (var ((key, index), kwh) in capacity.OrderBy(p => p.Key.Slot).ThenBy(p => p.Key.Key.ToString(), StringComparer.Ordinal))
        {
            var limitW = input.CapacityW.TryGetValue(key, out var w) ? w : 0;
            if (kwh > (limitW / 1000 * slots[index].Slot.Hours) + Tolerance)
            {
                Add(PlanViolationCode.CapacityExceeded, null, slots[index].Slot.Start, $"{key} carries {kwh:0.###} kWh against a limit of {limitW / 1000 * slots[index].Slot.Hours:0.###} kWh");
            }
        }

        for (var t = 0; t < slots.Length; t++)
        {
            if (surplusUsed[t] > slots[t].SurplusKwh + Tolerance)
            {
                Add(PlanViolationCode.SurplusExceeded, null, slots[t].Slot.Start, "more energy is planned on surplus than the slot has");
            }
        }

        return [.. found];
    }

    private static bool Expired(GoalRequest goal, DateTimeOffset now) => !goal.Hard && goal.Deadline is { } deadline && now >= deadline;

    private static int IndexOf(ImmutableArray<PlanInputSlot> slots, DateTimeOffset start, DateTimeOffset end)
    {
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].Slot.Start == start && slots[i].Slot.End == end)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A class is allowed when some live goal of the device lists it in its sources (the goal's, else the device's, else any), or when the entry is urgent
    /// and some live hard goal may escalate (or is past its deadline, which pursues on any energy).
    /// </summary>
    private static bool ClassAllowed(PlanEntry entry, PlanInputSlot slot, PlanDevice device, List<GoalRequest> goals)
    {
        foreach (var goal in goals.Where(g => !GoalMath.IsMet(g.RemainingKwh)))
        {
            var sources = goal.Sources ?? device.Sources ?? SourceSet.Any;
            if (PlanClasses.IsPv(entry.Class))
            {
                if (sources.Pv || (entry.Want == PlanWant.Urgent && goal.Hard))
                {
                    return true;
                }

                continue;
            }

            var zone = PlanClasses.ZoneId(entry.Class) ?? slot.Zone;
            if (sources.AllZones || (zone is not null && sources.Zones.Contains(zone)) || (entry.Want == PlanWant.Urgent && goal.Hard))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckGoals(PlanInput input, PlanSnapshot plan, Dictionary<DeviceName, PlanDevice> devices, PlanDevice device, List<GoalRequest> goals, DevicePlan devicePlan, double energyTotal, Action<PlanViolationCode, DeviceName?, DateTimeOffset?, string> add)
    {
        var plannedSum = 0.0;
        foreach (var goal in devicePlan.Goals)
        {
            if (!double.IsFinite(goal.PlannedKwh) || !double.IsFinite(goal.ShortfallKwh) || goal.PlannedKwh < 0 || goal.ShortfallKwh < 0)
            {
                add(PlanViolationCode.InvalidNumber, device.Id, null, "a goal has a negative or non-finite planned energy");
                continue;
            }

            plannedSum += goal.PlannedKwh;
            if (goal.PlannedKwh > goal.RemainingKwh + Tolerance)
            {
                add(PlanViolationCode.EnergyExceedsNeed, device.Id, null, "a goal is planned for more than it needs");
            }
        }

        if (Math.Abs(plannedSum - energyTotal) > Tolerance)
        {
            add(PlanViolationCode.HardGoalUnmet, device.Id, null, "the entries do not add up to the goals' planned energy");
        }

        if (!device.CanStart || device.Kind == PlanDeviceKind.Storage)
        {
            return;
        }

        foreach (var goal in goals.Where(g => g.Hard && !GoalMath.IsMet(g.RemainingKwh)))
        {
            var planned = devicePlan.Goals.FirstOrDefault(g => g.Ref.Revision == goal.Revision && g.Deadline == goal.Deadline);
            if (planned is null)
            {
                add(PlanViolationCode.HardGoalUnmet, device.Id, null, "a hard goal is missing from the plan");
                continue;
            }

            var alone = DeliverableAlone(input, device, goal);
            if (planned.Status == GoalStatus.Infeasible)
            {
                if (alone >= goal.RemainingKwh - Tolerance)
                {
                    add(PlanViolationCode.InfeasibleClaimed, device.Id, null, "a hard goal is called infeasible though the device could deliver it alone");
                }

                continue;
            }

            if (Math.Abs(planned.PlannedKwh + planned.ShortfallKwh - goal.RemainingKwh) > Tolerance)
            {
                add(PlanViolationCode.HardGoalUnmet, device.Id, null, "a hard goal's planned energy and shortfall do not add up to its need");
            }

            // A feasible hard goal with no energy is wrong only where nothing explains it: the device has no energy at all and no other device in the plan shares one of
            // its constraints or draws surplus (there the shortage may be contention, which the plan is free to resolve either way).
            if (alone >= goal.RemainingKwh - Tolerance && planned.PlannedKwh <= Tolerance && energyTotal <= Tolerance && !Contended(plan, devices, device))
            {
                add(PlanViolationCode.HardGoalUnmet, device.Id, null, "a hard goal that can be met has no energy in the plan");
            }
        }
    }

    private static bool Contended(PlanSnapshot plan, Dictionary<DeviceName, PlanDevice> devices, PlanDevice device) =>
        plan.Devices.Any(other =>
            other.Device != device.Id
            && devices.TryGetValue(other.Device, out var o)
            && other.Entries.Any(e => e.Want is PlanWant.Run or PlanWant.Urgent && e.EnergyKwh > Tolerance && (PlanClasses.IsPv(e.Class) || o.Keys.Any(k => device.Keys.Any(x => x.Key == k.Key)))));

    /// <summary>The most the device could draw for the goal before its deadline with nothing else competing, from the raw input.</summary>
    private static double DeliverableAlone(PlanInput input, PlanDevice device, GoalRequest goal)
    {
        // Within what the goal may use: its sources, or any zone when it may escalate (only a deadline inside the horizon escalates: what lies beyond it a later
        // plan can still serve) or is already past its deadline.
        var sources = goal.Sources ?? device.Sources ?? SourceSet.Any;
        var ceilingKw = Planner.CeilingW(device, input.CapacityW) / 1000;
        var cutoff = goal.Deadline is null || goal.Deadline <= input.Now ? input.Inputs.Grid.To : goal.UsableDeadline!.Value > input.Now ? goal.UsableDeadline.Value : goal.Deadline.Value;
        var anyZone = (goal.Escalate && cutoff <= input.Inputs.Grid.To) || (goal.Deadline is { } due && due <= input.Now);
        if (device.MinW > 0 && (ceilingKw * 1000) + Tolerance < device.MinW)
        {
            // Its minimum power is above what the static limits leave it: it can never run.
            return 0;
        }

        var total = 0.0;
        foreach (var slot in input.Inputs.Slots)
        {
            var start = slot.Slot.Start > input.Now ? slot.Slot.Start : input.Now;
            var end = slot.Slot.End < cutoff ? slot.Slot.End : cutoff;
            if (end <= start)
            {
                continue;
            }

            var reach = ceilingKw * (end - start).TotalHours;
            var grid = slot.Import is not null && (anyZone || sources.AllZones || (slot.Zone is not null && sources.Zones.Contains(slot.Zone)));
            if (grid)
            {
                total += reach;
            }
            else if (sources.Pv && slot.SurplusKwh > Tolerance)
            {
                // Without the grid the slot holds only its surplus.
                total += Math.Min(reach, slot.SurplusKwh);
            }
        }

        return total;
    }
}
