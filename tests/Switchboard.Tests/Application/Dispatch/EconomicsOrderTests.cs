using Switchboard.Application.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The dispatch start order follows what a kWh is worth now (ECO-05, 14-ECONOMICS "How value feeds the merit order"): order_key is the
/// shed priority where one is set, else the effective value; a new order is adopted only after economics.order_stability_cycles boundaries
/// and only for keys that differ by at least economics.order_min_gap_fraction, as the phase ranking is (DISP-10). The guard's shed order
/// never sees a price. The follower's value is 2 x the import price (1.20 off-peak, 2.20 in the morning peak) against a static 2.00.
/// </summary>
public sealed class EconomicsOrderTests
{
    private static string Follower(double factor) => Device("a_follow", "L1", 6.6, 1500, 1.0, null, $"    value_follows: import_price\n    value_factor: {factor.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");

    private static readonly string Static = Device("b_static", "L2", 6.6, 1500, 2.0, null);

    private static string Blip(string hours) =>
        "  tariff:\n    default_zone: base\n    zones:\n      - id: blip\n        price: 5.0\n        when:\n          - { hours: [\"" + hours + "\"] }\n      - id: base\n        price: 0.60\n        when: []\n";

    private static void Advance(GuardScenario s, TimeSpan by) => s.PollUntil(s.Now + by, 1, 1, 1);

    [Fact]
    public void A_new_value_order_is_adopted_only_after_the_stability_cycles_and_then_follows_the_price()
    {
        // Why: the order must follow prices, or the car would wait behind the heater whose energy became cheap; but it may not flip on the first
        // boundary of a new zone, or one noisy price would reshuffle who gets the headroom. Default: 3 boundaries.
        var s = Create(Follower(2) + Static, Workday(6, 58), []);
        Advance(s, TimeSpan.FromSeconds(50));
        Assert.Equal((2, 1), (Rank(s, "a_follow"), Rank(s, "b_static")));

        // The morning peak starts at 07:00:00: boundaries 07:00:00 and 07:00:30 propose the new order, the third (07:01:00) adopts it.
        Advance(s, TimeSpan.FromSeconds(110));
        Assert.Equal((2, 1), (Rank(s, "a_follow"), Rank(s, "b_static")));
        Advance(s, TimeSpan.FromSeconds(30));
        Assert.Equal((1, 2), (Rank(s, "a_follow"), Rank(s, "b_static")));
    }

    [Fact]
    public void A_price_that_flips_the_keys_for_less_than_the_stability_cycles_does_not_reshuffle_the_devices()
    {
        // Why: a price tick must not reshuffle devices (14-ECONOMICS). The blip zone makes the follower worth 10 for one minute (two boundaries):
        // the old order must hold throughout, at every step, not only at the end.
        var s = Create(Follower(2) + Static, Workday(9, 58), [], Economics(tariff: Blip("10:00-10:01")));
        var ranks = new List<(int, int)>();
        while (s.Now < Workday(10, 4))
        {
            Advance(s, TimeSpan.FromSeconds(10));
            if (Rank(s, "a_follow") is { } a && Rank(s, "b_static") is { } b)
            {
                ranks.Add((a, b));
            }
        }

        Assert.True(ranks.Count > 10, "the order was observed at too few steps");
        Assert.All(ranks, r => Assert.Equal((2, 1), r));
    }

    [Fact]
    public void A_flip_that_lasts_is_adopted_and_so_is_the_flip_back()
    {
        // Why: the stability rule must delay a change, not forbid it. A three-minute blip is adopted after three boundaries and, when the price
        // is back, the old order returns after three boundaries again.
        var s = Create(Follower(2) + Static, Workday(9, 58), [], Economics(tariff: Blip("10:00-10:03")));

        Advance(s, TimeSpan.FromSeconds(2 * 60 + 70));
        Assert.Equal((1, 2), (Rank(s, "a_follow"), Rank(s, "b_static")));

        // Back to the base price at 10:03:00; the third boundary of it, 10:04:00, readopts the old order.
        Advance(s, TimeSpan.FromSeconds(180));
        Assert.Equal((2, 1), (Rank(s, "a_follow"), Rank(s, "b_static")));
    }

    [Theory]
    [InlineData(3.5, 2, 1, 0.05)]
    [InlineData(3.6, 1, 2, 0.05)]
    [InlineData(3.5, 1, 2, 0.0)]
    public void Keys_closer_than_the_gap_fraction_tie_and_the_ordinary_tie_break_decides(double factor, int followerRank, int staticRank, double gap)
    {
        // Why: order_min_gap_fraction is configuration, not a literal. At 0.60 the follower is worth 2.10 (factor 3.5) or 2.16 (factor 3.6)
        // against 2.00: 4.8 % apart is inside a 5 % gap, so they tie and the name decides (b_static first); 7.4 % is outside it, and with a gap of 0
        // the exact keys always decide.
        var control = OneBoundary + $"  economics:\n    order_min_gap_fraction: {gap.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n";
        var s = Create(Follower(factor) + Static, Workday(23), [], control: control);

        Advance(s, TimeSpan.FromMinutes(1));

        Assert.Equal((followerRank, staticRank), (Rank(s, "a_follow"), Rank(s, "b_static")));
    }

    [Fact]
    public void A_shed_priority_pins_a_device_whatever_the_prices_do()
    {
        // Why: shed_priority replaces the value in the order (DISP-02) and must keep doing so: a device the user pinned never moves with a tariff.
        var pinned = Device("pinned", "L3", 6.6, 1500, 0.1, null, "    shed_priority: 5\n");
        var s = Create(pinned + Follower(2) + Static, Workday(6, 58), []);

        Advance(s, TimeSpan.FromSeconds(50));
        var before = (Rank(s, "pinned"), Rank(s, "a_follow"), Rank(s, "b_static"));
        Advance(s, TimeSpan.FromMinutes(3));
        var after = (Rank(s, "pinned"), Rank(s, "a_follow"), Rank(s, "b_static"));

        Assert.Equal((1, 3, 2), before);
        Assert.Equal((1, 2, 3), after);
    }

    [Fact]
    public void A_configuration_change_adopts_its_order_at_once()
    {
        // Why: the stability rule protects against noise, not against the user. After an edit of the file the new order must be in force at the
        // next boundary, as a new phase ranking is.
        var s = Create(Follower(2) + Static, Workday(23), []);
        Advance(s, TimeSpan.FromMinutes(1));
        Assert.Equal((2, 1), (Rank(s, "a_follow"), Rank(s, "b_static")));

        s.Reconfigure(Document(Follower(2) + Device("b_static", "L2", 6.6, 1500, 0.5, null)));
        Advance(s, TimeSpan.FromSeconds(40));

        Assert.Equal((1, 2), (Rank(s, "a_follow"), Rank(s, "b_static")));
    }

    [Fact]
    public void Without_an_economics_section_the_order_is_the_static_one_whatever_the_zone()
    {
        // Why: ECO-05 and ECO-15. A file with no economics: section has no prices, so value_follows is inert (a warning) and the order is the 0.9 one:
        // by value_per_kwh, here b_static (2.0) before a_follow (1.0), through the morning peak as well.
        var s = DispatchScenario.Create(Follower(2) + Static, [], OneBoundary, start: Workday(6, 58));
        s.Time.Zone = Warsaw;
        var ranks = new List<(int, int)>();
        while (s.Now < Workday(7, 6))
        {
            Advance(s, TimeSpan.FromSeconds(10));
            if (Rank(s, "a_follow") is { } a && Rank(s, "b_static") is { } b)
            {
                ranks.Add((a, b));
            }
        }

        Assert.True(ranks.Count > 10, "the order was observed at too few steps");
        Assert.All(ranks, r => Assert.Equal((2, 1), r));
    }

    [Fact]
    public void The_guard_shed_order_is_identical_with_and_without_prices_while_the_dispatch_order_moves()
    {
        // Why: D2 to D4 and 14-ECONOMICS: protection never sees a price. The cheap-by-file device B (0.5) follows a price that makes it the most
        // valuable one for dispatch (6.0 against 1.0); under overload the guard must still shed it first, as the static order says.
        var devices = Device("a_steady", "L1", 6.6, 1500, 1.0, null)
            + Device("b_follower", "L1", 6.6, 1500, 0.5, null, "    value_follows: import_price\n    value_factor: 10\n");

        (List<string> Sheds, int? RankA, int? RankB) Run(bool prices)
        {
            var s = prices
                ? Create(devices, Workday(23), [])
                : DispatchScenario.Create(devices, [], OneBoundary, start: Workday(23));
            s.Time.Zone = Warsaw;
            Advance(s, TimeSpan.FromMinutes(1));
            var ranks = (Rank(s, "a_steady"), Rank(s, "b_follower"));
            s.Sample(Phase.L1, 37.5);
            s.Hold(Phase.L1, 37.5, TimeSpan.FromSeconds(10), s.Now + TimeSpan.FromSeconds(120));
            return ([.. s.Sheds.Select(i => $"{i.Device.Value}:{i.Target}")], ranks.Item1, ranks.Item2);
        }

        var withPrices = Run(true);
        var without = Run(false);

        Assert.Equal(["b_follower:Off", "a_steady:Off"], withPrices.Sheds);
        Assert.Equal(without.Sheds, withPrices.Sheds);
        Assert.Equal((1, 2), (without.RankA, without.RankB));
        Assert.Equal((2, 1), (withPrices.RankA, withPrices.RankB));
    }
}
