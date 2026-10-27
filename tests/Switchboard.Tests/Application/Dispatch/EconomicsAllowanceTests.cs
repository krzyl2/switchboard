using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The allowance gate of 14-ECONOMICS on the virtual clock (Warsaw, a G13-like tariff): which energy a device that declares
/// <c>sources</c> may take, and why not. Devices that declare none keep the 0.9 balance rule (HourlyBalanceGateTests, unchanged).
/// Each row says what it protects: the maintainer's heaters must never start from the grid at night, the car may in the off-peak zone,
/// a price must never make a device buy or burn what costs more than it is worth, and nothing here may ever widen what the guard allows.
/// </summary>
public sealed class EconomicsAllowanceTests
{
    private static readonly string[] AllOff = ["heater_l1", "heater_l2", "heater_l3", "ev_charger"];

    private enum Outcome
    {
        /// <summary>A start is decided, for the class and with the reason.</summary>
        Start,

        /// <summary>No start; the increase is pending with the reason.</summary>
        Refused,

        /// <summary>A running device is lowered, with the reason.</summary>
        Stop,

        /// <summary>A running device stays, on the class.</summary>
        Keep,
    }

    /// <summary>One row of the allowance table: what the device and the world are, and what dispatch must make of it.</summary>
    public sealed record Row(string Label, string? Sources, DateTimeOffset At, double Wh, double ExportW, double Value, string? ExportPrice, bool Running, string Settlement, string Outcome, string? Class, string? Reason)
    {
        public override string ToString() => Label;
    }

    private static Row Off(string label, string sources, DateTimeOffset at, double wh, double exportW, double value, string outcome, string? klass, string? reason, string? exportPrice = "0.35") =>
        new(label, sources, at, wh, exportW, value, exportPrice, false, "hourly_netting", outcome, klass, reason);

    private static Row On(string label, string sources, DateTimeOffset at, double wh, double exportW, double value, string outcome, string? klass, string? reason, string? exportPrice = "0.35") =>
        new(label, sources, at, wh, exportW, value, exportPrice, true, "hourly_netting", outcome, klass, reason);

    private static readonly DateTimeOffset Morning = Workday(11);
    private static readonly DateTimeOffset Night = Workday(23);

    // The surplus kinds: 600 Wh with 3 kW exported is a surplus that covers a 1.5 kW load; -100 Wh with 500 W imported is none;
    // 0 Wh with 1.5 kW exported is a surplus the 1.5 kW candidate would use up; a running device covered by the grid (0 W exported)
    // projects to its balance: 100 Wh is below the 150 Wh floor, 300 Wh inside the band.
    public static TheoryData<Row> Rows() =>
    [
        Off("pv_only at 11:00 with surplus starts on the surplus", "pv_only", Morning, 600, 3000, 0.35, nameof(Outcome.Start), "pv", "balance_surplus"),
        Off("pv_only at 11:00 without surplus: the morning peak is not its source", "pv_only", Morning, -100, -500, 0.35, nameof(Outcome.Refused), null, "source_not_allowed"),
        Off("pv_only at 23:00 without surplus: the off-peak zone is not its source", "pv_only", Night, -100, -500, 0.35, nameof(Outcome.Refused), null, "source_not_allowed"),
        Off("pv_only with surplus the candidate would use up", "pv_only", Morning, 0, 1500, 0.35, nameof(Outcome.Refused), null, "balance_projection_low"),
        Off("pv_only with a positive balance still inside the band: the surplus is a source, it is just not enough yet", "pv_only", Morning, 250, 100, 0.35, nameof(Outcome.Refused), null, "balance_projection_low"),
        Off("pv_only with the projection inside the band is Hold on the surplus: balance_projection_low, not source_not_allowed", "pv_only", Morning, 300, 0, 0.35, nameof(Outcome.Refused), null, "balance_projection_low"),
        Off("pv_only whose value is below the export price does not burn surplus worth more sold", "pv_only", Morning, 600, 3000, 0.35, nameof(Outcome.Refused), null, "value_below_cost", "0.50"),
        Off("pv_only with a value equal to the export price starts (margin 0)", "pv_only", Morning, 600, 3000, 0.35, nameof(Outcome.Start), "pv", "balance_surplus"),
        Off("pv_only with no export price configured: the price test is off", "pv_only", Morning, 600, 3000, 0.01, nameof(Outcome.Start), "pv", "balance_surplus", exportPrice: null),
        Off("pv_and_zone:offpeak at 23:00 starts in its zone", "pv_and_zone:offpeak", Night, -100, -500, 2.03, nameof(Outcome.Start), "zone", "zone_allowed"),
        Off("pv_and_zone:offpeak at 11:00 has no source open", "pv_and_zone:offpeak", Morning, -100, -500, 2.03, nameof(Outcome.Refused), null, "source_not_allowed"),
        Off("pv_and_zone:offpeak whose value is below the off-peak price does not buy", "pv_and_zone:offpeak", Night, -100, -500, 0.35, nameof(Outcome.Refused), null, "value_below_cost"),
        Off("any buys in the morning peak when the value covers 1.10", "any", Morning, -100, -500, 2.03, nameof(Outcome.Start), "zone", "zone_allowed"),
        Off("any does not buy in the morning peak when the value is below 1.10", "any", Morning, -100, -500, 0.35, nameof(Outcome.Refused), null, "value_below_cost"),
        Off("any in the afternoon peak below 1.60 does not buy", "any", Workday(16, 30), -100, -500, 1.50, nameof(Outcome.Refused), null, "value_below_cost"),
        Off("any in the afternoon peak at exactly 1.60 buys (margin 0)", "any", Workday(16, 30), -100, -500, 1.60, nameof(Outcome.Start), "zone", "zone_allowed"),
        Off("any with surplus takes the surplus before buying", "any", Night, 600, 3000, 2.03, nameof(Outcome.Start), "pv", "balance_surplus"),
        Off("pv_and_zone:offpeak 10 minutes before the zone ends may still start", "pv_and_zone:offpeak", Workday(6, 50), -100, -500, 2.03, nameof(Outcome.Start), "zone", "zone_allowed"),
        Off("pv_and_zone:offpeak 5 minutes before the zone ends is inside zone_end_margin", "pv_and_zone:offpeak", Workday(6, 55), -100, -500, 2.03, nameof(Outcome.Refused), null, "source_not_allowed"),
        Off("a weekend day is all off-peak", "pv_and_zone:offpeak", Local(2026, 10, 31, 12), -100, -500, 2.03, nameof(Outcome.Start), "zone", "zone_allowed"),
        Off("a holiday on a weekday is off-peak", "pv_and_zone:offpeak", Local(2026, 11, 11, 10), -100, -500, 2.03, nameof(Outcome.Start), "zone", "zone_allowed"),
        On("running pv_only below the floor is stopped by the balance", "pv_only", Morning, 100, 0, 0.35, nameof(Outcome.Stop), null, "balance_projection_low"),
        On("running pv_and_zone:offpeak in its zone is not stopped by the balance", "pv_and_zone:offpeak", Night, 100, 0, 2.03, nameof(Outcome.Keep), "zone", null),
        On("running pv_and_zone:offpeak outside its zone is judged by the balance", "pv_and_zone:offpeak", Morning, 100, 0, 2.03, nameof(Outcome.Stop), null, "balance_projection_low"),
        On("running pv_only inside the band stays", "pv_only", Morning, 300, 0, 0.35, nameof(Outcome.Keep), "pv", null),
        On("running pv_only whose value falls below the export price is stopped", "pv_only", Morning, 600, 3000, 0.35, nameof(Outcome.Stop), null, "value_below_cost", "0.50"),
        On("running any in a peak it cannot afford is judged by the balance", "any", Morning, 100, 0, 0.35, nameof(Outcome.Stop), null, "balance_projection_low"),
        On("a running zone-class device is not stopped inside the zone-end margin (it only bars a start)", "pv_and_zone:offpeak", Workday(6, 55), 100, 0, 2.03, nameof(Outcome.Keep), "zone", null),
        new Row("net billing: surplus covering the candidate and the start hysteresis starts", "pv_only", Morning, 0, 3000, 0.35, "0.35", false, "net_billing", nameof(Outcome.Start), "pv", "surplus_available"),
        new Row("net billing: surplus below the candidate is surplus_low", "pv_only", Morning, 0, 1000, 0.35, "0.35", false, "net_billing", nameof(Outcome.Refused), null, "surplus_low"),
        new Row("net billing: no surplus at all leaves no source open", "pv_only", Morning, 0, -500, 0.35, "0.35", false, "net_billing", nameof(Outcome.Refused), null, "source_not_allowed"),
        new Row("net billing: a running device is stopped below the stop hysteresis", "pv_only", Morning, 0, -400, 0.35, "0.35", true, "net_billing", nameof(Outcome.Stop), null, "surplus_low"),
        new Row("net billing: a little import is tolerated while running", "pv_only", Morning, 0, -100, 0.35, "0.35", true, "net_billing", nameof(Outcome.Keep), "pv", null),
        new Row("net billing: the zone class needs no surplus", "pv_and_zone:offpeak", Night, 0, -500, 2.03, "0.35", false, "net_billing", nameof(Outcome.Start), "zone", "zone_allowed"),
    ];

    [Theory]
    [MemberData(nameof(Rows))]
    public void Allowance_table_gives_the_class_and_the_reason_code(Row row)
    {
        // Why: the table is the specification of 14-ECONOMICS "Allowance of device d" in one place. A row that changes means either the
        // sources, the price test, the zone-end margin or the settlement rule changed meaning, and a device would start or stop on energy
        // it was never meant to take.
        var s = Create(One(row.Sources!, row.Value), row.At, row.Running ? [] : ["dev"], Economics(row.Settlement, row.ExportPrice));
        Export(s, row.ExportW);
        Action? feed = row.Settlement == "hourly_netting" ? Feed(s, row.Wh) : null;

        Run(s, feed, 3);

        var starts = Starts(s, "dev").ToList();
        var stops = Stops(s, "dev").ToList();
        switch (Enum.Parse<Outcome>(row.Outcome))
        {
            case Outcome.Start:
                Assert.Empty(stops);
                var start = starts.First();
                Assert.Equal(row.Reason, ReasonCodes.Name(start.Reason));
                Assert.Equal(row.Class, ClassName(start.Inputs.Dispatch!.Economics!.Class));
                break;
            case Outcome.Refused:
                Assert.Empty(starts);
                Assert.Empty(stops);
                Assert.Equal(row.Reason, ReasonCodes.Name(Pending(s, "dev")!.Value));
                Assert.Equal("none", ClassName(Block(s, "dev")!.Class));
                break;
            case Outcome.Stop:
                Assert.Empty(starts);
                var stop = stops.First();
                Assert.Equal(row.Reason, ReasonCodes.Name(stop.Reason));
                break;
            default:
                Assert.Empty(starts);
                Assert.Empty(stops);
                Assert.Equal(row.Class, ClassName(Block(s, "dev")!.Class));
                break;
        }
    }

    private static string ClassName(EnergyClass? energyClass) => energyClass is { } c ? c.ToString().ToLowerInvariant() : "none";

    [Fact]
    public void The_heaters_that_may_only_take_surplus_do_not_start_at_23_00_while_the_car_that_may_take_off_peak_does()
    {
        // Why: this is the 0.9 gap the whole slice closes. In 0.9 every start: headroom device was judged by the balance alone, so a heater could be
        // started from the grid at night; now the sources decide. At 23:00 on a workday the zone is the off-peak one, which the car may use.
        var s = Create(HeatersAndCar(), Workday(23), AllOff);
        Export(s, -500);

        Run(s, Feed(s, -100), 5);

        Assert.Empty(Starts(s, "heater_l1").Concat(Starts(s, "heater_l2")).Concat(Starts(s, "heater_l3")));
        Assert.All(["heater_l1", "heater_l2", "heater_l3"], heater => Assert.Equal(ReasonCode.SourceNotAllowed, Pending(s, heater)));
        var start = Assert.Single(Starts(s, "ev_charger"));
        Assert.Equal(ReasonCode.ZoneAllowed, start.Reason);
        Assert.Equal((EnergyClass.Zone, "offpeak"), (start.Inputs.Dispatch!.Economics!.Class, start.Inputs.Dispatch.Economics.Zone));
    }

    [Fact]
    public void The_stop_of_a_surplus_class_device_below_the_floor_does_not_stop_the_zone_class_device()
    {
        // Why: importing in an allowed zone is intended (14-ECONOMICS). Both run at 23:00 on a balance far below the floor: the heater, which
        // lives on surplus, is stopped by the settlement rule; the car, which is in its off-peak zone, must not be. In 0.9 the balance would
        // have stopped both.
        var devices = Device("heater_l1", "L1", 6.6, 1500, 0.35, "pv_only") + Device("ev_charger", "L2", 4, 920, 2.03, "pv_and_zone:offpeak");
        var s = Create(devices, Night, []);
        Export(s, 0);

        Run(s, Feed(s, -2000), 3);

        Assert.Equal(ReasonCode.BalanceProjectionLow, Assert.Single(Stops(s, "heater_l1")).Reason);
        Assert.Empty(Stops(s, "ev_charger"));
        Assert.Equal(EnergyClass.Zone, Block(s, "ev_charger")!.Class);
    }

    [Fact]
    public void A_stop_comes_lowest_value_first_and_each_stop_lowers_the_load_the_next_is_judged_against()
    {
        // Why: the balance-gate stop order is the value order (ECO-05). Two running pv_only devices and a balance that is below the floor by
        // one device's worth of energy: only the cheaper may go, the dearer stays because the stop already brought the projection back.
        var devices = Device("cheap", "L1", 6.6, 1500, 0.35, "pv_only") + Device("dear", "L2", 6.6, 1500, 0.80, "pv_only");
        var s = Create(devices, Morning, [], Economics(exportPrice: "0.10"));
        Export(s, 0);

        // 1.5 kW + 1.5 kW covered by the grid: the projection is the balance, 100 Wh with two loads; -1350 Wh per load per hour is not modelled
        // because free power already includes both loads. 100 Wh is below the floor, 150 Wh: the first stop lowers the load, not the free power.
        Run(s, Feed(s, 100), 3);

        Assert.Equal(["cheap"], Stops(s).Select(d => d.Device!.Value.Value).Distinct());
    }

    [Fact]
    public void No_tariff_with_a_zone_source_narrows_the_device_to_surplus_and_warns()
    {
        // Why: a configuration problem may only narrow (14-ECONOMICS, "Failure behaviour"): a zone source without a tariff is dropped, so the
        // car has the surplus only and never buys. Nothing is guessed about a price that is not there.
        var document = TestDevices.Document(Device("ev_charger", "L2", 4, 920, 2.03, "pv_and_zone:offpeak"), OneBoundary, extra: "\n" + Economics(tariff: string.Empty));
        Assert.Contains(TestConfigurations.Valid(document).Warnings, w => w.Code == ConfigurationIssueCodes.SourcesUnusable);

        var night = Create(Device("ev_charger", "L2", 4, 920, 2.03, "pv_and_zone:offpeak"), Night, ["ev_charger"], Economics(tariff: string.Empty));
        Export(night, -500);
        Run(night, Feed(night, -100), 3);
        var surplus = Create(Device("ev_charger", "L2", 4, 920, 2.03, "pv_and_zone:offpeak"), Night, ["ev_charger"], Economics(tariff: string.Empty));
        Export(surplus, 3000);
        Run(surplus, Feed(surplus, 600), 3);

        Assert.Empty(Starts(night));
        Assert.Equal(ReasonCode.SourceNotAllowed, Pending(night, "ev_charger"));
        Assert.Equal(ReasonCode.BalanceSurplus, Assert.Single(Starts(surplus)).Reason);
    }

    [Fact]
    public void With_no_export_price_the_surplus_price_test_is_off_and_the_decision_says_so()
    {
        // Why: without an export price there is no forgone income to compare with, and nothing may be guessed: the surplus class is used and the
        // decision inputs carry a null export price, not 0, so the log and the API can say the test was off.
        var s = Create(One("pv_only", 0.01), Morning, ["dev"], Economics(exportPrice: null));
        Export(s, 3000);

        Run(s, Feed(s, 600), 3);

        var start = Assert.Single(Starts(s, "dev"));
        Assert.Null(start.Inputs.Dispatch!.Economics!.ExportPrice);
        Assert.Equal(EnergyClass.Pv, start.Inputs.Dispatch.Economics.Class);
    }

    [Theory]
    [InlineData("unknown balance")]
    [InlineData("no reading yet")]
    [InlineData("reading older than max_age")]
    public void With_the_balance_unknown_surplus_gives_no_allowance_for_a_device_whose_only_class_it_is(string case_)
    {
        // Why: BAL-05 stays true for a device with sources. A pv_only heater whose balance is not known may not start whatever the export is,
        // and the reason is balance_unknown, not a source or a price that was never the problem.
        var s = Create(One("pv_only", 0.35), Morning, ["dev"]);
        Export(s, 6000);
        Action feed = case_ switch
        {
            "unknown balance" => Feed(s, null),
            "no reading yet" => () =>
            {
                Feed(s, 0)();
                s.Balance = null;
            },
            _ => Feed(s, 900, age: TimeSpan.FromMinutes(5)),
        };

        Run(s, feed, 3);

        Assert.Empty(Starts(s));
        Assert.Equal(ReasonCode.BalanceUnknown, Pending(s, "dev"));
    }

    [Fact]
    public void With_the_balance_unknown_a_zone_class_stays_available_where_its_zone_is_known()
    {
        // Why: the zone class does not depend on the balance (14-ECONOMICS failure table), so a counter outage at night must not stop the car from
        // charging off-peak, while the heaters (surplus only) still wait.
        var s = Create(HeatersAndCar(), Night, AllOff);
        Export(s, -500);

        Run(s, Feed(s, null), 3);

        Assert.Equal(ReasonCode.ZoneAllowed, Assert.Single(Starts(s, "ev_charger")).Reason);
        Assert.Empty(Starts(s, "heater_l1"));
        Assert.Equal(ReasonCode.BalanceUnknown, Pending(s, "heater_l1"));
    }

    [Fact]
    public void Without_energy_counters_a_surplus_only_device_never_starts_and_a_zone_device_does()
    {
        // Why: a declared source that cannot be evaluated narrows the device, down to none (14-ECONOMICS): pv_only under hourly netting needs the
        // counters. Unlike a device with no sources (0.9: ungated without counters), a strict device never starts on a guess.
        var s = Create(HeatersAndCar(), Night, AllOff);
        Export(s, 3000);

        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 1, 1);

        Assert.Null(s.Energy);
        Assert.Empty(Starts(s, "heater_l1"));
        Assert.Equal(ReasonCode.BalanceUnknown, Pending(s, "heater_l1"));
        Assert.Equal(ReasonCode.ZoneAllowed, Assert.Single(Starts(s, "ev_charger")).Reason);
    }

    [Fact]
    public void Under_net_billing_unknown_free_power_gives_no_surplus_start()
    {
        // Why: the failure table row "free power unknown under net billing": no surplus-class start, reason balance_unknown. Net billing needs no
        // energy counters, so the only thing that can make the surplus unknown is the grid power.
        var s = Create(One("pv_only", 0.35), Morning, ["dev"], Economics("net_billing"));
        Export(s, 6000);
        s.InvalidatePower(Phase.L2);

        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 1, 1);

        Assert.Empty(Starts(s));
        Assert.Equal(ReasonCode.BalanceUnknown, Pending(s, "dev"));
    }

    [Fact]
    public void A_device_started_on_surplus_is_lowered_through_its_lockout_when_the_balance_goes_away()
    {
        // Why: BAL-05 for a device with sources: what Switchboard started for the surplus must not be left running when the surplus can no
        // longer be told, and the stop still waits for the relay's min_on (6 min): a lockout never yields to economics.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(One("pv_only", 0.35), ["dev"], OneBoundary, mode: DispatchScenario.Active, port: port, start: Morning, extra: "\n" + Economics());
        s.Time.Zone = Warsaw;
        s.Follow(port);
        Export(s, 3000);

        Run(s, Feed(s, 600), 2);
        Assert.Contains(port.Commands, c => c.EndsWith("relay 1 on", StringComparison.Ordinal));

        Run(s, Feed(s, null), 10);

        var stops = Stops(s).Where(d => d.Commanded).ToList();
        Assert.NotEmpty(stops);
        Assert.All(stops, d => Assert.Equal(ReasonCode.BalanceUnknown, d.Reason));
        Assert.All(stops, d => Assert.True(d.At - Morning >= TimeSpan.FromMinutes(6), "the stop came inside the relay's minimum on time"));
    }

    [Fact]
    public void A_device_with_no_sources_keeps_the_0_9_balance_rule_beside_devices_that_have_them()
    {
        // Why: ECO-06, "an omitted sources keeps the 0.9 behaviour". At night with a surplus the unspecified heater is started by the balance as
        // in 0.9 (balance_surplus), the pv_only one by its source; with no surplus the unspecified one is refused by the balance (a 0.9 reason)
        // where the strict one says its source is not allowed.
        var devices = Device("old_heater", "L1", 6.6, 1500, 0.35, null) + Device("new_heater", "L2", 6.6, 1500, 0.35, "pv_only");
        var surplus = Create(devices, Night, ["old_heater", "new_heater"]);
        Export(surplus, 6000);
        Run(surplus, Feed(surplus, 900), 3);
        var none = Create(devices, Night, ["old_heater", "new_heater"]);
        Export(none, -500);
        Run(none, Feed(none, -100), 3);

        Assert.Equal(ReasonCode.BalanceSurplus, Assert.Single(Starts(surplus, "old_heater")).Reason);
        Assert.Equal(ReasonCode.BalanceSurplus, Assert.Single(Starts(surplus, "new_heater")).Reason);
        Assert.Empty(Starts(none));
        Assert.Equal(ReasonCode.BalanceProjectionLow, Pending(none, "old_heater"));
        Assert.Equal(ReasonCode.SourceNotAllowed, Pending(none, "new_heater"));
    }

    [Fact]
    public void Without_an_economics_section_a_declared_source_changes_nothing()
    {
        // Why: ECO-15. No economics: section means exactly 0.9, so sources written without a section are inert (a heater that declares
        // pv_only and has no counters mapped is ungated as before) rather than half-working on a tariff that does not exist.
        var s = DispatchScenario.Create(One("pv_only", 0.35), ["dev"], OneBoundary, start: Night);
        s.Time.Zone = Warsaw;
        Export(s, 3000);

        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 1, 1);

        Assert.All(Starts(s), d => Assert.Equal(ReasonCode.HeadroomAvailable, d.Reason));
        Assert.NotEmpty(Starts(s));
        Assert.All(s.Dispatches, d => Assert.Null(d.Economics));
        Assert.All(s.Decisions.Where(d => d.Inputs.Dispatch is not null), d => Assert.Null(d.Inputs.Dispatch!.Economics));
    }

    [Fact]
    public void The_decision_inputs_carry_the_zone_prices_value_order_key_class_and_sources_of_the_step()
    {
        // Why: ECO-09. Whoever reads the log later must see which energy was judged and at what price, not only the reason code.
        var s = Create(HeatersAndCar(), Night, AllOff);
        Export(s, -500);

        Run(s, Feed(s, -100), 3);

        var car = Assert.Single(Starts(s, "ev_charger")).Inputs.Dispatch!.Economics!;
        Assert.Equal(("offpeak", 0.60, 0.35, PriceBasis.Zone, 2.03, ValueBasis.Static, 2.03, EnergyClass.Zone, "pv_and_zone:offpeak", SettlementModelId.HourlyNetting),
            (car.Zone, car.ImportPrice, car.ExportPrice, car.PriceBasis, car.VEff, car.ValueBasis, car.OrderKey, car.Class, car.Sources, car.Settlement));
        var heater = s.Dispatches[^1].Wish(DeviceName.Parse("heater_l1"))!.Inputs.Economics!;
        Assert.Equal((0.35, "pv_only", (EnergyClass?)null), (heater.VEff, heater.Sources, heater.Class));
    }

    [Fact]
    public void The_dispatch_status_the_api_reads_carries_the_economics_of_the_step()
    {
        // Why: the read-only exposure (like api/balance for the balance) must be the very view the rule used, with the class per device.
        var s = Create(HeatersAndCar(), Night, AllOff);
        Export(s, -500);
        Run(s, Feed(s, -100), 3);

        var status = DispatchStatusFactory.Build(s.Last, s.Dispatches[^1], s.Targets, s.Overrides, s.Lockouts, new ControlInputs(s.Inputs(), s.DispatchInputs(), s.DispatchSetup), s.Mode, s.Now);

        Assert.Equal(s.Dispatches[^1].Economics, status.Economics);
        Assert.Equal("offpeak", status.Economics!.Zone);
        Assert.Equal(EnergyClass.Zone, status.Economics.Devices.Single(d => d.Device.Value == "ev_charger").Class);
    }

    [Fact]
    public void A_gated_device_that_cannot_rise_for_the_guard_is_not_given_a_class()
    {
        // Why: the allowance sits behind the guard. L2 at 130 % of the rating: the guard stops increases there, so the car (which the zone would
        // allow) must not start, and the pending reason must be the guard's, never zone_allowed.
        var s = Create(HeatersAndCar(), Night, AllOff);
        Export(s, -500);
        Feed(s, -100)();

        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 32.5, 1, Feed(s, -100));

        Assert.Empty(Starts(s, "ev_charger"));
        Assert.NotEqual(ReasonCode.ZoneAllowed, Pending(s, "ev_charger"));
        Assert.Null(Block(s, "ev_charger")!.Class);
        Assert.Null(s.Dispatches[^1].Economics!.Devices.Single(d => d.Device.Value == "ev_charger").Class);
    }
}
