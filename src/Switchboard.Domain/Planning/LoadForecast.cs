using System.Collections.Immutable;
using Switchboard.Domain.Economics;

namespace Switchboard.Domain.Planning;

/// <summary>What the load forecast stood on: no filled bin at all (the configured fallback), some bins missing (the mean of the filled ones), or every bin used.</summary>
public enum LoadBasis
{
    Fallback,
    Partial,
    Profile,
}

/// <summary>The house load of each slot of a grid, kWh, the basis, and how much of the profile is filled (bins, and whole days of 24 bins).</summary>
public sealed record LoadSlots(ImmutableArray<double> Kwh, LoadBasis Basis, int FilledBins, int DaysFilled);

/// <summary>The uncontrolled house load per slot from a <see cref="LoadProfile"/> (15-PLANNER): the bin of each local hour, holidays on the Sunday row, an empty bin the mean of the filled ones, no filled bin the fallback.</summary>
public static class LoadForecast
{
    private const double WattsPerKilowatt = 1000;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public static LoadSlots Slots(LoadProfile profile, SlotGrid grid, ForecastParameters parameters, HolidayCalendar holidays)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(holidays);
        var filled = profile.Bins.Where(b => b.Filled).Select(b => b.MeanKw).ToList();
        var filler = filled.Count > 0 ? filled.Average() : parameters.LoadFallbackW / WattsPerKilowatt;
        var anyEmpty = false;
        var kwh = ImmutableArray.CreateBuilder<double>(grid.Count);
        foreach (var slot in grid.Slots)
        {
            var energy = 0.0;

            // A slot may straddle local hours (a slot is at most an hour, but a first slot or a cut can start mid-hour): each piece takes its own hour's bin.
            foreach (var piece in SlotGrid.Build(slot.Start, slot.End, Hour, grid.Zone).Slots)
            {
                var bin = profile.Bins[LoadProfile.BinIndex(piece.Start, grid.Zone, holidays)];
                anyEmpty |= !bin.Filled;
                energy += (bin.Filled ? bin.MeanKw : filler) * piece.Hours;
            }

            kwh.Add(energy);
        }

        var basis = filled.Count == 0 ? LoadBasis.Fallback : anyEmpty ? LoadBasis.Partial : LoadBasis.Profile;
        return new LoadSlots(kwh.MoveToImmutable(), basis, filled.Count, filled.Count / 24);
    }
}
