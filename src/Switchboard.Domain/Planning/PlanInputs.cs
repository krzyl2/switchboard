using System.Collections.Immutable;
using Switchboard.Domain.Economics;

namespace Switchboard.Domain.Planning;

/// <summary>A condition of the inputs that the plan states (15-PLANNER, notes of <c>api/plan</c>); the planner never guesses past it.</summary>
public enum PlanNote
{
    /// <summary>No usable PV forecast: zero PV for the whole horizon.</summary>
    PvForecastMissing,

    /// <summary>The PV forecast ends before the horizon: zero beyond it.</summary>
    PvForecastPartial,

    /// <summary>A price curve was supplied but does not cover the horizon: the tariff schedule prices the rest.</summary>
    PriceCurvePartial,

    /// <summary>A goal's need could not be placed (a soft goal not worth its price, a hard goal beyond capacity).</summary>
    GoalShortfall,

    /// <summary>A hard goal cannot be met by its deadline even at full power: the plan delivers the most it can.</summary>
    GoalInfeasible,

    /// <summary>A hard goal is urgent: its latest safe start has come (or its sources fell short and a zone was added).</summary>
    GoalUrgent,

    /// <summary>A hard goal's deadline passed with the need unmet; still pursued.</summary>
    GoalMissed,

    /// <summary>A goal input is stale or unavailable, the goal is inactive.</summary>
    GoalInputStale,

    /// <summary>The solver exceeded <c>planner.max_solve_time</c>; the last plan stays.</summary>
    SolverTimeout,

    /// <summary>The solver's result failed the verifier or the solver threw; the last plan stays.</summary>
    PlanInvalid,
}

/// <summary>Where the import prices of the plan stand: the tariff alone, the curve over the whole horizon, or the curve with the tariff for the rest.</summary>
public enum PlanPriceBasis
{
    Tariff,
    Curve,
    Partial,
}

/// <summary>Everything the planner knows about one slot before any device is placed (15-PLANNER, "What the plan is").</summary>
/// <param name="Zone">The import tariff's zone when one zone holds the whole slot, else null.</param>
/// <param name="Import">Null where no import price exists.</param>
/// <param name="Export">Null where no export price exists.</param>
/// <param name="PvKwh">Derated PV energy.</param>
/// <param name="BaseKwh">The forecast uncontrolled house load.</param>
public sealed record PlanInputSlot(Slot Slot, string? Zone, SlotPrice? Import, SlotPrice? Export, double PvKwh, double BaseKwh)
{
    /// <summary>The PV energy left after the house load (storage charge is not subtracted before the storage is planned).</summary>
    public double SurplusKwh => Math.Max(0, PvKwh - BaseKwh);
}

/// <summary>The sources a plan's inputs are made from, as plain values: tariffs, curves, forecasts and the load profile. No entity, no signal-layer type.</summary>
/// <param name="ImportCurve">Null when no curve is configured; <see cref="PriceCurve.Unusable"/> when one is configured but has no usable data.</param>
public sealed record PlanInputSources(
    TimeZoneInfo Zone,
    HolidayCalendar Holidays,
    Tariff? ImportTariff,
    Tariff? ExportTariff,
    PriceCurve? ImportCurve,
    PriceCurve? ExportCurve,
    PvForecast Pv,
    LoadProfile Load);

/// <summary>
/// The per-slot inputs of a plan (slice 3 of 15-PLANNER): the slot grid, the price of each slot (time-weighted, curve over tariff), the derated PV
/// forecast and the house load forecast, with the basis and coverage of each, and the notes the plan states. Deterministic from its arguments:
/// the clock is the caller's <c>now</c>.
/// </summary>
public sealed record PlanInputs(
    SlotGrid Grid,
    ImmutableArray<PlanInputSlot> Slots,
    PvBasis PvBasis,
    PvCoverage PvCoverage,
    DateTimeOffset? PvCoversUntil,
    LoadBasis LoadBasis,
    int LoadDaysFilled,
    PlanPriceBasis ImportPriceBasis,
    DateTimeOffset? ImportCurveCoversUntil,
    ImmutableArray<PlanNote> Notes)
{
    /// <param name="now">The first slot starts here.</param>
    /// <param name="horizonEnd">The last slot ends here.</param>
    /// <param name="cuts">Deadlines: each is a slot edge.</param>
    public static PlanInputs Build(DateTimeOffset now, DateTimeOffset horizonEnd, IEnumerable<DateTimeOffset>? cuts, ForecastParameters parameters, PlanInputSources sources)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sources);
        var grid = SlotGrid.Build(now, horizonEnd, parameters.Slot, sources.Zone, cuts);
        var pv = sources.Pv.Slots(grid, parameters);
        var load = LoadForecast.Slots(sources.Load, grid, parameters, sources.Holidays);
        var slots = ImmutableArray.CreateBuilder<PlanInputSlot>(grid.Count);
        var importGap = false;
        for (var i = 0; i < grid.Count; i++)
        {
            var slot = grid.Slots[i];
            var import = SlotPrices.Mean(slot, sources.ImportTariff, sources.ImportCurve, out var gap);
            importGap |= gap;
            var export = SlotPrices.Mean(slot, sources.ExportTariff, sources.ExportCurve, out var exportGap);
            importGap |= exportGap;
            slots.Add(new PlanInputSlot(slot, import?.Zone, import, export, pv.Kwh[i], load.Kwh[i]));
        }

        var notes = ImmutableArray.CreateBuilder<PlanNote>();
        if (pv.Coverage == PvCoverage.Missing)
        {
            notes.Add(PlanNote.PvForecastMissing);
        }
        else if (pv.Coverage == PvCoverage.Partial)
        {
            notes.Add(PlanNote.PvForecastPartial);
        }

        if (importGap)
        {
            notes.Add(PlanNote.PriceCurvePartial);
        }

        var basis = sources.ImportCurve is null || sources.ImportCurve.Intervals.IsEmpty
            ? PlanPriceBasis.Tariff
            : slots.All(s => s.Import is { Basis: SlotPriceBasis.Curve }) ? PlanPriceBasis.Curve : PlanPriceBasis.Partial;
        return new PlanInputs(grid, slots.ToImmutable(), pv.Basis, pv.Coverage, pv.CoversUntil, load.Basis, load.DaysFilled, basis, sources.ImportCurve?.CoversUntil, notes.ToImmutable());
    }
}
