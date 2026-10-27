using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;

namespace Switchboard.Domain.Planning;

/// <summary>What the plan says about a device in one slot (15-PLANNER, "What the plan is"). A want is a proposal, never a command.</summary>
public enum PlanWant
{
    /// <summary>Energy is wanted in this slot.</summary>
    Run,

    /// <summary>The device's energy is placed in a later slot; not now.</summary>
    Defer,

    /// <summary>The goal is met (and <c>on_met</c> is <c>stop</c>): nothing more is wanted.</summary>
    Done,

    /// <summary>A hard deadline is at risk: run now, whatever the plan counted on.</summary>
    Urgent,

    /// <summary>The device has no goal in the plan (or the plan does not plan it): dispatch decides as before.</summary>
    None,
}

/// <summary>The energy classes of a plan entry (15-PLANNER): <c>pv</c> (surplus, the forgone export) or <c>zone:&lt;id&gt;</c> (bought in a tariff zone).</summary>
public static class PlanClasses
{
    public const string Pv = "pv";
    public const string ZonePrefix = "zone";

    /// <summary>The class of grid energy in <paramref name="zone"/>; a slot that no single zone holds has the bare class <c>zone</c>.</summary>
    public static string Zone(string? zone) => zone is null ? ZonePrefix : ZonePrefix + ":" + zone;

    public static bool IsPv(string? text) => text == Pv;

    public static bool IsZone(string? text) => text is not null && (text == ZonePrefix || text.StartsWith(ZonePrefix + ":", StringComparison.Ordinal));

    /// <summary>The zone id of a zone class; null for <c>pv</c>, for the bare class and for anything else.</summary>
    public static string? ZoneId(string? text) => text is not null && text.StartsWith(ZonePrefix + ":", StringComparison.Ordinal) ? text[(ZonePrefix.Length + 1)..] : null;
}

/// <summary>How a device draws power, for the plan.</summary>
public enum PlanDeviceKind
{
    /// <summary>One relay or several stages: power between the smallest stage and all stages.</summary>
    Relay,

    /// <summary>A current setpoint between a minimum and a maximum.</summary>
    Modulating,

    /// <summary>A battery inverter; not planned before slice 6 (entries are <see cref="PlanWant.None"/>).</summary>
    Storage,
}

/// <summary>The fraction of a device's power that loads a constraint (a main phase, or a phase of a circuit group); a three-phase device has a third on each phase.</summary>
public readonly record struct PlanKeyShare(ConstraintKey Key, double Share);

/// <summary>
/// A device as the planner sees it: watts, never amperes or entities (the conversion is the caller's). <paramref name="Keys"/> are the
/// constraints it loads and the share of its power on each; <paramref name="CanStart"/> is false for <c>start: restore_only</c>, which
/// the planner never starts; <paramref name="SpecIdentity"/> fingerprints the device's physical model, so an entry made for another spec is not applied.
/// </summary>
public sealed record PlanDevice(
    DeviceName Id,
    PlanDeviceKind Kind,
    double MinW,
    double MaxW,
    TimeSpan MinRun,
    ImmutableArray<PlanKeyShare> Keys,
    bool CanStart,
    ValueSpec Value,
    SourceSet? Sources,
    long SpecIdentity);

/// <summary>Everything the pure planner needs (15-PLANNER, "The planning algorithm"): time, per-slot inputs, devices, goals, the guard's static limits and the parameters.</summary>
/// <param name="Now">The plan's moment; the first slot starts here.</param>
/// <param name="Inputs">The slot grid with prices, PV and house load (slice 3). Goal deadlines should be slot edges (cuts); a deadline inside a slot limits that slot by its overlap.</param>
/// <param name="Goals">The applied goals (the merge's result); a goal of a device the plan does not know is ignored.</param>
/// <param name="CapacityW">The guard's static allocation limit per constraint, watts: the main phases and circuit group phases.</param>
/// <param name="MinMargin"><c>economics.min_margin</c>: a soft goal uses a class only while its value exceeds the cost by this.</param>
/// <param name="ConfigRevision">A fingerprint of the configuration the plan was made for.</param>
public sealed record PlanInput(
    DateTimeOffset Now,
    PlanInputs Inputs,
    ImmutableArray<PlanDevice> Devices,
    ImmutableArray<GoalRequest> Goals,
    ImmutableDictionary<ConstraintKey, double> CapacityW,
    PlannerParameters Parameters,
    double MinMargin,
    long ConfigRevision);

/// <summary>What a plan entry was made for: the goal (device and deadline), its revision and the device's spec fingerprint.</summary>
public sealed record PlanGoalRef(string Id, long Revision, long SpecIdentity);

/// <summary>One slot of one device in the plan.</summary>
/// <param name="Class">Wholly surplus: <c>pv</c>; any grid energy in the slot: <c>zone:&lt;id&gt;</c> of the dearest zone used (or the bare <c>zone</c> where no single zone holds the slot); null unless run or urgent.</param>
/// <param name="EnergyKwh">The slot's budget for the device.</param>
/// <param name="MaxPowerW">The ceiling for the slot: at most the device's maximum, at most what the static limits leave; zero unless run or urgent.</param>
public sealed record PlanEntry(DateTimeOffset Start, DateTimeOffset End, PlanWant Want, string? Class, double EnergyKwh, double MaxPowerW, PlanGoalRef? Goal);

/// <summary>One goal as the plan treats it.</summary>
/// <param name="PlannedKwh">Energy the plan places for the goal.</param>
/// <param name="ShortfallKwh">Need that could not be placed.</param>
/// <param name="PredictedCompletion">When the planned energy is delivered; null when a shortfall remains or nothing is planned.</param>
/// <param name="LatestGridStart">The deadline less need over the device's full power: the last instant a grid-only delivery can start; null for a goal without a hard deadline.</param>
/// <param name="Urgent">A hard goal at its latest safe start, past its deadline, infeasible, or one that had to add zones.</param>
public sealed record PlanGoal(
    PlanGoalRef Ref,
    GoalKind Kind,
    bool Hard,
    GoalStatus Status,
    DateTimeOffset? Deadline,
    double RemainingKwh,
    double PlannedKwh,
    double ShortfallKwh,
    DateTimeOffset? PredictedCompletion,
    DateTimeOffset? LatestGridStart,
    bool Urgent,
    GoalOnMet OnMet);

/// <summary>A device's goals and its entry for every slot of the plan.</summary>
public sealed record DevicePlan(DeviceName Device, ImmutableArray<PlanGoal> Goals, ImmutableArray<PlanEntry> Entries);

/// <summary>The plan's cost and two references, in currency (the label is the configuration's).</summary>
/// <param name="Planned">The plan's own cost: surplus at the export value, grid energy at the import price of its slot.</param>
/// <param name="ReferenceV2">The same goals delivered as soon as possible on the energy the device's own sources allow (what v2 does on its own).</param>
/// <param name="ReferenceAsap">The same goals delivered as soon as possible on any energy.</param>
public sealed record PlanCost(double Planned, double ReferenceV2, double ReferenceAsap);

/// <summary>
/// The immutable result of one planning (15-PLANNER): the slots, per device the goals and an entry per slot, the cost and the notes. A pure
/// function of <see cref="PlanInput"/>: <see cref="Id"/> is a fingerprint of the content, so the same input is the same plan to the byte.
/// </summary>
public sealed record PlanSnapshot
{
    public PlanSnapshot(
        DateTimeOffset computedAt,
        long configRevision,
        PlanInputs inputs,
        ImmutableArray<DevicePlan> devices,
        PlanCost cost,
        ImmutableArray<PlanNote> notes)
    {
        ComputedAt = computedAt;
        ConfigRevision = configRevision;
        Inputs = inputs;
        Devices = devices;
        Cost = cost;
        Notes = notes;
        Id = Fingerprint(this);
    }

    public string Id { get; }

    public DateTimeOffset ComputedAt { get; }

    public long ConfigRevision { get; }

    /// <summary>The slots the plan stands on, with their prices, PV and house load (and the bases behind them).</summary>
    public PlanInputs Inputs { get; }

    public ImmutableArray<DevicePlan> Devices { get; }

    public PlanCost Cost { get; }

    public ImmutableArray<PlanNote> Notes { get; }

    public DateTimeOffset HorizonEnd => Inputs.Grid.To;

    /// <summary>The slots, as <see cref="PlanInputs.Slots"/>.</summary>
    public ImmutableArray<PlanInputSlot> Slots => Inputs.Slots;

    public DevicePlan? Device(DeviceName name) => Devices.FirstOrDefault(d => d.Device == name);

    /// <summary>The entry of the slot that contains <paramref name="t"/>, or null (no such device, or outside the plan).</summary>
    public PlanEntry? EntryAt(DeviceName name, DateTimeOffset t) =>
        Device(name)?.Entries.FirstOrDefault(e => t >= e.Start && t < e.End);

    /// <summary>The same plan with other device entries (the service's adoption keeps the current slot's entries); the content fingerprint follows.</summary>
    public PlanSnapshot WithDevices(ImmutableArray<DevicePlan> devices) => new(ComputedAt, ConfigRevision, Inputs, devices, Cost, Notes);

    /// <summary>The same plan with more notes (the service's, such as a held solve); the content fingerprint follows.</summary>
    public PlanSnapshot WithNotes(IEnumerable<PlanNote> notes) =>
        new(ComputedAt, ConfigRevision, Inputs, Devices, Cost, [.. Notes.Concat(notes).Distinct().Order()]);

    /// <summary>
    /// A canonical text of everything the plan says, culture-free: equal plans give equal bytes. The determinism tests compare it, and
    /// <see cref="Id"/> is its hash.
    /// </summary>
    public string Canonical()
    {
        var text = new StringBuilder();
        CanonicalCore(text);
        return text.ToString();
    }

    private static string Fingerprint(PlanSnapshot plan)
    {
        var text = new StringBuilder();
        plan.CanonicalCore(text);
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text.ToString()))
        {
            hash = (hash ^ b) * prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string T(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "-";

    private void CanonicalCore(StringBuilder text)
    {
        text.Append("plan ").Append(T(ComputedAt)).Append(' ').Append(ConfigRevision.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(T(HorizonEnd)).Append('\n');
        text.Append("inputs ").Append(Inputs.PvBasis).Append(' ').Append(Inputs.PvCoverage).Append(' ').Append(T(Inputs.PvCoversUntil)).Append(' ')
            .Append(Inputs.LoadBasis).Append(' ').Append(Inputs.LoadDaysFilled.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(Inputs.ImportPriceBasis).Append(' ').Append(T(Inputs.ImportCurveCoversUntil)).Append('\n');
        foreach (var s in Inputs.Slots)
        {
            text.Append("slot ").Append(T(s.Slot.Start)).Append(' ').Append(T(s.Slot.End)).Append(' ').Append(s.Zone ?? "-").Append(' ')
                .Append(s.Import is { } i ? N(i.PerKwh) : "-").Append(' ').Append(s.Export is { } e ? N(e.PerKwh) : "-").Append(' ')
                .Append(N(s.PvKwh)).Append(' ').Append(N(s.BaseKwh)).Append('\n');
        }

        foreach (var device in Devices)
        {
            text.Append("device ").Append(device.Device.Value).Append('\n');
            foreach (var g in device.Goals)
            {
                text.Append("goal ").Append(g.Ref.Id).Append(' ').Append(g.Ref.Revision.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(g.Ref.SpecIdentity.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(g.Kind).Append(' ').Append(g.Hard).Append(' ').Append(g.Status).Append(' ').Append(T(g.Deadline)).Append(' ')
                    .Append(N(g.RemainingKwh)).Append(' ').Append(N(g.PlannedKwh)).Append(' ').Append(N(g.ShortfallKwh)).Append(' ')
                    .Append(T(g.PredictedCompletion)).Append(' ').Append(T(g.LatestGridStart)).Append(' ').Append(g.Urgent).Append(' ').Append(g.OnMet).Append('\n');
            }

            foreach (var e in device.Entries)
            {
                text.Append("entry ").Append(T(e.Start)).Append(' ').Append(T(e.End)).Append(' ').Append(e.Want).Append(' ').Append(e.Class ?? "-").Append(' ')
                    .Append(N(e.EnergyKwh)).Append(' ').Append(N(e.MaxPowerW)).Append(' ')
                    .Append(e.Goal is { } r ? r.Id + "/" + r.Revision.ToString(CultureInfo.InvariantCulture) + "/" + r.SpecIdentity.ToString(CultureInfo.InvariantCulture) : "-").Append('\n');
            }
        }

        text.Append("cost ").Append(N(Cost.Planned)).Append(' ').Append(N(Cost.ReferenceV2)).Append(' ').Append(N(Cost.ReferenceAsap)).Append('\n');
        text.Append("notes ").Append(string.Join(',', Notes)).Append('\n');
    }
}
