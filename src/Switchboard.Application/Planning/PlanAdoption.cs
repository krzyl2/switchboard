using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>What a proposed change of the current slot's entry says: the want, the energy source and the power in kW rounded to a watt (so jitter below it is the same change).</summary>
internal sealed record PendingChange(PlanWant Want, EnergySource? Class, string PowerKw);

/// <summary>
/// Hysteresis (15-PLANNER, "Re-planning"): takes the new plan for every future slot and, for the current slot, keeps the entry in force unless the change has
/// been proposed <c>adopt_cycles</c> times running and moves the energy by <c>adopt_min_change_fraction</c> or flips the want. Triggers, urgent entries and a
/// plan that is not comparable (another configuration, nothing before it) replace the entry at once; a device running under a run entry keeps it when the
/// plan would defer it. Owns what is pending, which is the only state it has.
/// </summary>
internal sealed class PlanAdoption(ILogger logger)
{
    private readonly Dictionary<DeviceName, (PendingChange Change, int Count)> _pending = [];

    /// <summary>Forgets every pending change (the plan was dropped).</summary>
    public void Reset() => _pending.Clear();

    /// <param name="current">The plan in force; none: the proposal is adopted as it is.</param>
    /// <param name="held">How many devices kept their current slot's entry.</param>
    public PlanSnapshot Adopt(PlanSnapshot? current, PlanSnapshot proposal, PlannerCapture capture, DateTimeOffset now, bool everyone, HashSet<DeviceName> triggered, out int held)
    {
        held = 0;
        var parameters = capture.Input.Parameters;
        if (current is null || everyone || current.ConfigRevision != proposal.ConfigRevision || current.ComputedAt > now)
        {
            _pending.Clear();
            return proposal;
        }

        var heldSurplus = 0.0;
        var devices = ImmutableArray.CreateBuilder<DevicePlan>(proposal.Devices.Length);
        foreach (var device in proposal.Devices)
        {
            var next = device.Entries.FirstOrDefault(e => now >= e.Start && now < e.End);
            var entry = current.EntryAt(device.Device, now);
            if (next is null || entry is null || triggered.Contains(device.Device) || next.Want == PlanWant.Urgent || next.Goal != entry.Goal)
            {
                _pending.Remove(device.Device);
                devices.Add(device);
                continue;
            }

            if (!KeepCurrent(device.Device, entry, next, parameters))
            {
                devices.Add(device);
                continue;
            }

            held++;
            var scale = (next.End - next.Start) / (entry.End - entry.Start);
            var kept = entry with { Start = next.Start, End = next.End, EnergyKwh = entry.EnergyKwh * scale };
            var holding = Hold(device, next, kept);
            if (kept.Class is EnergySource.Surplus)
            {
                heldSurplus += holding.Entries.First(e => e.Start == next.Start).EnergyKwh;
            }

            devices.Add(holding);
        }

        var merged = proposal.WithDevices(devices.ToImmutable());

        // The kept entries were valid when they were made: the surplus they counted on is theirs to the end of the slot even if the forecast has since taken it away,
        // so the check of the merged plan is made against an input that still has it. Everything else (limits, need, sources) is checked as it stands.
        var checkedInput = heldSurplus <= 0 || capture.Input.Inputs.Slots.IsEmpty
            ? capture.Input
            : capture.Input with { Inputs = capture.Input.Inputs with { Slots = capture.Input.Inputs.Slots.SetItem(0, capture.Input.Inputs.Slots[0] with { PvKwh = Math.Max(capture.Input.Inputs.Slots[0].PvKwh, capture.Input.Inputs.Slots[0].BaseKwh) + heldSurplus }) } };
        var broken = held > 0 ? PlanVerifier.Verify(checkedInput, merged) : [];
        if (!broken.IsEmpty)
        {
            // Keeping an entry would have broken a limit the new plan respects (the energy it freed went to another device): the new plan stands as it is.
            logger.LogDebug("plan_hold_dropped: keeping the current slot's entries would have broken {Violations}; the new plan stands as it is.", string.Join(", ", broken.Select(v => v.Code).Distinct()));
            _pending.Clear();
            held = 0;
            return proposal;
        }

        return merged;
    }

    /// <summary>
    /// The device's plan with the current slot's entry kept as it was. Whatever the kept entry has more than the new plan's, the new plan's later slots have
    /// less (taken from the latest first), so the device's total is still the goals' need and the plan adds up; what it has less, the goal shows as unplaced.
    /// If the later slots cannot give it back the kept energy is cut to what the need allows: the commitment never raises the total.
    /// </summary>
    private static DevicePlan Hold(DevicePlan device, PlanEntry next, PlanEntry kept)
    {
        var entries = device.Entries.ToArray();
        var index = Array.IndexOf(entries, next);
        var goals = device.Goals;
        var delta = kept.EnergyKwh - next.EnergyKwh;
        var energy = kept.EnergyKwh;
        if (delta > Planner.Tolerance)
        {
            for (var i = entries.Length - 1; i > index && delta > Planner.Tolerance; i--)
            {
                var later = entries[i];
                if (later.Want is not (PlanWant.Run or PlanWant.Urgent) || later.EnergyKwh <= Planner.Tolerance)
                {
                    continue;
                }

                var take = Math.Min(later.EnergyKwh, delta);
                delta -= take;
                entries[i] = take >= later.EnergyKwh - Planner.Tolerance
                    ? later with { Want = PlanWant.Defer, Class = null, EnergyKwh = 0, MaxPowerW = 0 }
                    : later with { EnergyKwh = later.EnergyKwh - take };
            }

            energy -= Math.Max(0, delta);
        }
        else if (delta < -Planner.Tolerance && kept.Goal is { } reference)
        {
            goals = [.. goals.Select(g => g.Ref == reference ? g with { PlannedKwh = Math.Max(0, g.PlannedKwh + delta), ShortfallKwh = g.ShortfallKwh - delta } : g)];
        }

        entries[index] = kept with { EnergyKwh = energy };
        return device with { Entries = [.. entries], Goals = goals };
    }

    private bool KeepCurrent(DeviceName device, PlanEntry current, PlanEntry next, PlannerParameters parameters)
    {
        var currentPower = current.EnergyKwh / (current.End - current.Start).TotalHours;
        var nextPower = next.EnergyKwh / (next.End - next.Start).TotalHours;
        var larger = Math.Max(currentPower, nextPower);
        var change = larger <= Planner.Tolerance ? 0 : Math.Abs(nextPower - currentPower) / larger;
        var flips = current.Want != next.Want || current.Class != next.Class;
        if (!flips && change < parameters.AdoptMinChangeFraction)
        {
            _pending.Remove(device);
            return true;
        }

        if (current.Want is PlanWant.Run or PlanWant.Urgent && next.Want == PlanWant.Defer)
        {
            // A device that is running under a run entry keeps it to the slot's end: a forecast wobble must not produce a stop and a start inside an hour.
            _pending.Remove(device);
            return true;
        }

        var proposed = new PendingChange(next.Want, next.Class, nextPower.ToString("0.###", CultureInfo.InvariantCulture));
        var count = _pending.TryGetValue(device, out var known) && known.Change == proposed ? known.Count + 1 : 1;
        if (count >= parameters.AdoptCycles)
        {
            _pending.Remove(device);
            return false;
        }

        _pending[device] = (proposed, count);
        return true;
    }
}
