using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Goals;

namespace Switchboard.Domain.Planning;

/// <summary>
/// The pure planner (15-PLANNER, "The planning algorithm"): <see cref="Plan"/> turns a <see cref="PlanInput"/> into a <see cref="PlanSnapshot"/>.
/// No clock, no I/O, no entity, no randomness, no dependency on the order the devices or goals arrive in: every tie is broken by device name, slot
/// start and class order, so the same input is the same plan to the byte.
/// <para>
/// A kWh a goal device draws costs the export value while it fits the slot's surplus (class <c>pv</c>) and the import price of the slot beyond it (class
/// <c>zone</c>). Goals are placed earliest-deadline-first (urgent, then hard, then soft), each on its cheapest cells with a tie going to the earliest
/// slot, within the guard's static limit per constraint, the device's own power and the surplus; a short refinement gives each goal in turn first call on
/// the capacity and keeps the result only if it is better. A hard goal that its sources cannot meet adds every zone (<see cref="PlanWant.Urgent"/>), and
/// one at its latest safe start runs from now, so a failed forecast cannot cost a deadline. Storage is not planned here (slice 6).
/// </para>
/// </summary>
public static class Planner
{
    /// <summary>Numeric noise of energy arithmetic in kWh, not a control parameter.</summary>
    public const double Tolerance = 1e-9;

    public static PlanSnapshot Plan(PlanInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new PlanRun(input, cancellationToken).Run();
    }

    /// <summary>The most a device can draw at once: its maximum, and what the static limits leave on every constraint it loads (watts).</summary>
    internal static double CeilingW(PlanDevice device, ImmutableDictionary<ConstraintKey, double> capacityW)
    {
        var ceiling = device.MaxW;
        foreach (var key in device.Keys)
        {
            if (key.Share <= 0)
            {
                continue;
            }

            ceiling = Math.Min(ceiling, capacityW.TryGetValue(key.Key, out var limit) ? limit / key.Share : 0);
        }

        return Math.Max(0, ceiling);
    }

    /// <summary>The goal's identity in a plan: the device and the deadline it is due at.</summary>
    public static string GoalId(GoalRequest request) =>
        request.Device.Value + "@" + (request.Deadline is { } deadline ? deadline.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) : "horizon");

    /// <summary>One planning run: describes the goals, places them earliest-deadline-first on a ledger, lets the refiner try other orders, and hands the result to the assembler.</summary>
    private sealed class PlanRun(PlanInput input, CancellationToken cancel)
    {
        private readonly PlanContext _ctx = new(input, cancel);

        public PlanSnapshot Run()
        {
            var ledger = CapacityLedger.Fresh(_ctx);
            var works = BuildWorks();
            cancel.ThrowIfCancellationRequested();

            var ordered = works.Where(w => w.Planned).OrderBy(w => w, Comparer<GoalWork>.Create(GoalWork.CompareUrgency)).ToList();
            foreach (var w in ordered)
            {
                cancel.ThrowIfCancellationRequested();
                GoalPlacer.Place(_ctx, ledger, w);
            }

            new OrderRefiner(_ctx, ledger, ordered).Refine([.. ordered.Where(w => !w.Critical)]);
            return new PlanAssembler(_ctx).Assemble(works, ordered);
        }

        private List<GoalWork> BuildWorks()
        {
            var works = new List<GoalWork>();
            foreach (var request in input.Goals.OrderBy(r => r.Device).ThenBy(r => r.Deadline is null).ThenBy(r => r.Deadline).ThenByDescending(r => r.Hard).ThenBy(r => r.Revision))
            {
                if (!_ctx.ByName.TryGetValue(request.Device, out var device))
                {
                    continue;
                }

                works.Add(GoalWork.Describe(_ctx, request, device));
            }

            return works;
        }
    }
}