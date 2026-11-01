using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Domain;

/// <summary>
/// Pins the planner's output over a fixed family of generated worlds, recorded on the unsplit planner: energy and SoC goals, hard and soft, spread, quiet and
/// stop goals, past, near and beyond-horizon deadlines, pv-only, zone and any sources, relay, modulating and storage devices, three-phase shares, capacity
/// limits, and the 23- and 25-hour days. The plan's <see cref="PlanSnapshot.Id"/> is the hash of everything it says, so a refactoring of the planner that
/// moves one kWh, one entry or one status in one world fails here, with the world's number.
/// </summary>
public sealed class PlannerPlanPinTests
{
    private static readonly DateTimeOffset[] Starts = [Local(2026, 10, 24, 20), Local(2026, 10, 25, 0), Local(2026, 3, 28, 21), Local(2026, 3, 29, 0), Local(2026, 10, 26, 18)];

    private static readonly DeviceName[] Names = [.. new[] { "dev_a", "dev_b", "dev_c", "dev_d" }.Select(DeviceName.Parse)];

    [Fact]
    public void The_plan_of_every_generated_world_is_the_one_recorded_before_the_split()
    {
        // Why: the planner's split is a refactoring; its contract is that no plan changes by a byte. The worlds are fixed by a seed so the pin is stable.
        var ids = Enumerable.Range(0, PlannerPinnedIds.All.Length).Select(i => Solve(World(i)).Id).ToArray();

        for (var i = 0; i < ids.Length; i++)
        {
            Assert.True(PlannerPinnedIds.All[i] == ids[i], $"world {i}: plan {ids[i]} (recorded {PlannerPinnedIds.All[i]})");
        }
    }

    [Fact]
    public void The_worlds_reach_every_status_every_want_and_the_spread_pass()
    {
        // Why: a pin over worlds that never reach a branch pins nothing there; this keeps the family honest if it is ever regenerated.
        var plans = Enumerable.Range(0, PlannerPinnedIds.All.Length).Select(i => Solve(World(i))).ToList();
        var statuses = plans.SelectMany(p => p.Devices).SelectMany(d => d.Goals).Select(g => g.Status).ToHashSet();
        var wants = plans.SelectMany(p => p.Devices).SelectMany(d => d.Entries).Select(e => e.Want).ToHashSet();
        var notes = plans.SelectMany(p => p.Notes).ToHashSet();

        foreach (var status in new[] { GoalStatus.OnTrack, GoalStatus.AtRisk, GoalStatus.Infeasible, GoalStatus.Met, GoalStatus.Expired, GoalStatus.Missed })
        {
            Assert.Contains(status, statuses);
        }

        foreach (var want in Enum.GetValues<PlanWant>())
        {
            Assert.Contains(want, wants);
        }

        Assert.Contains(PlanNote.GoalShortfall, notes);
        Assert.Contains(PlanNote.GoalUrgent, notes);
        Assert.Contains(PlanNote.GoalMissed, notes);
        Assert.Contains(PlanNote.GoalInfeasible, notes);
        Assert.Contains(plans, p => p.Slots.Any(s => s.Slot.Hours > 0 && s.Slot.Start.Offset != p.Slots[0].Slot.Start.Offset));
        Assert.True(plans.Any(p => SpreadEntries(p) > 0), "no world reaches the spread pass");
    }

    [Fact]
    public void Every_generated_plan_passes_the_verifier()
    {
        // Why: protection outranks the plan; whatever the split does, no world may produce a plan the verifier refuses.
        for (var i = 0; i < PlannerPinnedIds.All.Length; i++)
        {
            var input = World(i);
            Assert.Empty(PlanVerifier.Verify(input, Solve(input)));
        }
    }

    /// <summary>Entries that carry a power below their device's ceiling: only the spread pass writes one.</summary>
    private static int SpreadEntries(PlanSnapshot plan) =>
        plan.Devices.SelectMany(d => d.Entries).Count(e => e.Want == PlanWant.Run && e.MaxPowerW > 0 && e.MaxPowerW < 2000);

    private static PlanInput World(int index)
    {
        var rng = new Random(1000 + index);
        var now = Starts[index % Starts.Length].AddMinutes(index % 3 == 0 ? 20 : 0);
        var slots = 6 + rng.Next(0, 30);
        var zones = new[] { "off", "peak", "mid" };
        var prices = new[] { 0.6, 1.1, 0.8 };
        var flatPrice = rng.Next(0, 4) == 0;
        var inputs = Custom(now, slots, i =>
        {
            var z = flatPrice ? 0 : ((i / 3) + index) % 3;
            var noImport = rng.Next(0, 12) == 0;
            var surplus = rng.Next(0, 3) == 0 ? Math.Round(rng.NextDouble() * 3, 3) : 0;
            return (noImport ? null : zones[z], noImport ? null : prices[z], rng.Next(0, 10) == 0 ? null : 0.2 + (0.05 * rng.Next(0, 4)), surplus);
        });

        var devices = new List<PlanDevice>();
        var deviceCount = 1 + rng.Next(0, 4);
        for (var d = 0; d < deviceCount; d++)
        {
            var kind = rng.Next(0, 6) switch { 0 => PlanDeviceKind.Relay, 1 => PlanDeviceKind.Storage, _ => PlanDeviceKind.Modulating };
            ImmutableArray<PlanKeyShare> keys = rng.Next(0, 4) == 0 ? [new PlanKeyShare(L1, 1.0 / 3), new PlanKeyShare(L2, 1.0 / 3), new PlanKeyShare(L3, 1.0 / 3)] : [new PlanKeyShare(new[] { L1, L2, L3 }[rng.Next(0, 3)], 1)];
            var max = 1500 + (rng.Next(0, 4) * 500);
            var min = kind == PlanDeviceKind.Relay ? max : rng.Next(0, 3) == 0 ? 0 : 1000;
            var follows = rng.Next(0, 5) switch { 0 => ValueFollows.ImportPrice, 1 => ValueFollows.ExportPrice, _ => ValueFollows.Static };
            SourceSet? sources = rng.Next(0, 5) switch { 0 => SourceSet.PvOnly, 1 => null, 2 => Offpeak, _ => SourceSet.Any };
            devices.Add(new PlanDevice(
                Names[d],
                kind,
                min,
                max,
                TimeSpan.FromMinutes(rng.Next(0, 3) * 30),
                keys,
                rng.Next(0, 8) != 0,
                new ValueSpec(follows == ValueFollows.Static ? 0.3 + (rng.Next(0, 20) * 0.1) : 0.5, follows, 1 + rng.NextDouble()),
                sources,
                d + 1,
                kind == PlanDeviceKind.Modulating && rng.Next(0, 2) == 0 ? 230 : null));
        }

        var goals = new List<GoalRequest>();
        var goalCount = rng.Next(0, 6);
        for (var g = 0; g < goalCount; g++)
        {
            var device = Names[rng.Next(0, deviceCount)];
            var deadline = rng.Next(0, 9) switch
            {
                0 => (DateTimeOffset?)null,
                1 => now.AddHours(-rng.Next(1, 3)),
                2 => now.AddHours(slots + rng.Next(1, 20)),
                _ => now.AddHours(1 + rng.Next(0, slots)).AddMinutes(rng.Next(0, 4) * 15),
            };
            var hard = rng.Next(0, 2) == 0;
            var energy = rng.Next(0, 2) == 0;
            var remaining = rng.Next(0, 14) == 0 ? 0 : Math.Round(0.5 + (rng.NextDouble() * 14), 3);
            GoalTarget target = energy ? new EnergyTarget(90, 0, default) : new SocTarget(90, 40);
            SourceSet? sources = rng.Next(0, 4) switch { 0 => SourceSet.PvOnly, 1 => Offpeak, 2 => null, _ => SourceSet.Any };
            goals.Add(new GoalRequest(
                device,
                target,
                remaining,
                deadline,
                TimeSpan.FromMinutes(rng.Next(0, 3) * 30),
                hard,
                sources,
                rng.Next(0, 4) != 0,
                rng.Next(0, 3) == 0 ? GoalOnMet.Stop : GoalOnMet.Opportunistic,
                GoalOrigin.Config,
                1 + rng.Next(0, 4),
                rng.Next(0, 3) == 0,
                rng.Next(0, 6) == 0));
        }

        var capacity = Capacity(2500 + (rng.Next(0, 5) * 500));
        var parameters = Parameters(refinePasses: rng.Next(0, 4));
        return Input(now, inputs, devices, goals, capacity, parameters, rng.Next(0, 3) == 0 ? 0.0 : 0.05, 3);
    }
}

internal static class PlannerPinnedIds
{
    public static readonly string[] All =
    [
        "d7b500a03333b9c4",
        "20ca0248d1622438",
        "0f9f0fd6d7257a06",
        "6c6b9e173d8e00f6",
        "9fb489f802fb8508",
        "0a772cb56f369433",
        "821bc1319e0c7d1c",
        "f608e8a40ee0bbda",
        "1c99c0e007a9cebd",
        "91c1bc20420d28aa",
        "21d73a4bfbb8ed9e",
        "8bd4bdff58bf85c3",
        "f395c73193bf21af",
        "c5e8352c1e086937",
        "a101fd5593683759",
        "45832c29d59e6d13",
        "44207c8859784bbb",
        "53fa7abe210aa129",
        "ec9a3b6773aab289",
        "d6592ad4864d5df9",
        "c4a1acea47e1b85c",
        "b46f77ed8f115d51",
        "5b3dfff5aa3c41ac",
        "7ac165c61220d5d9",
        "52af460c75c32f0f",
        "8792c03260a7cf9b",
        "9f3693a030a4b2e7",
        "3e156a3b05b59421",
        "0499769c8698fc53",
        "54740d490b71486c",
        "98d613ccef7f5139",
        "04fc6f1fa0e88181",
        "07f9df97126eec2d",
        "593db7936e981cb0",
        "b6d75d500b0f2814",
        "eda990343e0bbbe7",
        "14e04d278bb8b29f",
        "82aaff80a06b0173",
        "2da0150e6f63247a",
        "dfc321c00d5395b4",
        "cec8faa6b5f715a1",
        "e0224107d730509d",
        "ba3d12e454e28e47",
        "739ccd69b9be0c1e",
        "ab00f7b48497b1e6",
        "05f587783e3c276a",
        "1484a5e5066fb058",
        "f5ed25281df9a7f3",
        "f53fa1f9ff0aadb9",
        "1a1ea418570d81d9",
        "c873048d1acd3739",
        "e3a2952439844351",
        "256e54623e07968b",
        "3d80ddbb7719f437",
        "4efa8e08a8a28fef",
        "d8d565f50d6dc9b2",
        "199ae1f7137f9bca",
        "c6360e28c0effc7d",
        "92d7d6563877aa2f",
        "91f3e1465c7b1e01",
        "3c02f301c59a1187",
        "55604f0010a3fcf0",
        "13e4efb5c0c0d834",
        "4ce0bd2fbb7db31b",
        "5c9daa4894ee9c21",
        "10441cf886085955",
        "0ad90ea239113e40",
        "d69951da3e39fffc",
        "09565b870a663e5b",
        "9bf5d32693080b53",
        "04fb5f55064a607c",
        "ed834c41470d8402",
        "67d55775ce4b1305",
        "0488c4975304921e",
        "f2a30c5d3a0db900",
        "a7cdbfdeeb78aaa5",
        "3233a3a07450d9f6",
        "97378136c8ac201d",
        "ed52a1a5e0cc8b6b",
        "3d7667a041cf64fd",
        "677818cb2e1d8032",
        "f91dac5a321f09ab",
        "f9625737b78f2cb3",
        "789f44dbec35fba8",
        "21c2f50ea2cf1ce6",
        "236a576985aecb67",
        "2e61c994d6bf28c9",
        "9dae22e979963623",
        "2cead1128a62e339",
        "63938345d98d9b97",
        "8418a7628440ecde",
        "f201e6f890976f1c",
        "7396d9f87d010a46",
        "8c646d72404c3d76",
        "29bdfb31ef2265e7",
        "aa036de95304a2ff",
        "0446cbe24bd79d09",
        "0349314209d14584",
        "0825dd6b1e4542f2",
        "d22544d3b3b69ad2",
        "d380adbc6d556107",
        "bbcad7881f70384a",
        "1c06937caac12cd0",
        "72d35adced6c4e0e",
        "d293bcfefe79ff5d",
        "e94b50eb7564dea4",
        "279098d26780e23a",
        "2bb38c5fb8b87901",
        "242f5193e2a40c27",
        "776b027e18e866cd",
        "46c4d5ea1bf6bdd1",
        "ed85c5cd01eda71f",
        "22a88c89f23cce8a",
        "658dc654fdf60ff6",
        "6d937a6235308a6d",
        "79776dc4edbc6d00",
        "28900fea285fec1b",
        "795b51bd422bb0ee",
        "13affc6148724419",
        "290bd54c0e71765b",
    ];
}
