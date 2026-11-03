using Switchboard.Application.Dispatch;
using Switchboard.Application.Planning;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The power bands as a standing ceiling of grid power (DECISIONS 2026-11-03, a named exception to PLAN-17): the EV charger of <see cref="EvRig"/> on the
/// virtual clock in the Warsaw zone with the G13-like tariff, the bands of the live installation (21:00-24:00 2000 W, 00:00-07:00 3000 W) and a quiet L1.
/// At 230 V the 2000 W band is 8 A (1840 W) and the 3000 W band 13 A (2990 W).
/// </summary>
public sealed class PowerBandDispatchTests
{
    private const string LiveBands = "  ev:\n    power_bands:\n      - { hours: \"21:00-24:00\", max_power_w: 2000 }\n      - { hours: \"00:00-07:00\", max_power_w: 3000 }\n";

    private static readonly DateTimeOffset Night = Workday(23);

    private static GuardScenario Play(
        DateTimeOffset at,
        string bands = LiveBands,
        string charger = EvRig.Charger,
        PlannerDispatchMode mode = PlannerDispatchMode.Narrow,
        Action<GuardScenario, ScriptedPlanView>? plan = null,
        Action<GuardScenario>? before = null,
        Action<GuardScenario>? eachPoll = null,
        double export = -500,
        double? wh = -100,
        double minutes = 3)
    {
        var view = new ScriptedPlanView(mode);
        var s = Create(charger, at, ["ev_charger"], Economics(), OneBoundary + bands, extra: "\n" + EvRig.EvBlock, plan: view);
        plan?.Invoke(s, view);
        before?.Invoke(s);
        Export(s, export);
        var balance = Feed(s, wh);
        void Poll()
        {
            balance();
            eachPoll?.Invoke(s);
        }

        Poll();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(minutes), 1, 1, 1, Poll);
        return s;
    }

    private static Action<GuardScenario, ScriptedPlanView> Entry(PlanWant want, double maxPowerW) =>
        (s, view) => view.Entries[DeviceName.Parse("ev_charger")] = ScriptedPlanView.Entry(s.Now, want, "zone:offpeak", 5, maxPowerW);

    private static double Setpoint(GuardScenario s) => s.Dispatches[^1].Wish(DeviceName.Parse("ev_charger"))!.Level.Setpoint()!.Value.Value;

    [Fact]
    public void Without_a_plan_entry_the_band_caps_grid_charging()
    {
        // Why: 2026-11-03 01:36 and 03:49. The plan had no entry for the charger for a minute (a goal revision, the replan after a safe state), dispatch fell
        // back to v2 and charged at 17-18 A through the night band the maintainer set. The band is a standing ceiling of grid power, plan or not.
        var v2 = Play(Night, bands: string.Empty);
        Assert.True(Setpoint(v2) > 8, $"v2 at {Setpoint(v2)} A");

        var banded = Play(Night);

        Assert.Equal(8, Setpoint(banded));
        Assert.Equal(ReasonCode.PowerBand, Pending(banded, "ev_charger"));
    }

    [Fact]
    public void The_band_caps_v2_too_when_the_planner_does_not_dispatch()
    {
        // Why: the band belongs to the device, not to the plan; with planner.dispatch off the maintainer's night limit holds all the same.
        Assert.Equal(8, Setpoint(Play(Night, mode: PlannerDispatchMode.Off)));
    }

    [Fact]
    public void A_run_entry_ceiling_supersedes_the_band()
    {
        // Why: the planner keeps to the band while the charge fits its window under it and lifts it only for a deadline; its slot ceiling is that decision,
        // so the band does not cap it a second time.
        var s = Play(Night, plan: Entry(PlanWant.Run, maxPowerW: 2990));

        Assert.True(Setpoint(s) > 8, $"at {Setpoint(s)} A");
        Assert.NotEqual(ReasonCode.PowerBand, Pending(s, "ev_charger"));
    }

    [Fact]
    public void A_run_entry_with_an_unusable_ceiling_falls_back_to_the_band()
    {
        // Why: an entry whose ceiling is not a number decides nothing about the power; the device must not run uncapped for it.
        Assert.Equal(8, Setpoint(Play(Night, plan: Entry(PlanWant.Run, maxPowerW: double.NaN))));
    }

    [Fact]
    public void Charge_now_is_capped_only_until_its_plan_entry_is_in_force()
    {
        // Why: charge-now reaches dispatch as an urgent plan entry; until the replan publishes it the band holds, and from then on the entry decides.
        ScriptedPlanView? view = null;
        var s = Play(Night, plan: (_, v) => view = v);
        Assert.Equal(8, Setpoint(s));

        view!.Entries[DeviceName.Parse("ev_charger")] = ScriptedPlanView.Entry(s.Now, PlanWant.Urgent, "zone:offpeak", 5, 3680);
        Run(s, Feed(s, -100), 3);

        Assert.True(Setpoint(s) > 8, $"at {Setpoint(s)} A");
    }

    [Fact]
    public void Surplus_not_used_by_other_devices_is_not_limited_by_the_band()
    {
        // Why: a band limits what the car takes from the grid; solar surplus on top of it is free energy the band was never meant to refuse.
        // The 2000 W band alone allows 8 A; anything above it is the surplus the band adds back. The charger reports its draw (the surplus is known only
        // with every own load measured), at its floor: shadow mode applies nothing.
        var s = Play(
            Workday(11),
            bands: "  ev:\n    power_bands:\n      - { hours: \"10:00-14:00\", max_power_w: 2000 }\n",
            charger: EvRig.Charger + "    draw: { power: sensor.charger_power }\n",
            eachPoll: s =>
            {
                s.SetPowerDraw("ev_charger", Phase.L1, 6 * 230);
                s.SetDraw("ev_charger", Phase.L1, 6);
            },
            export: 3500,
            wh: 600);

        Assert.True(Setpoint(s) > 8, $"at {Setpoint(s)} A");
    }

    [Fact]
    public void An_override_is_not_capped_by_the_band()
    {
        // Why: a manual override is the maintainer's explicit word for this device now; the band is the default it overrides (protection still is not).
        var s = Play(Night, before: s => s.Override("ev_charger", OverrideMode.ForceOn));

        Assert.True(Setpoint(s) > 8, $"at {Setpoint(s)} A");
    }
}
