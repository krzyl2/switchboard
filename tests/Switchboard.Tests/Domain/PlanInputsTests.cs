using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The per-slot inputs of a plan (price, PV, base load). Why it matters: the planner picks the cheapest kWh slot by slot, so a price taken at the slot start
/// instead of time-weighted, a curve that silently replaces the tariff where it has no data, or a surplus that ignores the house load would each move
/// the plan to the wrong hour.
/// </summary>
public sealed class PlanInputsTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly ForecastParameters Parameters = new(Hour, 0.85, TimeSpan.FromDays(14), 0.8, 400);

    // Peak 07:30-13:00 on workdays at 0.40, off-peak 0.10: the zone edge at :30 falls inside an hourly slot on purpose.
    private static readonly Tariff Import = new(
        new ZonedPrice(
            [
                new TariffZone("peak", 0.40, [new ZoneRule([DayClass.Workday], [], [new HoursRange((7 * 60) + 30, 13 * 60)])]),
                new TariffZone("offpeak", 0.10, []),
            ],
            "offpeak"),
        HolidayCalendar.Empty,
        Warsaw);

    private static readonly Tariff Export = new(new FlatPrice(0.35), HolidayCalendar.Empty, Warsaw);

    // Wednesday 2026-07-15 (CEST).
    private static DateTimeOffset At(int hour, int minute = 0, int day = 15) => new(2026, 7, day, hour, minute, 0, TimeSpan.FromHours(2));

    private static PlanInputSources Sources(PvForecast? pv = null, PriceCurve? curve = null, Tariff? import = null, Tariff? export = null, LoadProfile? load = null) =>
        new(Warsaw, HolidayCalendar.Empty, import ?? Import, export, curve, null, pv ?? PvForecast.None, load ?? LoadProfile.Empty);

    [Fact]
    public void The_slot_price_is_the_time_weighted_mean_of_the_zones_inside_it()
    {
        // Why: a zone that changes at 07:30 is half of the 07:00 slot: (0.10 x 0.5 h + 0.40 x 0.5 h) / 1 h = 0.25, and the slot has no single zone.
        // Slots wholly inside a zone carry that zone's price: 06:00 off-peak 0.10, 09:00 peak 0.40.
        var inputs = PlanInputs.Build(At(6), At(10), null, Parameters, Sources());

        Assert.Equal([0.10, 0.25, 0.40, 0.40], inputs.Slots.Select(s => Math.Round(s.Import!.Value.PerKwh, 9)).ToArray());
        Assert.Equal(["offpeak", null, "peak", "peak"], inputs.Slots.Select(s => s.Zone).ToArray());
        Assert.All(inputs.Slots, s => Assert.Equal(SlotPriceBasis.Tariff, s.Import!.Value.Basis));
    }

    [Fact]
    public void Slots_match_the_tariff_at_every_boundary_over_both_clock_changes()
    {
        // Why: api/plan's slots must agree with what dispatch will pay. Where the tariff changes on the hour, each hourly slot's price equals the tariff's price at its start,
        // across 2026-03-29 (23 h) and 2026-10-25 (25 h) where a 24-hour assumption would shift every price by an hour.
        var onTheHour = new Tariff(
            new ZonedPrice([new TariffZone("peak", 0.40, [new ZoneRule([DayClass.Workday, DayClass.Weekend], [], [new HoursRange(7 * 60, 13 * 60), new HoursRange(16 * 60, 21 * 60)])]), new TariffZone("offpeak", 0.10, [])], "offpeak"),
            HolidayCalendar.Empty,
            Warsaw);

        foreach (var start in new[] { new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1)), new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2)) })
        {
            var inputs = PlanInputs.Build(start, start.AddHours(48), null, Parameters, Sources(import: onTheHour));

            Assert.Equal(48, inputs.Slots.Length);
            foreach (var slot in inputs.Slots)
            {
                Assert.Equal(onTheHour.PriceAt(slot.Slot.Start)!.Value.PerKwh, slot.Import!.Value.PerKwh, 9);
                Assert.Equal(onTheHour.ZoneAt(slot.Slot.Start)!.Id, slot.Zone);
            }
        }
    }

    [Fact]
    public void The_forward_curve_prices_the_slots_it_covers_and_the_tariff_the_rest()
    {
        // Why: a curve that stops at 08:00 must not leave the later slots unpriced or priced at its last value. Slots 06 and 07 come from the curve (0.50, 0.70);
        // 08:00 and 09:00 fall back to the tariff (peak 0.40). Basis curve for the first two, tariff for the rest, and the plan says the curve is partial.
        var curve = PriceCurve.From([new PriceCurveInterval(At(6), At(7), 0.50), new PriceCurveInterval(At(7), At(8), 0.70)]);

        var inputs = PlanInputs.Build(At(6), At(10), null, Parameters, Sources(curve: curve));

        Assert.Equal([0.50, 0.70, 0.40, 0.40], inputs.Slots.Select(s => Math.Round(s.Import!.Value.PerKwh, 9)).ToArray());
        Assert.Equal([SlotPriceBasis.Curve, SlotPriceBasis.Curve, SlotPriceBasis.Tariff, SlotPriceBasis.Tariff], inputs.Slots.Select(s => s.Import!.Value.Basis).ToArray());
        Assert.Contains(PlanNote.PriceCurvePartial, inputs.Notes);
        Assert.Equal(PlanPriceBasis.Partial, inputs.ImportPriceBasis);
        Assert.Equal(At(8), inputs.ImportCurveCoversUntil);
    }

    [Fact]
    public void A_slot_half_covered_by_the_curve_mixes_both_by_time()
    {
        // Why: curve 0.50 until 06:30, then the tariff (off-peak 0.10) for the other half of the 06:00 slot: 0.50 x 0.5 + 0.10 x 0.5 = 0.30, basis mixed.
        var curve = PriceCurve.From([new PriceCurveInterval(At(5), At(6, 30), 0.50)]);

        var inputs = PlanInputs.Build(At(6), At(7), null, Parameters, Sources(curve: curve));

        Assert.Equal(0.30, inputs.Slots[0].Import!.Value.PerKwh, 9);
        Assert.Equal(SlotPriceBasis.Mixed, inputs.Slots[0].Import!.Value.Basis);
    }

    [Fact]
    public void A_curve_over_the_whole_horizon_is_the_basis_and_raises_no_note()
    {
        // Why: the happy path of a market price: every slot priced from the curve, no warning in the plan.
        var curve = PriceCurve.FromStarts(Enumerable.Range(6, 4).Select(h => (At(h), 0.2 + (h / 100.0))), Hour);

        var inputs = PlanInputs.Build(At(6), At(10), null, Parameters, Sources(curve: curve));

        Assert.Equal(PlanPriceBasis.Curve, inputs.ImportPriceBasis);
        Assert.DoesNotContain(PlanNote.PriceCurvePartial, inputs.Notes);
        Assert.Equal(0.26, inputs.Slots[0].Import!.Value.PerKwh, 9);
    }

    [Fact]
    public void A_configured_curve_with_no_usable_data_is_priced_from_the_tariff_and_noted()
    {
        // Why: a stale curve is treated as missing (15-PLANNER): the tariff schedule prices everything and the plan says price_curve_partial; it never goes unpriced.
        var inputs = PlanInputs.Build(At(6), At(8), null, Parameters, Sources(curve: PriceCurve.Unusable));

        Assert.All(inputs.Slots, s => Assert.Equal(SlotPriceBasis.Tariff, s.Import!.Value.Basis));
        Assert.Contains(PlanNote.PriceCurvePartial, inputs.Notes);
        Assert.Equal(PlanPriceBasis.Tariff, inputs.ImportPriceBasis);
    }

    [Fact]
    public void With_no_curve_configured_nothing_is_noted()
    {
        // Why: an installation without a curve is not degraded; warning about it would teach the user to ignore the notes.
        var inputs = PlanInputs.Build(At(6), At(8), null, Parameters, Sources());

        Assert.Empty(inputs.Notes.Where(n => n == PlanNote.PriceCurvePartial));
    }

    [Fact]
    public void No_tariff_means_no_price_not_a_made_up_one()
    {
        // Why: a slot without a price must be visibly unpriced (null), never zero: zero would make every slot look free to the planner.
        var inputs = PlanInputs.Build(At(6), At(8), null, Parameters, new PlanInputSources(Warsaw, HolidayCalendar.Empty, null, null, null, null, PvForecast.None, LoadProfile.Empty));

        Assert.All(inputs.Slots, s =>
        {
            Assert.Null(s.Import);
            Assert.Null(s.Export);
        });
    }

    [Fact]
    public void The_export_price_is_its_own_tariff()
    {
        // Why: the forgone export income (what a kWh not exported earns) is what a PV-class kWh costs the planner; it must not be the import price.
        var inputs = PlanInputs.Build(At(6), At(8), null, Parameters, Sources(export: Export));

        Assert.All(inputs.Slots, s => Assert.Equal(0.35, s.Export!.Value.PerKwh, 9));
        Assert.Equal(0.10, inputs.Slots[0].Import!.Value.PerKwh, 9);
    }

    [Fact]
    public void The_surplus_is_the_derated_pv_minus_the_base_load_and_never_negative()
    {
        // Why: the planner's pv-class energy is S = max(0, pv x derate - base). PV 2.0 kWh x 0.85 = 1.7 against a 0.4 kWh fallback load: 1.3. A slot where the load is higher than the PV has zero surplus, not a negative one.
        var pv = PvForecast.FromPeriodEnergies([new PvPeriod(At(10), At(11), 2000), new PvPeriod(At(11), At(12), 200)]);

        var inputs = PlanInputs.Build(At(10), At(12), null, Parameters, Sources(pv: pv));

        Assert.Equal(1.7, inputs.Slots[0].PvKwh, 9);
        Assert.Equal(0.4, inputs.Slots[0].BaseKwh, 9);
        Assert.Equal(1.3, inputs.Slots[0].SurplusKwh, 9);
        Assert.Equal(0.17, inputs.Slots[1].PvKwh, 9);
        Assert.Equal(0, inputs.Slots[1].SurplusKwh);
    }

    [Fact]
    public void The_notes_name_a_missing_and_a_partial_forecast_and_the_basis_of_the_load()
    {
        // Why: the plan must say what it stood on: missing PV (zero for the horizon), PV that ends early, and a load that is only the fallback.
        var missing = PlanInputs.Build(At(6), At(12), null, Parameters, Sources());
        var partial = PlanInputs.Build(At(6), At(12), null, Parameters, Sources(pv: PvForecast.FromPowerPoints([new PvPowerPoint(At(6), 0), new PvPowerPoint(At(9), 500)])));
        var full = PlanInputs.Build(At(6), At(12), null, Parameters, Sources(pv: PvForecast.FromPowerPoints([new PvPowerPoint(At(6), 0), new PvPowerPoint(At(12), 500)])));

        Assert.Equal([PlanNote.PvForecastMissing], missing.Notes.ToArray());
        Assert.Equal(PvBasis.None, missing.PvBasis);
        Assert.Equal([PlanNote.PvForecastPartial], partial.Notes.ToArray());
        Assert.Equal(At(9), partial.PvCoversUntil);
        Assert.Empty(full.Notes);
        Assert.Equal(LoadBasis.Fallback, full.LoadBasis);
        Assert.Equal(0, full.LoadDaysFilled);
    }

    [Fact]
    public void A_deadline_is_a_slot_edge_in_the_inputs_and_the_cut_halves_share_the_hour_price_by_time()
    {
        // Why: a 07:30 deadline cuts the 07:00 slot; the off-peak half (0.10) and the peak half (0.40) then each carry their own price instead of the 0.25 average.
        var inputs = PlanInputs.Build(At(6), At(9), [At(7, 30)], Parameters, Sources());

        Assert.Equal(4, inputs.Slots.Length);
        Assert.Equal(At(7, 30), inputs.Slots[1].Slot.End);
        Assert.Equal([0.10, 0.10, 0.40, 0.40], inputs.Slots.Select(s => Math.Round(s.Import!.Value.PerKwh, 9)).ToArray());
    }

    [Fact]
    public void The_same_inputs_give_the_same_slots()
    {
        // Why: the planner is deterministic from its inputs; the assembly under it must not depend on order or state. Curve intervals given in reverse give equal slots.
        var forward = PriceCurve.From([new PriceCurveInterval(At(6), At(7), 0.5), new PriceCurveInterval(At(7), At(8), 0.7)]);
        var reverse = PriceCurve.From([new PriceCurveInterval(At(7), At(8), 0.7), new PriceCurveInterval(At(6), At(7), 0.5)]);

        var a = PlanInputs.Build(At(6), At(10), null, Parameters, Sources(curve: forward));
        var b = PlanInputs.Build(At(6), At(10), null, Parameters, Sources(curve: reverse));

        Assert.Equal(a.Slots, b.Slots);
    }

    [Fact]
    public void A_malformed_curve_is_refused()
    {
        // Why: overlapping or non-finite intervals would double-price a slot or poison a sum.
        Assert.Throws<ArgumentException>(() => PriceCurve.From([new PriceCurveInterval(At(6), At(8), 0.5), new PriceCurveInterval(At(7), At(9), 0.5)]));
        Assert.Throws<ArgumentException>(() => PriceCurve.From([new PriceCurveInterval(At(6), At(6), 0.5)]));
        Assert.Throws<ArgumentException>(() => PriceCurve.From([new PriceCurveInterval(At(6), At(7), double.NaN)]));
    }
}
