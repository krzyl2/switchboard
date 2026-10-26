using Switchboard.Application.Dispatch;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Economics;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The rows of the failure table of 14-ECONOMICS that slice 3 owns (ECO-14): an economic input that fails narrows or falls back to the
/// static value, never widens and never guesses; protection is untouched. The rows that need a dynamic price (stale entity, fallback basis)
/// are in DynamicPriceDispatchTests (slice 4); the statistics file row comes with slice 5. Each test names its row.
/// </summary>
public sealed class EconomicsFailureTests
{
    private static string Follower(string follows = "import_price") => Device("a_follow", "L1", 6.6, 1500, 1.0, null, $"    value_follows: {follows}\n    value_factor: 2\n");

    private static string Static => Device("b_static", "L2", 6.6, 1500, 2.0, null);

    private static void Advance(GuardScenario s, TimeSpan by) => s.PollUntil(s.Now + by, 1, 1, 1);

    [Fact]
    public void Row_followed_price_unavailable_the_device_keeps_its_static_value_and_the_log_says_static()
    {
        // Row: "Followed price for value_follows unavailable". No tariff exists, so value_follows: import_price cannot be followed; the device is
        // worth its static 1.0, and the decision inputs say the basis is static, not a price that was never there.
        var s = Create(Follower() + Static, Workday(23), [], Economics(tariff: string.Empty));

        Advance(s, TimeSpan.FromMinutes(1));

        var follower = Block(s, "a_follow")!;
        Assert.Equal((1.0, ValueBasis.Static, 1.0), (follower.VEff, follower.ValueBasis, follower.OrderKey));
        Assert.Equal((2, 1), (Rank(s, "a_follow"), Rank(s, "b_static")));
    }

    [Fact]
    public void Row_clock_step_back_keeps_the_adopted_value_order()
    {
        // Row: "Clock step, DST: a backward step keeps the adopted order". A step back puts the clock in the off-peak zone again; the order that was
        // adopted in the morning peak must stay until three new boundaries agree, not be reset by the step.
        var s = Create(Follower() + Static, Workday(6, 58), []);
        Advance(s, TimeSpan.FromMinutes(3));
        Assert.Equal((1, 2), (Rank(s, "a_follow"), Rank(s, "b_static")));

        s.Time.StepBack(TimeSpan.FromMinutes(5));
        Advance(s, TimeSpan.FromSeconds(40));

        Assert.Equal((1, 2), (Rank(s, "a_follow"), Rank(s, "b_static")));
    }

    [Fact]
    public void Row_dst_a_zone_over_the_repeated_hour_of_the_25_hour_day_is_in_force_both_times_and_never_on_the_23_hour_day()
    {
        // Row: "Clock step, DST: zone re-derived from the clock each step". A 02:00-03:00 zone is in force for the two hours the local 02:xx
        // happens on 2026-10-25 and not at all on 2026-03-29, where there is no 02:xx. Read from what dispatch logged at every step.
        const string Zone = "  tariff:\n    default_zone: base\n    zones:\n      - id: hot\n        price: 5.0\n        when:\n          - { hours: [\"02:00-03:00\"] }\n      - id: base\n        price: 0.60\n        when: []\n";
        TimeSpan InForce(DateTimeOffset from, DateTimeOffset to)
        {
            var s = Create(One("pv_only", 0.35), from, ["dev"], Economics(tariff: Zone));
            var steps = new List<(DateTimeOffset At, string? Zone)>();
            while (s.Now < to)
            {
                Advance(s, TimeSpan.FromSeconds(10));
                steps.Add((s.Now, s.Dispatches[^1].Economics!.Zone));
            }

            return TimeSpan.FromSeconds(steps.Count(x => x.Zone == "hot") * 10);
        }

        var autumn = InForce(new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.Zero));
        var spring = InForce(new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 2, 30, 0, TimeSpan.Zero));

        Assert.InRange(autumn, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        Assert.Equal(TimeSpan.Zero, spring);
    }

    [Fact]
    public void Row_economics_removed_on_reload_dispatch_is_the_0_9_one_at_the_next_cycle()
    {
        // Row: "economics: removed on reload: 0.9 behaviour exactly". A pv_only heater with no counters mapped never starts under economics; after the
        // section is removed the sources are inert and it is the 0.9 ungated headroom start again, and no decision carries an economics block.
        var devices = One("pv_only", 0.35);
        var s = Create(devices, Workday(11), ["dev"]);
        Export(s, 3000);
        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 1, 1, 1);
        Assert.Empty(Starts(s));
        Assert.Equal(ReasonCode.BalanceUnknown, Pending(s, "dev"));

        s.Reconfigure(TestDevices.Document(devices, OneBoundary));
        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 1, 1, 1);

        Assert.All(Starts(s), d => Assert.Equal(ReasonCode.HeadroomAvailable, d.Reason));
        Assert.NotEmpty(Starts(s));
        Assert.Null(s.Dispatches[^1].Economics);
        Assert.All(Starts(s), d => Assert.Null(d.Inputs.Dispatch!.Economics));
    }

    [Fact]
    public void Row_configuration_invalid_on_reload_dispatch_wants_nothing_and_shows_no_economics_until_it_is_valid_again()
    {
        // Row: "economics: invalid on reload: safe state". With no valid configuration dispatch plans nothing (protection holds the devices off);
        // the economics of the step are gone with it, and come back, with the adopted order starting afresh, when the file is valid again.
        var s = Create(Follower() + Static, Workday(23), []);
        Advance(s, TimeSpan.FromMinutes(1));
        Assert.NotNull(s.Dispatches[^1].Economics);

        s.ConfigurationValid(false);
        Advance(s, TimeSpan.FromSeconds(40));
        Assert.Null(s.Dispatches[^1].Economics);
        Assert.Empty(s.Dispatches[^1].Wishes);

        s.ConfigurationValid(true);
        Advance(s, TimeSpan.FromMinutes(1));
        Assert.NotNull(s.Dispatches[^1].Economics);
        Assert.Equal((2, 1), (Rank(s, "a_follow"), Rank(s, "b_static")));
    }

    [Fact]
    public void Row_export_price_not_configured_the_api_view_says_none_not_zero()
    {
        // Row: "Export price unknown: no export price configured at all means the PV cost test is off (stated), not guessed". The read-only view
        // carries a null export price, so the API and a card can say "no export price" instead of a price of 0.
        var s = Create(One("pv_only", 0.35), Workday(11), ["dev"], Economics(exportPrice: null));
        Advance(s, TimeSpan.FromMinutes(1));

        Assert.Null(s.Dispatches[^1].Economics!.Export);
        Assert.NotNull(s.Dispatches[^1].Economics!.Import);
    }
}
