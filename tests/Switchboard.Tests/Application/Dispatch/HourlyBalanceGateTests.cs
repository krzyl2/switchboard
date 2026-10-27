using Switchboard.Application.Balance;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Signals;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The hourly balance as the dispatch gate of the devices that declare start: headroom (BAL-04, BAL-05,
/// 13-HOURLY-BALANCE): the utility bills the energy netted per hour, so a start is wanted only while the
/// balance projected to the next :00 stays above the band, a stop when it falls below the floor, and with
/// the balance not known nothing starts at all. The balance only narrows what dispatch may do: the
/// guard's envelope and the allocation stay in front of it. Defaults: floor 150 Wh, ceiling 525 Wh, burn
/// window 15 min; 1500 W heaters; the scenario clock starts at a :00.
/// </summary>
public sealed class HourlyBalanceGateTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly string[] AllHeaters = ["heater_l1", "heater_l2", "heater_l3"];
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n    persistence_down_cycles: 1\n";

    private static Action Feed(GuardScenario s, double? wh, DateTimeOffset hourStart, TimeSpan? age = null) => () =>
    {
        s.Energy = new EnergyInputs(new EnergyCounters(0, 0, s.Now), TimeSpan.FromSeconds(90));
        s.Balance = new HourlyBalanceStatus(
            new BalanceReading(wh, hourStart, wh is null ? BalanceUnknown.Stale : BalanceUnknown.None),
            s.Now - (age ?? TimeSpan.Zero),
            hourStart.AddHours(1) - s.Now);
    };

    /// <summary>The grid exports this much in total, split over the phases (net import is negative).</summary>
    private static void Export(GuardScenario s, double watts)
    {
        foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
        {
            s.SetLoad(phase, -watts / 3);
        }
    }

    /// <summary>The grid imports nothing and exports nothing: whatever the house draws is covered on the spot.</summary>
    private static void Neutral(GuardScenario s) => Export(s, 0);

    private static GuardScenario Heaters(string[] off, string control = OneBoundary) => DispatchScenario.Create(DispatchDevices.Heaters, off, control);

    private static void Run(GuardScenario s, Action feed, TimeSpan duration, double l1 = 5)
    {
        feed();
        s.PollUntil(s.Now + duration, l1, 5, 5, feed);
    }

    private static IEnumerable<Decision> Starts(GuardScenario s, string? device = null) =>
        (device is null ? s.Decisions : s.DecisionsFor(device)).Where(d => d.Action == DecisionAction.SwitchOn);

    private static IEnumerable<Decision> Stops(GuardScenario s) => s.Decisions.Where(d => d.Action == DecisionAction.SwitchOff);

    [Fact]
    public void A_surplus_above_the_band_starts_a_heater_for_the_balance()
    {
        var s = Heaters(AllHeaters);
        Export(s, 3000);

        // 600 Wh and 3 kW exported with 55 min to go: 3.3 kWh projected, 1.9 kWh still left after the heater.
        Run(s, Feed(s, 600, T0), TimeSpan.FromMinutes(5));

        var started = Starts(s).ToList();
        Assert.NotEmpty(started);
        Assert.All(started, d => Assert.Equal(ReasonCode.BalanceSurplus, d.Reason));
    }

    [Fact]
    public void Without_energy_counters_the_balance_is_off_and_headroom_decides_as_before()
    {
        var s = Heaters(AllHeaters);
        Export(s, 3000);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 5, 5, 5);

        Assert.Null(s.Energy);
        Assert.All(Starts(s), d => Assert.Equal(ReasonCode.HeadroomAvailable, d.Reason));
        Assert.NotEmpty(Starts(s));
    }

    [Fact]
    public void A_start_that_would_push_the_projection_below_the_floor_is_not_made()
    {
        var s = Heaters(AllHeaters);
        // 1500 W exported, 1500 W heater: 0 Wh at the :00 after it, under the 150 Wh floor.
        Export(s, 1500);

        Run(s, Feed(s, 0, T0), TimeSpan.FromMinutes(5));

        Assert.Empty(Starts(s));
        Assert.Contains(s.Dispatches.Last().Devices, d => d.Pending == ReasonCode.BalanceProjectionLow);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("    relay_min_on: 0s\n", true)]
    public void A_start_in_the_burn_window_pays_for_what_min_on_holds_past_the_hour(string lockout, bool starts)
    {
        // Live 0.10.1: heater_l2 started 3 min before the :00 with min_on 6 min, so it was bound to draw
        // 3 min into the next hour, where nothing could stop it. 3 min before the :00: 120 Wh and 2.4 kW
        // free. The start leaves 165 Wh at the :00, above the 150 Wh floor, but its 3-minute tail of 1.5 kW
        // (75 Wh) is not this hour's surplus to burn: 90 Wh is below the floor, so it is not made. Without
        // a min_on there is no tail and the same start is made, so the tail is the only reason.
        var s = Heaters(AllHeaters, OneBoundary + lockout);
        Export(s, 2400);

        Run(s, Feed(s, 120, T0 - TimeSpan.FromMinutes(57)), TimeSpan.FromSeconds(90));

        Assert.Equal(starts, Starts(s).Any());
        if (!starts)
        {
            Assert.Contains(s.Dispatches.Last().Devices, d => d.Pending == ReasonCode.BalanceProjectionLow);
        }
    }

    [Fact]
    public void A_projection_inside_the_band_neither_starts_nor_stops()
    {
        var s = Heaters(["heater_l2", "heater_l3"]);
        Neutral(s);

        // The running heater is covered: projection equals the 300 Wh balance, between 150 and 525.
        Run(s, Feed(s, 300, T0), TimeSpan.FromMinutes(5));

        Assert.Empty(Starts(s));
        Assert.Empty(Stops(s));
    }

    [Fact]
    public void A_projection_below_the_floor_stops_the_running_heater_for_the_balance()
    {
        var s = Heaters(["heater_l2", "heater_l3"]);
        Neutral(s);

        Run(s, Feed(s, 100, T0), TimeSpan.FromMinutes(5));

        var stop = Assert.Single(Stops(s), d => d.Device?.Value == "heater_l1");
        Assert.Equal(ReasonCode.BalanceProjectionLow, stop.Reason);
        Assert.Empty(Starts(s));
    }

    [Fact]
    public void In_the_burn_window_a_stop_waits_while_the_projection_is_not_negative_and_comes_below_zero()
    {
        // 10 minutes before the :00 the band has collapsed: 100 Wh covered projects to 100, below the
        // floor but not negative, so the start made to burn the surplus is not undone.
        var held = Heaters(["heater_l2", "heater_l3"]);
        Neutral(held);
        Run(held, Feed(held, 100, T0 - TimeSpan.FromMinutes(50)), TimeSpan.FromMinutes(2));
        Assert.Empty(Stops(held));

        var below = Heaters(["heater_l2", "heater_l3"]);
        Neutral(below);
        Run(below, Feed(below, -20, T0 - TimeSpan.FromMinutes(50)), TimeSpan.FromMinutes(2));
        Assert.Equal(ReasonCode.BalanceProjectionLow, Assert.Single(Stops(below), d => d.Device?.Value == "heater_l1").Reason);
    }

    [Fact]
    public void The_most_valuable_device_takes_the_surplus_first_and_the_rest_waits()
    {
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndPool, ["heater_l1", "pool"], OneBoundary);
        // 2.1 kW exported: the 690 W pool leaves 1.4 kW, but the 1.5 kW heater after it would not.
        Export(s, 2100);

        Run(s, Feed(s, 0, T0), TimeSpan.FromMinutes(5), l1: 2);

        Assert.Contains(Starts(s, "pool"), d => d.Reason == ReasonCode.BalanceSurplus);
        Assert.Empty(Starts(s, "heater_l1"));
        Assert.Equal(ReasonCode.BalanceProjectionLow, s.Dispatches.Last().Devices.Single(d => d.Device.Value == "heater_l1").Pending);
    }

    [Fact]
    public void Both_start_when_the_surplus_covers_them_one_after_the_other()
    {
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndPool, ["heater_l1", "pool"], OneBoundary);
        Export(s, 4500);

        Run(s, Feed(s, 0, T0), TimeSpan.FromMinutes(10), l1: 2);

        Assert.NotEmpty(Starts(s, "pool"));
        Assert.Contains(Starts(s, "heater_l1"), d => d.Reason == ReasonCode.BalanceSurplus);
    }

    [Theory]
    [InlineData("unknown balance")]
    [InlineData("no reading yet")]
    [InlineData("reading older than max_age")]
    [InlineData("reading of the hour before")]
    public void With_the_balance_not_known_nothing_starts_whatever_the_export(string case_)
    {
        var s = Heaters(AllHeaters);
        Export(s, 6000);
        s.Energy = new EnergyInputs(new EnergyCounters(0, 0, s.Now), TimeSpan.FromSeconds(90));
        Action feed = case_ switch
        {
            "unknown balance" => Feed(s, null, T0),
            "no reading yet" => () =>
            {
                s.Energy = new EnergyInputs(new EnergyCounters(0, 0, s.Now), TimeSpan.FromSeconds(90));
                s.Balance = null;
            },
            "reading older than max_age" => Feed(s, 900, T0, age: TimeSpan.FromMinutes(5)),
            _ => Feed(s, 900, T0 - TimeSpan.FromHours(1)),
        };

        Run(s, feed, TimeSpan.FromMinutes(5));

        Assert.Empty(Starts(s));
        Assert.Contains(s.Dispatches.Last().Devices, d => d.Pending == ReasonCode.BalanceUnknown);
    }

    [Fact]
    public void With_the_grid_power_not_valid_free_power_is_unknown_and_nothing_starts()
    {
        var s = Heaters(AllHeaters);
        Export(s, 6000);
        s.InvalidatePower(Phase.L2);

        Run(s, Feed(s, 900, T0), TimeSpan.FromMinutes(5));

        Assert.Empty(Starts(s));
    }

    [Fact]
    public void A_restore_only_device_is_not_gated_by_the_balance()
    {
        var devices = DispatchDevices.Heaters.ReplaceLineEndings("\n").Replace("    value_per_kwh:", "    start: restore_only\n    value_per_kwh:", StringComparison.Ordinal);
        var s = DispatchScenario.Create(devices, AllHeaters, OneBoundary);
        Export(s, 6000);

        Run(s, Feed(s, null, T0), TimeSpan.FromMinutes(5));

        // It never starts a device the user left off, and the balance has nothing to say about it.
        Assert.Empty(Starts(s));
        Assert.DoesNotContain(s.Dispatches.SelectMany(d => d.Devices), d => d.Pending is ReasonCode.BalanceUnknown or ReasonCode.BalanceProjectionLow);
    }

    [Fact]
    public void A_start_made_for_the_balance_is_stopped_through_the_lockouts_when_the_balance_becomes_unknown()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.Heaters, AllHeaters, OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        Export(s, 3000);

        Run(s, Feed(s, 600, T0), TimeSpan.FromMinutes(2));
        var started = port.Commands.Where(c => c.EndsWith("relay 1 on", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(started);

        // The counters go away: the heater Switchboard started comes off once its min_on (6 min) has run.
        Run(s, Feed(s, null, T0), TimeSpan.FromMinutes(10));

        var stops = Stops(s).Where(d => d.Commanded).ToList();
        Assert.NotEmpty(stops);
        Assert.All(stops, d => Assert.Equal(ReasonCode.BalanceUnknown, d.Reason));
        Assert.Contains(port.Commands, c => c.EndsWith("relay 1 off", StringComparison.Ordinal));
        Assert.All(stops, d => Assert.True(d.At - T0 >= TimeSpan.FromMinutes(6), "the stop came inside the heater's minimum on time"));
    }

    [Fact]
    public void A_heater_the_user_switched_on_is_left_on_when_the_balance_becomes_unknown()
    {
        // Switchboard did not start it, so a missing balance gives it no reason to stop it.
        var s = Heaters(["heater_l2", "heater_l3"]);
        Neutral(s);

        Run(s, Feed(s, null, T0), TimeSpan.FromMinutes(10));

        Assert.Empty(Stops(s));
    }

    [Fact]
    public void The_balance_never_lets_a_device_rise_where_the_guard_forbids_it()
    {
        // L1 at 130 % of the rating: the guard stops increases on the phase whatever the surplus says.
        var s = Heaters(AllHeaters);
        Export(s, 6000);
        var feed = Feed(s, 900, T0);
        feed();
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 32.5, 5, 5, feed);

        Assert.DoesNotContain(Starts(s), d => d.Device?.Value == "heater_l1");
    }

    [Fact]
    public void The_read_only_view_carries_balance_free_power_projection_and_band()
    {
        var s = Heaters(["heater_l2", "heater_l3"]);
        Neutral(s);
        Run(s, Feed(s, 300, T0), TimeSpan.FromMinutes(2));

        var view = s.Dispatches.Last().Balance!;

        Assert.True(view.Known);
        Assert.Equal(300, view.BalanceWh!.Value, 6);
        Assert.Equal(1500, view.Free!.Value.Value, 6);
        Assert.Equal(1500, view.Load!.Value.Value, 6);
        Assert.Equal(300, view.ProjectionWh!.Value, 6);
        Assert.Equal((150, 525), (view.Band!.Value.LoWh, view.Band!.Value.HiWh));
        Assert.InRange(view.UntilNext, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void The_view_is_absent_while_no_energy_counters_are_mapped()
    {
        var s = Heaters(AllHeaters);

        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 5, 5, 5);

        Assert.All(s.Dispatches, d => Assert.Null(d.Balance));
    }

    // BAL-07: whoever reads a decision later must see the balance it was made beside, not only the
    // reason code; and the read side (api/balance) gets the same view the rule used.
    [Fact]
    public void A_start_for_the_balance_records_the_balance_it_was_made_beside()
    {
        var s = Heaters(AllHeaters);
        Export(s, 3000);

        Run(s, Feed(s, 600, T0), TimeSpan.FromMinutes(5));

        var balance = Starts(s).First(d => d.Reason == ReasonCode.BalanceSurplus).Inputs.Dispatch!.Balance;
        Assert.NotNull(balance);
        Assert.Equal(600, balance.BalanceWh!.Value, 6);
        Assert.Equal((150, 525), (balance.LoWh!.Value, balance.HiWh!.Value));
        Assert.False(balance.Burning);
        Assert.InRange(balance.RemainingS, 55 * 60, 60 * 60);
        Assert.True(balance.FreeW > 0);
        Assert.NotNull(balance.LoadW);
        Assert.NotNull(balance.ProjectionWh);
    }

    [Fact]
    public void Decisions_carry_no_balance_while_no_energy_counters_are_mapped()
    {
        var s = Heaters(AllHeaters);
        Export(s, 3000);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 5, 5, 5);

        Assert.NotEmpty(Starts(s));
        Assert.All(Starts(s), d => Assert.Null(d.Inputs.Dispatch!.Balance));
    }

    [Fact]
    public void An_unknown_balance_is_recorded_as_unknown_values_not_as_zero()
    {
        var s = Heaters(AllHeaters);
        Export(s, 3000);

        Run(s, Feed(s, null, T0), TimeSpan.FromMinutes(2));

        var recorded = s.Dispatches.Last().Balance!.ToDecisionInputs();
        Assert.Null(recorded.BalanceWh);
        Assert.Null(recorded.ProjectionWh);
        Assert.Null(recorded.LoWh);
        Assert.Null(recorded.HiWh);
    }

    [Fact]
    public void The_dispatch_status_the_api_reads_carries_the_view_of_the_step()
    {
        var s = Heaters(AllHeaters);
        Export(s, 3000);
        Run(s, Feed(s, 600, T0), TimeSpan.FromMinutes(2));

        var status = DispatchStatusFactory.Build(s.Last, s.Dispatches[^1], s.Targets, s.Overrides, s.Lockouts, new ControlInputs(s.Inputs(), s.DispatchInputs(), s.DispatchSetup), s.Mode, s.Now);

        Assert.Equal(s.Dispatches[^1].Balance, status.Balance);
        Assert.Equal(600, status.Balance!.BalanceWh!.Value, 6);
    }
}
