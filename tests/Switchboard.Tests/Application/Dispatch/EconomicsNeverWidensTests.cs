using System.Globalization;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// "Never widens" (ECO-14, 14-ECONOMICS "Test strategy"): over randomised prices, zones, sources, values, balances and phase loads, on the virtual
/// clock, economics may only narrow. Every increase dispatch decides must have the guard's permission on every constraint of the device, must
/// be on a class that its sources, its value and the prices allow, and an increase on surplus must still satisfy the 0.9 balance rule
/// (projection after the candidate at or above the floor) whatever order, zone or price said. The seeds are fixed, so a failure names the
/// scenario that reproduces it.
/// </summary>
public sealed class EconomicsNeverWidensTests
{
    private const int Scenarios = 80;

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
    private static readonly string?[] ExportPrices = [null, "0.20", "0.35", "0.70"];

    private static readonly DateTimeOffset[] Instants =
    [
        Workday(23), Workday(11), Workday(6, 50), Workday(6, 55), Workday(16, 30), Workday(12, 58),
        Local(2026, 10, 31, 12), Local(2026, 11, 11, 10), Local(2026, 3, 29, 3, 30), Local(2026, 7, 14, 20),
    ];

    private sealed record Scenario(int Seed, string Description, GuardScenario S, bool Counters, bool NetBilling);

    private static Scenario Build(int seed)
    {
        var rng = new Random(seed);
        var devices = new List<string>();
        var off = new List<string>();
        var parts = new List<string>();
        foreach (var (name, phase, amps, watts) in Pool.OrderBy(_ => rng.Next()).Take(rng.Next(1, 5)))
        {
            var sources = SourceChoices[rng.Next(SourceChoices.Length)];
            var value = Values[rng.Next(Values.Length)];
            devices.Add(Device(name, phase, amps, watts, value, sources));
            if (rng.Next(3) != 0)
            {
                off.Add(name);
            }

            parts.Add($"{name}[{sources ?? "-"} {value.ToString(CultureInfo.InvariantCulture)}{(off.Contains(name) ? " off" : " on")}]");
        }

        var netBilling = rng.Next(10) < 3;
        var exportPrice = ExportPrices[rng.Next(ExportPrices.Length)];
        var at = Instants[rng.Next(Instants.Length)];
        var counters = rng.Next(10) < 9;
        var wh = Balances[rng.Next(Balances.Length)];
        var export = Exports[rng.Next(Exports.Length)];
        var l1 = rng.Next(4) == 0 ? 14 + rng.Next(19) : 1;
        var l2 = rng.Next(4) == 0 ? 14 + rng.Next(19) : 1;
        var s = Create(string.Concat(devices), at, [.. off], Economics(netBilling ? "net_billing" : "hourly_netting", exportPrice));
        Export(s, export);
        Action? feed = counters ? Feed(s, wh) : null;
        feed?.Invoke();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(4), l1, l2, 1, feed);
        return new Scenario(seed, $"seed {seed}: {at:u} {(netBilling ? "net_billing" : "hourly_netting")} export price {exportPrice ?? "none"}, balance {(counters ? wh?.ToString(CultureInfo.InvariantCulture) ?? "unknown" : "unmapped")}, export {export} W, L1 {l1} A, L2 {l2} A, {string.Join(", ", parts)}", s, counters, netBilling);
    }

    private static void CheckIncreases(Scenario scenario)
    {
        var s = scenario.S;
        var minMargin = s.Configuration.Control.Economics.MinMargin;
        var floorWh = s.Configuration.Control.HourlyBalance.FloorWh;
        var minRemaining = s.Configuration.Control.HourlyBalance.MinRemaining.TotalHours;
        var increases = 0;
        for (var i = 0; i < s.Dispatches.Count; i++)
        {
            var guard = s.Evaluations[i];

            // The gate's stops come first and each lowers the load the starts are judged against (13-HOURLY-BALANCE: stops, then starts).
            var stopped = s.Dispatches[i].Wishes
                .Where(w => w.Fresh && w.Change == DispatchChange.Decrease && w.Reason is Switchboard.Domain.Decisions.ReasonCode.BalanceProjectionLow or Switchboard.Domain.Decisions.ReasonCode.BalanceUnknown or Switchboard.Domain.Decisions.ReasonCode.ValueBelowCost or Switchboard.Domain.Decisions.ReasonCode.SurplusLow)
                .Sum(w => s.DispatchSetup.Device(w.Device)!.StagePower.Sum(p => p.Value));
            var granted = -stopped;
            foreach (var wish in s.Dispatches[i].Wishes.Where(w => w.Fresh && w.Change == DispatchChange.Increase))
            {
                increases++;
                var device = s.Setup.Device(wish.Device)!;
                var watts = s.DispatchSetup.Device(wish.Device)!.StagePower.Sum(w => w.Value);

                // The guard's permission and nobody else's: no increase where an envelope entry forbids increases.
                foreach (var phase in device.Phases)
                {
                    Assert.True(guard.Envelope.For(ConstraintKey.Main(phase)) is { IncreasesAllowed: true }, $"{scenario.Description}: step {i} {wish.Device} rises on {phase} without the guard's permission");
                }

                var economics = wish.Inputs.Economics;
                Assert.True(economics is not null, $"{scenario.Description}: step {i} {wish.Device} rises with no economics block");
                var sources = s.DispatchSetup.Economics!.Device(wish.Device)!.Sources;
                var where = $"{scenario.Description}: step {i} {wish.Device}";

                // A device with no sources and no energy counters is 0.9's ungated headroom start: no class, no balance, nothing to check but the guard.
                if (sources is null && !scenario.Counters)
                {
                    Assert.Null(economics.Class);
                    granted += watts;
                    continue;
                }

                Assert.True(economics.Class is not null, $"{where} rises with no energy class");

                if (economics.Class == EnergyClass.Zone)
                {
                    Assert.True(sources is not null && economics.Zone is not null && sources.AllowsZone(economics.Zone), $"{where} rises on the zone {economics.Zone} that is not among its sources (or declares none)");
                    Assert.True(economics.ImportPrice is { } import && economics.VEff - import >= minMargin - 1e-9, $"{where} buys at {economics.ImportPrice} with a value of {economics.VEff}");
                }
                else
                {
                    // A device with no sources is the 0.9 balance rule alone: no price test (ECO-06).
                    if (sources is not null && economics.ExportPrice is { } export)
                    {
                        Assert.True(economics.VEff - export >= minMargin - 1e-9, $"{where} burns surplus worth {export} with a value of {economics.VEff}");
                    }

                    if (!scenario.NetBilling)
                    {
                        // The 0.9 rule, recomputed from what the decision recorded: the projection after this candidate may not be below the floor.
                        var balance = wish.Inputs.Balance;
                        Assert.True(balance is { BalanceWh: not null, FreeW: not null, LoadW: not null }, $"{where} rises on surplus with the balance not known");
                        var before = balance.LoadW!.Value + granted;
                        var remaining = Math.Max(balance.RemainingS / 3600, minRemaining);
                        var projection = balance.BalanceWh!.Value + (balance.FreeW!.Value - (before + watts)) * remaining;
                        Assert.True(projection >= floorWh - 1e-6, $"{where} rises on surplus and the projection after it would be {projection} Wh, below the floor {floorWh}");
                    }
                }

                if (sources is { Zones.IsEmpty: true, AllZones: false })
                {
                    Assert.True(economics.Class == EnergyClass.Pv, $"{where} is pv_only and rises on {economics.Class}");
                }

                granted += watts;
            }
        }
    }

    [Fact]
    public void Over_randomised_prices_zones_sources_and_loads_every_increase_has_the_guards_permission_and_an_allowed_class()
    {
        // Why: protection outranks everything and economics only narrows (CLAUDE.md hard rules). A bug in the order, the zone arithmetic or the price
        // test would show up first as a device rising where a guard, a source or the balance said no; this walks 80 fixed-seed worlds for that.
        var total = 0;
        for (var seed = 1; seed <= Scenarios; seed++)
        {
            var scenario = Build(seed);
            CheckIncreases(scenario);
            total += scenario.S.Decisions.Count(d => d.Action == DecisionAction.SwitchOn);
        }

        Assert.True(total >= 40, $"the scenarios decided only {total} starts; the checks would pass vacuously");
    }

    [Fact]
    public void The_randomised_worlds_do_reach_every_class_and_every_refusal()
    {
        // Why: a property test that never reaches the zone class, a surplus start or a refusal proves nothing about them. The union of the worlds
        // must contain each, so a change that silently stops one of them from ever happening fails here and not in the field.
        var classes = new HashSet<string>();
        var pending = new HashSet<string>();
        for (var seed = 1; seed <= Scenarios; seed++)
        {
            var s = Build(seed).S;
            foreach (var decision in s.Decisions.Where(d => d.Action == DecisionAction.SwitchOn && d.Inputs.Dispatch?.Economics is not null))
            {
                classes.Add(decision.Inputs.Dispatch!.Economics!.Class.ToString()!);
            }

            foreach (var state in s.Dispatches.SelectMany(d => d.Devices).Where(d => d.Pending is { } p && p.ToString() is "SourceNotAllowed" or "ValueBelowCost" or "BalanceUnknown" or "BalanceProjectionLow" or "SurplusLow"))
            {
                pending.Add(state.Pending!.Value.ToString());
            }
        }

        Assert.Contains("Pv", classes);
        Assert.Contains("Zone", classes);
        Assert.Superset(new HashSet<string> { "SourceNotAllowed", "ValueBelowCost", "BalanceUnknown", "BalanceProjectionLow" }, pending);
    }
}
