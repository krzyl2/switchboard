using Switchboard.Application.Decisions;
using Switchboard.Application.Planning;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// How far an economic stop takes a setpoint device in one step (DECISIONS 2026-11-03): straight down when the allowance would not carry even its lowest
/// level, one stage while it would. The charger of <see cref="EvRig"/> (6-16 A, no stop actuator, so its lowest level is 6 A) reports its draw, which the
/// balance needs; shadow mode on the virtual clock in the Warsaw zone with the G13-like tariff.
/// </summary>
public sealed class SetpointStopTests
{
    private const string Charger = EvRig.Charger + "    draw: { power: sensor.charger_power }\n";

    private static GuardScenario Run(DateTimeOffset from, DateTimeOffset until, double exportW, double wh, PlannerDispatchMode mode = PlannerDispatchMode.Off, PlanWant? want = null)
    {
        const double DrawA = 16;
        var view = new ScriptedPlanView(mode);
        var s = Create(Charger, from, [], Economics(), OneBoundary, plan: view);
        if (want is { } w)
        {
            view.Entries[DeviceName.Parse("ev_charger")] = ScriptedPlanView.Entry(s.Now, w, "zone:offpeak");
        }

        s.SetState("ev_charger", new SetpointApplied(new Amperes(DrawA)));
        Export(s, exportW);
        var balance = Feed(s, wh);
        void Poll()
        {
            balance();
            s.SetPowerDraw("ev_charger", Phase.L1, DrawA * 230);
            s.SetDraw("ev_charger", Phase.L1, DrawA);
        }

        Poll();
        s.PollUntil(until, 1, 1, 1, Poll);
        return s;
    }

    private static Decision FirstDecrease(GuardScenario s, DateTimeOffset after) =>
        s.DecisionsFor("ev_charger").First(d => d.At >= after && d.Action is DecisionAction.SetSetpoint or DecisionAction.SwitchOff && d.Target!.Setpoint()!.Value.Value < 16);

    [Fact]
    public void At_the_zone_end_a_charger_the_balance_cannot_carry_comes_down_at_once()
    {
        // Why: 2026-11-02 07:01 and 16:01. The off-peak zone ended, nothing but the grid fed the car, and it came down an ampere every 30 s: six minutes
        // of charging at the peak price the zone's end was there to avoid. The house imports at the lowest level too, so it goes there in one step.
        var s = Run(Workday(6, 58), Workday(7, 3), exportW: -3980, wh: -100);

        var down = FirstDecrease(s, Workday(7));

        Assert.Equal((6.0, ReasonCode.BalanceProjectionLow), (down.Target!.Setpoint()!.Value.Value, down.Reason));
    }

    [Fact]
    public void A_night_plan_defer_stops_the_charger_at_once()
    {
        // Why: 2026-11-03 01:38 and 03:53. The plan moved the car's energy to a later slot while it charged from the zone at 16 A; it then stepped down for
        // six minutes, buying exactly the energy the deferral was made to avoid. Deferred is deferred: down in one step.
        var s = Run(Workday(23), Workday(23, 3), exportW: -3980, wh: -100, mode: PlannerDispatchMode.Narrow, want: PlanWant.Defer);

        var down = FirstDecrease(s, Workday(23));

        Assert.Equal((6.0, ReasonCode.PlanDeferred), (down.Target!.Setpoint()!.Value.Value, down.Reason));
    }

    [Fact]
    public void Surplus_tracking_still_lowers_one_step_at_a_time()
    {
        // Why: on surplus the rule would carry the car at its lowest level (2.5 kW free, 1.4 kW at 6 A) but not at 16 A; stepping down follows the surplus
        // instead of dropping a load the sun still pays for, and the next step judges again.
        var s = Run(Workday(11, 10), Workday(11, 13), exportW: 2500 - (16 * 230), wh: 200);

        var down = FirstDecrease(s, Workday(11, 10));

        Assert.Equal(15, down.Target!.Setpoint()!.Value.Value);
    }
}
