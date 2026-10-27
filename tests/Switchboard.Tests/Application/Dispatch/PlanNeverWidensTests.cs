using System.Globalization;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Planning;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The dispatch property test of 15-PLANNER ("Never causes an allocation the envelope refuses"), over random worlds on the virtual clock with a fixed seed: whatever the
/// plan says, valid or garbage (a NaN ceiling, a negative one, an infinite one, a budget of zero), every increase dispatch decides has the guard's permission on each
/// constraint and the headroom the allocation promised, a restore_only device is never started, a plan in narrow mode adds no class, and in full mode the only
/// increases that exist because of the plan are the two it is allowed to add, for the devices and entries it is allowed to add them for. Why it matters: the plan is
/// the first optimisation to sit in front of dispatch, and protection outranks everything (CLAUDE.md hard rules).
/// </summary>
public sealed class PlanNeverWidensTests
{
    private const int Scenarios = 600;

    private static readonly (string Name, string Phase, double Amps, double Watts)[] Pool =
    [
        ("heater_l1", "L1", 6.6, 1500),
        ("heater_l2", "L2", 6.6, 1500),
        ("heater_l3", "L3", 6.6, 1500),
        ("ev_charger", "L2", 4, 920),
    ];

    private static readonly string?[] SourceChoices = [null, "pv_only", "pv_and_zone:offpeak", "any"];
    private static readonly double[] Values = [0.2, 0.35, 0.6, 1.2, 2.03];
    private static readonly double[] Exports = [-1500, 0, 1500, 3000, 6000];
    private static readonly double?[] Balances = [-200, 0, 100, 300, 600, 900, null];
    private static readonly double[] Energies = [0, 0.01, 1, 5, double.NaN];
    private static readonly double[] Ceilings = [0, 1000, 1500, 3000, 10_000, double.NaN, -5, double.PositiveInfinity];
    private static readonly string?[] Classes = [null, "pv", "zone", "zone:offpeak", "zone:morning_peak"];

    private static readonly DateTimeOffset[] Instants =
    [
        Workday(23), Workday(11), Workday(6, 50), Workday(6, 55), Workday(16, 30), Workday(12, 58),
        Local(2026, 10, 31, 12), Local(2026, 11, 11, 10), Local(2026, 3, 29, 3, 30), Local(2026, 7, 14, 20),
    ];

    private sealed record Scenario(int Seed, string Description, GuardScenario S, PlannerDispatchMode Mode, Dictionary<DeviceName, PlanEntryView> Entries, HashSet<DeviceName> RestoreOnly);

    private static PlanEntryView? RandomEntry(Random rng, DateTimeOffset now)
    {
        var want = rng.Next(7) switch
        {
            0 or 1 => (PlanWant?)null,
            2 => PlanWant.Defer,
            3 => PlanWant.Done,
            4 or 5 => PlanWant.Run,
            _ => PlanWant.Urgent,
        };
        return want is null
            ? null
            : ScriptedPlanView.Entry(now, want.Value, Classes[rng.Next(Classes.Length)], Energies[rng.Next(Energies.Length)], Ceilings[rng.Next(Ceilings.Length)], rng.Next(3) != 0);
    }

    private static Scenario Build(int seed)
    {
        var rng = new Random(seed);
        var devices = new List<string>();
        var off = new List<string>();
        var restoreOnly = new HashSet<DeviceName>();
        var parts = new List<string>();
        foreach (var (name, phase, amps, watts) in Pool.OrderBy(_ => rng.Next()).Take(rng.Next(1, 5)))
        {
            var sources = SourceChoices[rng.Next(SourceChoices.Length)];
            var value = Values[rng.Next(Values.Length)];
            var restore = rng.Next(5) == 0;
            devices.Add(Device(name, phase, amps, watts, value, sources, restore ? "    start: restore_only\n" : string.Empty));
            if (restore)
            {
                restoreOnly.Add(DeviceName.Parse(name));
            }

            if (rng.Next(3) != 0)
            {
                off.Add(name);
            }

            parts.Add($"{name}[{sources ?? "-"} {value.ToString(CultureInfo.InvariantCulture)}{(restore ? " restore" : string.Empty)}{(off.Contains(name) ? " off" : " on")}]");
        }

        var mode = rng.Next(2) == 0 ? PlannerDispatchMode.Narrow : PlannerDispatchMode.Full;
        var view = new ScriptedPlanView(mode);
        var at = Instants[rng.Next(Instants.Length)];
        var wh = Balances[rng.Next(Balances.Length)];
        var export = Exports[rng.Next(Exports.Length)];
        var l1 = rng.Next(4) == 0 ? 14 + rng.Next(19) : 1;
        var l2 = rng.Next(4) == 0 ? 14 + rng.Next(19) : 1;
        var s = Create(string.Concat(devices), at, [.. off], Economics(rng.Next(10) < 3 ? "net_billing" : "hourly_netting", "0.35"), plan: view);
        var entries = new Dictionary<DeviceName, PlanEntryView>();
        foreach (var (name, _, _, _) in Pool)
        {
            if (RandomEntry(rng, s.Now) is { } entry)
            {
                entries[DeviceName.Parse(name)] = entry;
                view.Entries[DeviceName.Parse(name)] = entry;
            }
        }

        Export(s, export);
        var feed = Feed(s, wh);
        feed();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(4), l1, l2, 1, feed);
        var entryText = string.Join(", ", entries.Select(e => $"{e.Key}:{e.Value.Want}/{e.Value.Class ?? "-"}/{e.Value.EnergyKwh}/{e.Value.MaxPowerW}/{e.Value.GoalAllowsZone}"));
        return new Scenario(seed, $"seed {seed}: {at:u} {mode} export {export} W, balance {wh?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, L1 {l1} A, L2 {l2} A, {string.Join(", ", parts)}; plan {entryText}", s, mode, entries, restoreOnly);
    }

    private static double Watts(GuardScenario s, DeviceName device, DeviceLevel level) =>
        s.DispatchSetup.Device(device)!.StagePower.Where((_, i) => !level.Relays.IsDefault && i < level.Relays.Length && level.Relays[i]).Sum(w => w.Value);

    [Fact]
    public void Over_random_worlds_and_random_plans_every_increase_keeps_the_guards_permission_the_headroom_and_the_plans_narrow_limits()
    {
        // Why: the promise the whole slice rests on, tested where it could break: a plan that is wrong, stale in spirit or hostile must still never start or raise a
        // load the envelope or the allocation would refuse, never start a restore_only device, never open a class in narrow, and in full open only the two classes
        // for the entries and devices the design allows. The seed is fixed, so a failure names a world that can be replayed.
        var increases = 0;
        var planOpened = 0;
        var urgent = 0;
        for (var seed = 1; seed <= Scenarios; seed++)
        {
            var scenario = Build(seed);
            var s = scenario.S;
            for (var i = 0; i < s.Dispatches.Count; i++)
            {
                var guard = s.Evaluations[i];
                foreach (var wish in s.Dispatches[i].Wishes.Where(w => w.Fresh && w.Change == DispatchChange.Increase))
                {
                    increases++;
                    var spec = s.Setup.Device(wish.Device)!;
                    var where = $"{scenario.Description}: step {i} {wish.Device}";

                    foreach (var phase in spec.Phases)
                    {
                        Assert.True(guard.Envelope.For(ConstraintKey.Main(phase)) is { IncreasesAllowed: true }, $"{where} rises on {phase} without the guard's permission");
                    }

                    // The allocation's own promise: its ceiling and the start hysteresis fit in what was left on every constraint when its turn came.
                    foreach (var headroom in wish.Inputs.Headroom.Where(h => h.Remaining is not null))
                    {
                        var ceiling = DeviceLevels.Ceiling(spec, wish.Level, headroom.Key.Phase).Value;
                        Assert.True(headroom.Remaining!.Value.Value - ceiling >= wish.Inputs.StartHysteresis.Value - 1e-9, $"{where} rises into {headroom.Key} with {headroom.Remaining} left for a ceiling of {ceiling}");
                    }

                    Assert.False(scenario.RestoreOnly.Contains(wish.Device), $"{where} is restore_only and rises");

                    var entry = scenario.Entries.GetValueOrDefault(wish.Device);
                    var economics = wish.Inputs.Economics;
                    var byPlan = wish.Reason is ReasonCode.PlanSlotTarget or ReasonCode.GoalDeadlineUrgent;
                    if (byPlan)
                    {
                        planOpened++;
                        urgent += wish.Reason == ReasonCode.GoalDeadlineUrgent ? 1 : 0;
                        Assert.True(scenario.Mode == PlannerDispatchMode.Full, $"{where} rises on the plan's own class in narrow mode");
                        Assert.NotNull(entry);
                        Assert.Equal(EnergyClass.Zone, economics!.Class);
                        Assert.True(wish.Reason == ReasonCode.GoalDeadlineUrgent
                            ? entry.Want == PlanWant.Urgent
                            : entry.Want == PlanWant.Run && PlanClasses.IsZone(entry.Class) && entry.GoalAllowsZone, $"{where} rises on the plan class for an entry that does not allow it ({entry.Want}, {entry.Class})");
                    }
                    else if (economics?.Sources is { } sources && economics.Class == EnergyClass.Zone)
                    {
                        // Not the plan's doing: v2's own rule for a zone start still holds (the zone is one of the device's sources).
                        Assert.True(economics.Zone is not null && new SourceSetParse(sources).Allows(economics.Zone), $"{where} rises on the zone {economics.Zone} that is not among its sources {sources}");
                    }

                    if (entry is { Want: PlanWant.Done })
                    {
                        Assert.Fail($"{where} rises under a Done entry");
                    }

                    if (entry is { Want: PlanWant.Defer } && economics?.Class == EnergyClass.Zone)
                    {
                        Assert.Fail($"{where} rises on a zone under a Defer entry");
                    }

                    if (entry is { Want: PlanWant.Run or PlanWant.Urgent } && double.IsFinite(entry.MaxPowerW) && entry.MaxPowerW >= 0)
                    {
                        Assert.True(Watts(s, wish.Device, wish.Level) <= entry.MaxPowerW + 1e-6, $"{where} rises to {Watts(s, wish.Device, wish.Level)} W over the plan's ceiling {entry.MaxPowerW}");
                    }

                    if (entry is { Want: PlanWant.Run } && entry.EnergyKwh <= 0)
                    {
                        Assert.Fail($"{where} rises under a Run entry whose budget is spent");
                    }
                }
            }
        }

        Assert.True(increases > 150, $"the worlds were too quiet to mean anything ({increases} increases)");
        Assert.True(planOpened > 0 && urgent > 0, $"no world exercised the plan's classes ({planOpened} opened, {urgent} urgent)");
    }

    /// <summary>The sources a decision recorded, parsed back for the one question asked of them.</summary>
    private readonly struct SourceSetParse(string text)
    {
        public bool Allows(string zone) => SourceSet.TryParse(text, out var set, out _) && set!.AllowsZone(zone);
    }
}
