using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Planning;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The plan as a source of targets for the dispatch loop (15-PLANNER, "How dispatch consumes it", PLAN-13 to 15): owns the
/// view it reads through, the valid entry of each device in this step and what each device with an entry drew in its slot.
/// Where a valid entry exists for a device and the configured plan mode allows it, the plan narrows what dispatch may do and, in
/// full mode only, adds one class behind every other check. The guard's permission and envelope, persistence, lockouts, settling,
/// the step limit and the simultaneous-increase limit are not touched, and without a valid entry nothing here runs at all, so the
/// decision is the v2 one to the byte.
/// </summary>
internal sealed class PlanReader(IPlanView? plan)
{
    /// <summary>The valid plan entry of each device in this step (empty while the mode is off or no plan applies).</summary>
    private readonly Dictionary<DeviceName, PlanEntryView> _planEntries = [];

    /// <summary>What each device with an entry drew since the slot of its entry began, as this engine counted it.</summary>
    private readonly Dictionary<DeviceName, SlotDraw> _slotDraws = [];

    /// <summary>The configured plan mode (off while there is no view).</summary>
    public PlannerDispatchMode Mode => plan?.Mode ?? PlannerDispatchMode.Off;

    /// <summary>The valid entry of a device in this step; null leaves the device to v2.</summary>
    public PlanEntryView? Entry(DeviceName device) => _planEntries.GetValueOrDefault(device);

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
    public void Observe(DispatchInput input, DispatchSetup setup, DateTimeOffset now, ImmutableArray<DevicePhasePower> devicePowers)
    {
        _planEntries.Clear();
        plan?.Observe(now);
        if (plan is null || plan.Mode == PlannerDispatchMode.Off)
        {
            _slotDraws.Clear();
            return;
        }

        foreach (var device in setup.Devices)
        {
            var entry = input.Overrides.For(device.Id).Mode == OverrideMode.Auto && input.SelfTest?.Device != device.Id ? plan.For(device.Id, now) : null;
            if (entry is null)
            {
                _slotDraws.Remove(device.Id);
                continue;
            }

            _planEntries[device.Id] = entry;
            var watts = devicePowers.Where(p => p.Device == device.Id).Sum(p => p.Power?.Value ?? 0);
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

    /// <summary>No valid configuration: no entries and no slot energy.</summary>
    public void Reset()
    {
        _planEntries.Clear();
        _slotDraws.Clear();
    }

    /// <summary>The energy a device has drawn in the slot of its entry.</summary>
    public double Delivered(DeviceName device) => _slotDraws.TryGetValue(device, out var draw) ? draw.Kwh : 0;

    /// <summary>Urgent devices first (full mode): the order is otherwise the start order, unchanged.</summary>
    public ImmutableArray<DeviceSpec> UrgentFirst(ImmutableArray<DeviceSpec> order)
    {
        if (Mode != PlannerDispatchMode.Full || !_planEntries.Values.Any(e => e.Want == PlanWant.Urgent))
        {
            return order;
        }

        return [.. order.Where(IsUrgent), .. order.Where(d => !IsUrgent(d))];

        bool IsUrgent(DeviceSpec spec) => _planEntries.TryGetValue(spec.Id, out var entry) && entry.Want == PlanWant.Urgent;
    }

    /// <summary>What the plan lets the allowance gate do for a device with this entry.</summary>
    public PlanGate GateFor(PlanEntryView? entry)
    {
        if (entry is null)
        {
            return PlanGate.None;
        }

        if (entry.Want == PlanWant.Defer)
        {
            return new PlanGate(true, null);
        }

        if (Mode != PlannerDispatchMode.Full)
        {
            return PlanGate.None;
        }

        return entry.Want switch
        {
            PlanWant.Urgent => new PlanGate(false, ReasonCode.GoalDeadlineUrgent),
            PlanWant.Run when entry.Class is EnergySource.Grid && entry.GoalAllowsZone => new PlanGate(false, ReasonCode.PlanSlotTarget),
            _ => PlanGate.None,
        };
    }

    /// <summary>What a decision records of the plan for a device; null without an entry.</summary>
    public PlanDecisionInputs? Block(PlanEntryView? entry, DeviceName device) =>
        entry is not null
            ? new PlanDecisionInputs(
                entry.PlanId,
                entry.ComputedAt,
                entry.SlotStart,
                entry.SlotEnd,
                entry.Want.ToString().ToLowerInvariant(),
                entry.Class,
                entry.EnergyKwh,
                Delivered(device),
                entry.MaxPowerW,
                Mode.ToString().ToLowerInvariant())
            : null;
}

/// <summary>What the plan lets the allowance gate do for a device with an entry (see the allowance gate of the dispatch engine).</summary>
/// <param name="Defers">A zone-class start or increase is refused, and a device already running on the zone (which the surplus does not hold) is brought down one stage a step (plan_deferred).</param>
/// <param name="Opens">Full mode only: the reason of the one class the plan adds, for a grid slot of a goal device (plan_slot_target) or an urgent hard goal (goal_deadline_urgent); null when none.</param>
internal readonly record struct PlanGate(bool Defers, ReasonCode? Opens)
{
    public static PlanGate None => default;
}
