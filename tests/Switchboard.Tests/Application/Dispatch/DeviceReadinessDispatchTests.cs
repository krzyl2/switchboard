using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// 2026-11-08 on the live installation: the car left at 10:08, the go-e kept being enabled and raised to 20 A on the surplus, and the hourly balance counted
/// those 4.6 kW as load. The heaters started only when about 1 kWh of real balance had built up in the hour, were stopped at every :00, and about 3.5 kWh were
/// sold instead of heating water. Two rules came of it: a charger with no car plugged in is not ready (dispatch keeps it off, the energy goes to the devices
/// behind it, and once the car is plugged in the value order hands the energy back to it), and a device that measures its own draw counts at that draw in the
/// projection once its command has settled in active mode. The world is the live one at 11:00 on a workday: three 1.7 kW heaters worth 0.35 that may only take
/// surplus, a go-e worth 2.03 on L1 with a force-state stop and its own power draw, 2.5 kW exported and the hour's balance at zero.
/// </summary>
public sealed class DeviceReadinessDispatchTests
{
    private const string Charger =
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    sources: pv_only\n"
        + "    stop_actuator: select.charger_force\n    stop_option: \"1\"\n    resume_option: \"0\"\n    state_reports: on_change\n    draw: { power: sensor.charger_power }\n";

    private static readonly string LiveDevices =
        Device("heater_l1", "L1", 7, 1700, 0.35, "pv_only")
        + Device("heater_l2", "L2", 7, 1700, 0.35, "pv_only")
        + Device("heater_l3", "L3", 7, 1700, 0.35, "pv_only")
        + Charger;

    private static readonly string[] Heaters = ["heater_l1", "heater_l2", "heater_l3"];
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    private static GuardScenario World(bool active = false, RecordingDeviceCommandPort? port = null, double chargerAmps = 0)
    {
        var s = DispatchScenario.Create(LiveDevices, Heaters, OneBoundary, mode: active ? DispatchScenario.Active : null, port: port, start: Workday(11), extra: "\n" + Economics());
        s.Time.Zone = Warsaw;
        if (port is not null)
        {
            s.Follow(port);
        }

        // The charger as the go-e reports it: off by its force state, or enabled at a setpoint.
        s.SetState(Car, 1, new SetpointApplied(new Amperes(chargerAmps > 0 ? chargerAmps : 6)));
        s.SetState(Car, DeviceStateSample.StopStage, new RelayApplied(chargerAmps > 0));
        Export(s, 2500);
        return s;
    }

    /// <summary>The balance of the hour and the go-e's own draw, which stays at zero: no car takes anything.</summary>
    private static Action Poll(GuardScenario s, double? wh = 0)
    {
        var balance = Feed(s, wh);
        return () =>
        {
            balance();
            s.SetPowerDraw("ev_charger", Phase.L1, 0);
            s.SetDraw("ev_charger", Phase.L1, 0);
        };
    }

    private static ReasonCode? Reason(GuardScenario s, string device) => s.Dispatches[^1].Devices.Single(d => d.Device.Value == device).Reason;

    [Fact]
    public void A_charger_with_no_car_plugged_in_does_not_start_and_its_surplus_goes_to_the_heaters()
    {
        // Why: the car worth 2.03 comes first in the value order, so with it "present" every surplus start goes to it. With no car it takes nothing, and
        // a heater must get the energy that would otherwise be sold.
        var plugged = World();
        Run(plugged, Poll(plugged), 3);
        Assert.True(Starts(plugged, "ev_charger").Any(), $"{Reason(plugged, "ev_charger")} pending {Pending(plugged, "ev_charger")} wish {plugged.Dispatches[^1].Wish(Car)!.Level}");

        var away = World();
        away.NotReady = [Car];
        Run(away, Poll(away), 3);

        Assert.Empty(Starts(away, "ev_charger"));
        Assert.Equal(ReasonCode.DeviceNotReady, Reason(away, "ev_charger"));
        var heater = Assert.Single(Starts(away));
        Assert.Equal(ReasonCode.BalanceSurplus, heater.Reason);
    }

    [Fact]
    public void A_charger_that_runs_when_the_car_is_unplugged_comes_down_with_device_not_ready()
    {
        // Why: the car can leave while the charger is enabled; Switchboard must not keep it enabled (and its setpoint budgeted on L1) for a car that is gone.
        var s = World(chargerAmps: 10);
        s.NotReady = [Car];
        Run(s, Poll(s), 2);

        var stop = Assert.Single(Stops(s, "ev_charger"));
        Assert.Equal(ReasonCode.DeviceNotReady, stop.Reason);
    }

    [Fact]
    public void When_the_car_is_plugged_in_the_energy_moves_from_the_heaters_to_the_car()
    {
        // Why: the maintainer's words (2026-11-09): once the car is plugged in Switchboard learns it and moves the energy from the heaters to the car. The
        // car is worth more, so it displaces the running heater once that heater's min_on has run, and then starts on the surplus the heater leaves.
        var s = World();
        s.NotReady = [Car];
        Run(s, Poll(s), 2);
        var heater = Assert.Single(Starts(s));

        s.NotReady = [];
        Run(s, Poll(s), 10);

        Assert.Contains(s.DecisionsFor(heater.Device!.Value.Value), d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.ValueRankBelowCutoff);
        var car = Assert.Single(Starts(s, "ev_charger"));
        Assert.Equal(ReasonCode.BalanceSurplus, car.Reason);
        Assert.True(car.At > heater.At + TimeSpan.FromMinutes(6), "the heater was displaced inside its minimum on time");
    }

    [Fact]
    public void A_charger_that_draws_nothing_at_its_setpoint_counts_at_its_draw_once_its_command_has_settled()
    {
        // Why: a full car (go-e "Complete") is plugged in and takes nothing at 16 A. Counting the setpoint, 3.7 kW, made the projection negative, so no
        // heater could start and the charger itself was lowered on a load that did not exist. In active mode, with nothing in flight, its measured 0 W counts.
        var port = new RecordingDeviceCommandPort();
        var s = World(active: true, port: port, chargerAmps: 16);
        Run(s, Poll(s), 3);

        Assert.Empty(Stops(s, "ev_charger"));
        var heater = Assert.Single(Starts(s));
        Assert.Equal(ReasonCode.BalanceSurplus, heater.Reason);

        // The decision says what the rule judged it with: the charger at its 0 W, nothing else running yet.
        var balance = heater.Inputs.Dispatch!.Balance!;
        Assert.Equal(0, balance.RuleLoadW);
        Assert.True(balance.RuleProjectionWh > balance.HiWh, $"projection {balance.RuleProjectionWh} Wh");
    }

    [Fact]
    public void In_shadow_mode_the_charger_still_counts_at_its_setpoint()
    {
        // Why: in shadow mode nothing is applied, so a measured draw says nothing about what the decided level would draw; the log must read as the
        // balance would drive the devices (13-HOURLY-BALANCE). The rule judges the charger's own level with its 3.7 kW.
        var s = World(chargerAmps: 16);
        Run(s, Poll(s), 0.5);

        var lowered = s.DecisionsFor("ev_charger").First(d => d.Reason == ReasonCode.BalanceProjectionLow);
        Assert.Equal(16 * 230, lowered.Inputs.Dispatch!.Balance!.RuleLoadW);
    }
}
