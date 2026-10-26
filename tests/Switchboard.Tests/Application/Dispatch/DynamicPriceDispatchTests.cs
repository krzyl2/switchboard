using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Economics;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// Dynamic prices in dispatch (14-ECONOMICS, slice 4, ECO-10, ECO-14): a price that follows a Home Assistant entity decides the
/// PV cost test, the zone price and what a device that follows a price is worth, while the entity is fresh; the moment it is not
/// usable the configured static fallback stands in, never the last known value. Everything here runs on the virtual clock with a
/// real signal store between the entity and dispatch, so the freshness and unit rules under test are the signal layer's own.
/// </summary>
public sealed class DynamicPriceDispatchTests
{
    private const string PricePerMwh = "PLN/MWh";

    /// <summary>The export price follows the entity (fallback 0.30), the import tariff is the usual zoned one.</summary>
    private static string DynamicExport(string fallback = "0.30", string add = "") =>
        Economics(exportPrice: $"\n      signal: {PriceScenarios.Signal}\n      fallback: {fallback}\n{add}");

    private static string Heater(double value = 0.35, string sources = "pv_only", string extra = "") => Device("dev", "L1", 6.6, 1500, value, sources, extra);

    private static (GuardScenario S, PriceScenarios P) Morning(string devices, string economics, string? signal = null, string[]? off = null)
    {
        var (s, p) = PriceScenarios.Create(devices, Workday(11), off ?? ["dev"], economics, signal);
        Export(s, 3000);
        return (s, p);
    }

    private static EconomicsDecisionInputs Block(GuardScenario s) => EconomicsScenarios.Block(s, "dev")!;

    [Fact]
    public void A_fresh_price_is_live_the_per_mwh_number_becomes_per_kwh_and_decides_the_cost_test()
    {
        // Why: the whole point of ECO-10. 200 PLN/MWh is 0.20 per kWh, below the heater's value of 0.35, so the surplus is worth taking: the
        // heater starts on surplus, and the log says the export price was live, not the 0.30 fallback.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("200");

        p.Run(2);

        Assert.NotEmpty(Starts(s, "dev"));
        var block = Block(s);
        Assert.Equal((0.20, PriceBasis.Live), (block.ExportPrice, block.PriceBasis));
        Assert.Equal(EnergyClass.Pv, block.Class);
    }

    [Fact]
    public void A_dynamic_export_price_that_rises_above_the_value_flips_a_start_into_value_below_cost_and_back()
    {
        // Why: selling must beat burning. At 500 PLN/MWh (0.50) a 0.35 heater would burn surplus worth more sold: no start, value_below_cost.
        // When the market falls to 0.20 the same heater, same surplus, starts: the live price, not the fallback, moved the decision.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("500");
        p.Run(2);
        Assert.Empty(Starts(s, "dev"));
        Assert.Equal(ReasonCode.ValueBelowCost, Pending(s, "dev"));

        p.Report("200");
        p.Run(2);

        Assert.NotEmpty(Starts(s, "dev"));
    }

    [Fact]
    public void A_price_stale_at_max_age_is_replaced_by_the_fallback_not_by_its_last_value()
    {
        // Why: "never the last known value". The entity last said 500 (too dear for the heater); after max_age (2 min) of silence the fallback 0.30
        // is in force: the heater, worth 0.35, now starts. Had the last value stood, it would have stayed refused. The basis in the log is fallback.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("500");
        p.Run(1);
        Assert.Empty(Starts(s, "dev"));
        Assert.Equal(PriceBasis.Live, Block(s).PriceBasis);

        p.Run(3);

        var block = Block(s);
        Assert.Equal((0.30, PriceBasis.Fallback), (block.ExportPrice, block.PriceBasis));
        Assert.NotEmpty(Starts(s, "dev"));
    }

    [Fact]
    public void The_price_goes_stale_exactly_at_max_age_not_a_step_before()
    {
        // Why: SIG-05 for a price. One poll before the signal is 2 minutes old it is live, at 2 minutes it is not.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("200");
        p.Refresh();
        s.PollUntil(s.Now + TimeSpan.FromSeconds(110), 1, 1, 1, p.FeedWith(600));
        Assert.Equal(PriceBasis.Live, s.Dispatches[^1].Economics!.Export!.Value.Basis);

        s.PollUntil(s.Now + TimeSpan.FromSeconds(10), 1, 1, 1, p.FeedWith(600));

        Assert.Equal(PriceBasis.Fallback, s.Dispatches[^1].Economics!.Export!.Value.Basis);
    }

    [Fact]
    public void A_repeating_value_stays_live_as_long_as_the_entity_keeps_reporting()
    {
        // Why: freshness is from last_reported (SIG-06): a market price that stays the same for an hour is still a live price while Home
        // Assistant keeps confirming it, so it must not drop to the fallback every max_age.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("200");
        for (var i = 0; i < 6; i++)
        {
            s.PollUntil(s.Now + TimeSpan.FromSeconds(60), 1, 1, 1, p.FeedWith(600));
            p.Confirm("200");
        }

        p.Refresh();
        s.PollUntil(s.Now + TimeSpan.FromSeconds(10), 1, 1, 1, p.FeedWith(600));

        Assert.Equal(PriceBasis.Live, Block(s).PriceBasis);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("unknown")]
    public void An_unavailable_or_unknown_entity_means_the_fallback(string state)
    {
        // Why: a price entity that Home Assistant cannot read is not a price of zero and not the last price; the fallback is.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("500");
        p.Run(0.5);
        p.Report(state);

        p.Run(2);

        var block = Block(s);
        Assert.Equal((0.30, PriceBasis.Fallback), (block.ExportPrice, block.PriceBasis));
    }

    [Theory]
    [InlineData("PLN/kWh", "PLN/MWh")]
    [InlineData("PLN/MWh", "EUR/MWh")]
    [InlineData("PLN/MWh", "PLN/kWh")]
    public void A_wrong_unit_or_a_wrong_currency_is_not_a_price_and_the_fallback_stands_in(string declared, string reported)
    {
        // Why: a unit that differs from the declaration (a 1000x error) or a currency that is not the user's is never converted on a guess.
        var (s, p) = Morning(Heater(), DynamicExport(), PriceScenarios.SignalYaml(declared));
        p.Report("200", reported);

        p.Run(2);

        var block = Block(s);
        Assert.Equal((0.30, PriceBasis.Fallback), (block.ExportPrice, block.PriceBasis));
        Assert.Equal(Switchboard.Domain.Signals.SignalDetail.UnitMismatch, p.Reading().Detail);
    }

    [Fact]
    public void A_price_signal_in_another_currency_than_the_section_is_unusable_from_the_first_sample()
    {
        // Why: the signal declares EUR/MWh while economics.currency is PLN: 200 EUR/MWh compared with a value in PLN would be a silent error.
        var (s, p) = Morning(Heater(), DynamicExport(), PriceScenarios.SignalYaml("EUR/MWh"));
        p.Report("200");

        p.Run(2);

        Assert.Equal(PriceBasis.Fallback, Block(s).PriceBasis);
        Assert.Contains(p.Configuration.Warnings, w => w.Code == ConfigurationIssueCodes.UnitMismatch);
    }

    [Fact]
    public void A_price_that_has_never_been_reported_is_at_its_fallback_from_the_start()
    {
        // Why: right after a start Home Assistant has not delivered the entity yet; the fallback is the price until it does.
        var (s, p) = Morning(Heater(), DynamicExport());

        p.Run(1);

        Assert.Equal(PriceBasis.Fallback, Block(s).PriceBasis);
    }

    [Fact]
    public void Without_any_price_inputs_a_dynamic_price_is_at_its_fallback()
    {
        // Why: a store cycle that carries no price view (no price signal mapped, a replay) must not leave a dynamic spec without a price.
        var (s, _) = Morning(Heater(), DynamicExport());
        s.Prices = null;

        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 1, 1, 1);

        Assert.Equal((0.30, PriceBasis.Fallback), (Block(s).ExportPrice, Block(s).PriceBasis));
    }

    [Fact]
    public void A_device_that_follows_the_export_price_is_worth_a_multiple_of_the_live_price_and_the_fallback_when_it_is_gone()
    {
        // Why: value_follows with a live price (ECO-05 with ECO-10). Factor 2: live 0.20 is a value of 0.40 (followed), the fallback 0.30 is 0.60
        // with the basis fallback; the order key follows, and never the stale 0.40.
        var device = Heater(value: 0.10, extra: "    value_follows: export_price\n    value_factor: 2\n");
        var (s, p) = Morning(device, DynamicExport());
        p.Report("200");
        p.Run(1);
        var live = Block(s);
        Assert.Equal((0.40, ValueBasis.Followed, 0.40), (live.VEff, live.ValueBasis, live.OrderKey));

        p.Run(3);

        var fallback = Block(s);
        Assert.Equal((0.60, ValueBasis.Fallback, 0.60), (fallback.VEff, fallback.ValueBasis, fallback.OrderKey));
    }

    [Fact]
    public void A_dynamic_import_price_with_distribution_zones_adds_the_zone_to_the_live_price_and_the_zone_start_pays_it()
    {
        // Why: the Polish shape: market price plus the distribution zone. 11:00 on a workday is the morning peak (add 0.40); live 300 PLN/MWh is
        // 0.30, so the zone costs 0.70 and a car worth 2.03 on pv_and_zone:morning_peak starts on the zone class with zone_allowed.
        const string Tariff = """
              tariff:
                signal: market_price
                fallback: 1.10
                add:
                  default_zone: offpeak
                  zones:
                    - id: morning_peak
                      price: 0.40
                      when:
                        - { days: [workday], hours: ["07:00-13:00"] }
                    - id: offpeak
                      price: 0.10
                      when: []
            """;
        var economics = Economics(tariff: Tariff + "\n", exportPrice: "0.35");
        var (s, p) = PriceScenarios.Create(Device("dev", "L1", 6.6, 1500, 2.03, "pv_and_zone:morning_peak"), Workday(11), ["dev"], economics);
        p.Report("300");

        p.Run(2, wh: null);

        var block = Block(s);
        Assert.Equal(("morning_peak", 0.70, PriceBasis.Live), (block.Zone, block.ImportPrice!.Value, block.PriceBasis));
        Assert.Equal(EnergyClass.Zone, block.Class);
        Assert.Contains(Starts(s, "dev"), d => d.Reason == ReasonCode.ZoneAllowed);
    }

    [Fact]
    public void A_dynamic_import_price_at_its_fallback_uses_the_fallback_spec_and_its_own_zone()
    {
        // Why: the fallback is a spec of its own (here a flat 1.10, no zone): while the entity is down the zone class has no zone to be in, the
        // price is the fallback's, and the basis says so.
        const string Tariff = """
              tariff:
                signal: market_price
                fallback: 1.10
                add: 0.40
            """;
        var (s, p) = PriceScenarios.Create(Device("dev", "L1", 6.6, 1500, 2.03, "pv_only"), Workday(11), ["dev"], Economics(tariff: Tariff + "\n"));

        p.Run(1, wh: null);

        var block = Block(s);
        Assert.Equal((1.10, PriceBasis.Fallback, null), (block.ImportPrice, block.PriceBasis, block.Zone));
    }

    [Fact]
    public void The_fallback_is_an_event_once_on_entering_and_once_on_leaving_and_the_log_does_not_repeat()
    {
        // Why: ECO-10 / the failure table: "a tariff_fallback system event on enter and exit". It is not a decision (no reason code exists for it),
        // so dispatch reports each change once per step it happens; a steady fallback or a steady live price is silent, so nothing flaps in the log.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("200");
        p.Run(1);
        var changes = new List<TariffFallbackChange>();

        void Collect(double minutes)
        {
            var count = s.Dispatches.Count;
            p.Run(minutes);
            changes.AddRange(s.Dispatches.Skip(count).SelectMany(d => d.TariffFallback));
        }

        Collect(5);
        Assert.Equal([new TariffFallbackChange("export", PriceScenarios.Signal, true)], changes);

        p.Report("210");
        Collect(1);
        Assert.Equal([new TariffFallbackChange("export", PriceScenarios.Signal, true), new TariffFallbackChange("export", PriceScenarios.Signal, false)], changes);
        Collect(0.5);
        Assert.Equal(2, changes.Count);

        // Silent again for 3 minutes: the price turns stale at max_age and enters once more; nothing repeats while it stays there.
        Collect(5);
        Assert.Equal(3, changes.Count);
        Assert.True(changes[2].Entered);
    }

    [Fact]
    public void The_log_gets_one_line_when_the_fallback_is_entered_and_one_when_it_is_left_and_none_in_between()
    {
        // Why: the event has no reason code, so the add-on log is where it is recorded; a line per step would drown the log while an entity is down
        // for hours, and an entity id in it would be a leak of mapping detail the core does not have.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("200");
        p.Run(1);
        p.Run(5);
        p.Report("210");
        p.Run(1);
        var log = new LogCapture();
        var holder = new DispatchStatusHolder(DispatchStatus.Initial(s.Now, s.Mode), log.For<DispatchStatusHolder>());

        foreach (var evaluation in s.Dispatches)
        {
            holder.Publish(DispatchStatus.Initial(evaluation.At, s.Mode) with { TariffFallback = evaluation.TariffFallback });
        }

        var lines = log.Entries.Where(e => e.Text.StartsWith("tariff_fallback", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("tariff_fallback entered", lines[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("tariff_fallback left", lines[1].Text, StringComparison.Ordinal);
        Assert.Contains(PriceScenarios.Signal, lines[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(PriceScenarios.Entity, log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void No_fallback_event_for_a_static_price_and_none_without_an_economics_section()
    {
        // Why: a flat or zoned price is never "in fallback"; the event exists only for a dynamic spec, so a static file stays silent.
        var s = Create(Heater(), Workday(11), ["dev"]);
        Export(s, 3000);
        Run(s, Feed(s, 600), 3);

        Assert.All(s.Dispatches, d => Assert.Empty(d.TariffFallback));
    }

    [Fact]
    public void A_dynamic_price_never_widens_what_the_guard_allows()
    {
        // Why: protection outranks everything. A price so low that surplus is free, a heater that wants to start, and a phase at its limit:
        // the live price changes the allowance but the guard's envelope still decides, so nothing is switched on while the phase is over.
        var (s, p) = Morning(Heater(), DynamicExport());
        p.Report("0");
        var feed = p.FeedWith(600);
        feed();

        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 24.9, 24.9, 24.9, feed);

        Assert.Empty(Starts(s, "dev"));
    }
}
