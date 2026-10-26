using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Economics;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// ECO-09 in the decision log: the dispatch inputs may carry an <c>economics</c> block (zone, prices, effective value, order key, energy class,
/// sources, settlement). It reads back exactly, is absent on the lines older versions wrote and without an economics section, and a block that is
/// present but not exactly what the writer writes makes the line no decision (an edited line must never be trusted as a price).
/// </summary>
public sealed class EconomicsDecisionJsonTests
{
    private static Decision CarStart()
    {
        var s = Create(HeatersAndCar(), Workday(23), ["heater_l1", "heater_l2", "heater_l3", "ev_charger"]);
        Export(s, -500);
        Run(s, Feed(s, -100), 3);
        return Starts(s, "ev_charger").First();
    }

    [Fact]
    public void The_economics_block_round_trips_through_the_log_line()
    {
        // Why: the log is the only record of what the economics of a decision were; the Log page and the API after a restart read it back.
        var decision = CarStart();
        var written = decision.Inputs.Dispatch!.Economics!;

        var line = DecisionJson.Serialize(decision);
        var read = DecisionJson.TryParse(line)!;

        Assert.Equal(written, read.Inputs.Dispatch!.Economics);
        Assert.Equal(line, DecisionJson.Serialize(read));
        Assert.Contains("\"codes\":6", line, StringComparison.Ordinal);
        Assert.Contains("\"class\":\"zone\"", line, StringComparison.Ordinal);
        Assert.Contains("\"sources\":\"pv_and_zone:offpeak\"", line, StringComparison.Ordinal);
        Assert.Equal(ReasonCode.ZoneAllowed, read.Reason);
    }

    [Fact]
    public void A_decision_without_economics_writes_no_key_and_reads_as_none()
    {
        // Why: lines of 0.9 and every decision without an economics: section have no key, and must keep reading, unchanged byte for byte when written again.
        var s = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1", "heater_l2", "heater_l3"], OneBoundary);
        Export(s, 3000);
        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 5, 5, 5);
        var dispatch = s.Decisions.First(d => d.Inputs.Dispatch is not null);

        var line = DecisionJson.Serialize(dispatch);

        Assert.DoesNotContain("economics", line, StringComparison.Ordinal);
        Assert.Null(DecisionJson.TryParse(line)!.Inputs.Dispatch!.Economics);
        Assert.Equal(line, DecisionJson.Serialize(DecisionJson.TryParse(line)!));
    }

    [Theory]
    [InlineData("\"class\":\"zone\"", "\"class\":\"grid\"")]
    [InlineData("\"class\":\"zone\"", "\"class\":null")]
    [InlineData("\"price_basis\":\"zone\"", "\"price_basis\":\"spot\"")]
    [InlineData("\"value_basis\":\"static\"", "\"value_basis\":\"guessed\"")]
    [InlineData("\"settlement\":\"hourly_netting\"", "\"settlement\":\"net_metering\"")]
    [InlineData("\"sources\":\"pv_and_zone:offpeak\"", "\"sources\":\"everything\"")]
    [InlineData("\"import_price\":0.6", "\"import_price\":-0.6")]
    [InlineData("\"import_price\":0.6", "\"import_price\":\"0.6\"")]
    [InlineData("\"v_eff\":2.03", "\"v_eff\":null")]
    [InlineData("\"order_key\":2.03", "\"order_key\":\"high\"")]
    [InlineData("\"economics\":{", "\"economics\":[{")]
    public void A_present_but_malformed_economics_block_makes_the_line_no_decision(string good, string bad)
    {
        // Why: like the balance block. A line a person edited, or a writer of another version, must be skipped, not half-read: a price the add-on
        // did not write must never be shown as the price a decision was made at.
        var line = DecisionJson.Serialize(CarStart());
        Assert.Contains(good, line, StringComparison.Ordinal);

        Assert.Null(DecisionJson.TryParse(line.Replace(good, bad, StringComparison.Ordinal)));
    }

    [Fact]
    public void A_missing_key_of_the_block_makes_the_line_no_decision()
    {
        // Why: a block with a key missing is a different shape, not an older one; only the whole key may be absent.
        var line = DecisionJson.Serialize(CarStart());

        Assert.Null(DecisionJson.TryParse(line.Replace("\"order_key\":2.03,", string.Empty, StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse(line.Replace("\"settlement\":\"hourly_netting\"", "\"settlement_model\":\"hourly_netting\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Prices_may_be_null_where_no_price_exists_and_a_flat_basis_is_read()
    {
        // Why: no export price configured, or a flat tariff, are real worlds; they must read back as null prices and a flat basis, not fail.
        var s = Create(One("pv_only", 0.35), Workday(11), ["dev"], Economics(exportPrice: null, tariff: "  tariff: 0.80\n"));
        Export(s, 3000);
        Run(s, Feed(s, 600), 3);
        var decision = Starts(s, "dev").First();

        var read = DecisionJson.TryParse(DecisionJson.Serialize(decision))!.Inputs.Dispatch!.Economics!;

        Assert.Equal((null, 0.80, null, PriceBasis.Flat, EnergyClass.Pv), (read.Zone, read.ImportPrice, read.ExportPrice, read.PriceBasis, read.Class));
    }

    [Theory]
    [InlineData("200", PriceBasis.Live, 0.20, "live")]
    [InlineData(null, PriceBasis.Fallback, 0.30, "fallback")]
    public void A_live_and_a_fallback_price_basis_are_written_and_read_back(string? reported, PriceBasis basis, double exportPrice, string written)
    {
        // Why: ECO-10. The log is where a user checks which basis a decision rested on; live and fallback must survive the log line, not just be
        // accepted by the reader.
        var (s, prices) = PriceScenarios.Create(One("pv_only", 0.35), Workday(11), ["dev"], Economics(exportPrice: "\n      signal: market_price\n      fallback: 0.30\n"));
        Export(s, 3000);
        if (reported is not null)
        {
            prices.Report(reported);
        }

        prices.Run(3);
        var decision = Starts(s, "dev").First();

        var line = DecisionJson.Serialize(decision);
        var read = DecisionJson.TryParse(line)!.Inputs.Dispatch!.Economics!;

        Assert.Contains($"\"price_basis\":\"{written}\"", line, StringComparison.Ordinal);
        Assert.Equal((basis, exportPrice), (read.PriceBasis, read.ExportPrice));
    }
}
