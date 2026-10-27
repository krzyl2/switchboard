using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Planning;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The plan as a source of targets for the dispatch loop (15-PLANNER, "How dispatch consumes it", PLAN-13 to 15). Where a valid entry exists for a device and
/// the configured plan mode allows it, the plan narrows what dispatch may do (a ceiling, a defer, a goal that is met, a slot budget that is spent) and, in full
/// mode only, adds one class behind every other check: the plan's own, for a goal device its slot runs on a grid zone, and an urgent hard goal, which opens every
/// zone and is asked first. The guard's permission and envelope, persistence, lockouts, settling, the step limit and the simultaneous-increase limit are not
/// touched, and without a valid entry nothing here runs at all, so the decision is the v2 one to the byte.
/// </summary>
public sealed partial class DispatchEngine
{
    private readonly IPlanView? _plan;

    /// <summary>The valid plan entry of each device in this step (empty while the mode is off or no plan applies).</summary>
    private readonly Dictionary<DeviceName, PlanEntryView> _planEntries = [];

    /// <summary>What each device with an entry drew since the slot of its entry began, as this engine counted it.</summary>
    private readonly Dictionary<DeviceName, SlotDraw> _slotDraws = [];

    private PlannerDispatchMode PlanMode => _plan?.Mode ?? PlannerDispatchMode.Off;

    /// <summary>The energy a device drew in a slot, integrated step by step from what dispatch measured (or modelled) of its power.</summary>
    private sealed class SlotDraw(DateTimeOffset slotStart, DateTimeOffset at, double watts)
    {
        public DateTimeOffset SlotStart { get; } = slotStart;

        public double Kwh { get; set; }

        public DateTimeOffset At { get; set; } = at;

        public double Watts { get; set; } = watts;
    }

    /// <summary>
    /// Reads the plan's entries for this step and counts what the devices that have one drew in their slot. Nothing is read while the mode is off; a device in
    /// an override, or under the active self-test, has no entry (the plan never outranks an override).
    /// </summary>
    private void ObservePlan(DispatchInput input, DispatchSetup setup, DateTimeOffset now)
    {
        _planEntries.Clear();
        _plan?.Observe(now);
        if (_plan is null || _plan.Mode == PlannerDispatchMode.Off)
        {
            _slotDraws.Clear();
            return;
        }

        foreach (var device in setup.Devices)
        {
            var entry = input.Overrides.For(device.Id).Mode == OverrideMode.Auto && input.SelfTest?.Device != device.Id ? _plan.For(device.Id, now) : null;
            if (entry is null)
            {
                _slotDraws.Remove(device.Id);
                continue;
            }

            _planEntries[device.Id] = entry;
            var watts = _devicePowers.Where(p => p.Device == device.Id).Sum(p => p.Power?.Value ?? 0);
            if (!_slotDraws.TryGetValue(device.Id, out var draw) || draw.SlotStart != entry.SlotStart)
            {
                _slotDraws[device.Id] = new SlotDraw(entry.SlotStart, now, watts);
                continue;
            }

            if (now > draw.At)
            {
                draw.Kwh += draw.Watts * (now - draw.At).TotalHours / 1000;
            }

            draw.At = now;
            draw.Watts = watts;
        }
    }

    private void ResetPlan()
    {
        _planEntries.Clear();
        _slotDraws.Clear();
    }

    private double Delivered(DeviceName device) => _slotDraws.TryGetValue(device, out var draw) ? draw.Kwh : 0;

    /// <summary>Urgent devices first (full mode): the order is otherwise the start order, unchanged.</summary>
    private ImmutableArray<DeviceSpec> UrgentFirst(ImmutableArray<DeviceSpec> order)
    {
        if (PlanMode != PlannerDispatchMode.Full || !_planEntries.Values.Any(e => e.Want == PlanWant.Urgent))
        {
            return order;
        }

        return [.. order.Where(IsUrgent), .. order.Where(d => !IsUrgent(d))];

        bool IsUrgent(DeviceSpec spec) => _planEntries.TryGetValue(spec.Id, out var entry) && entry.Want == PlanWant.Urgent;
    }

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
        if (entry.Want == PlanWant.Run && Delivered(plan.Device.Id) >= entry.EnergyKwh && plan.MaxAllowed > plan.BasisIndex)
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

    /// <summary>What the plan lets the allowance gate do for a device with an entry (see <see cref="ApplyAllowanceGate"/>).</summary>
    /// <param name="Defers">A zone-class start or increase is refused (plan_deferred).</param>
    /// <param name="Opens">Full mode only: the reason of the one class the plan adds, for a grid slot of a goal device (plan_slot_target) or an urgent hard goal (goal_deadline_urgent); null when none.</param>
    private readonly record struct PlanGate(bool Defers, ReasonCode? Opens)
    {
        public static PlanGate None => default;
    }

    private PlanGate GateFor(Plan plan)
    {
        if (plan.Entry is not { } entry)
        {
            return PlanGate.None;
        }

        if (entry.Want == PlanWant.Defer)
        {
            return new PlanGate(true, null);
        }

        if (PlanMode != PlannerDispatchMode.Full)
        {
            return PlanGate.None;
        }

        return entry.Want switch
        {
            PlanWant.Urgent => new PlanGate(false, ReasonCode.GoalDeadlineUrgent),
            PlanWant.Run when PlanClasses.IsZone(entry.Class) && entry.GoalAllowsZone => new PlanGate(false, ReasonCode.PlanSlotTarget),
            _ => PlanGate.None,
        };
    }

    private PlanDecisionInputs? PlanBlock(Plan plan) =>
        plan.Entry is { } entry
            ? new PlanDecisionInputs(
                entry.PlanId,
                entry.ComputedAt,
                entry.SlotStart,
                entry.SlotEnd,
                entry.Want.ToString().ToLowerInvariant(),
                entry.Class,
                entry.EnergyKwh,
                Delivered(plan.Device.Id),
                entry.MaxPowerW,
                PlanMode.ToString().ToLowerInvariant())
            : null;
}
