using Switchboard.Domain.Units;

namespace Switchboard.Domain.Economics;

/// <summary>
/// The import and the export price at one instant, each with its basis; null where no such price exists (no
/// tariff, no export price configured, a moment no zone claims). Never a last known value.
/// </summary>
public readonly record struct PriceBook(PricePoint? Import, PricePoint? Export)
{
    public static PriceBook None => new(null, null);

    /// <summary>The book at <paramref name="t"/> from the two evaluated price specs (either may be absent).</summary>
    public static PriceBook At(DateTimeOffset t, Tariff? import, Tariff? export, PriceInputs? prices = null) => new(import?.PriceAt(t, prices), export?.PriceAt(t, prices));
}

/// <summary>An amount in the configured currency; the label belongs to configuration, never to code.</summary>
public readonly record struct Money(double Amount)
{
    public static Money Zero => new(0);
}

/// <summary>The energy a settlement period exchanged with the grid, both directions apart, kWh, each not negative.</summary>
public readonly record struct PeriodFlows(KilowattHours Exported, KilowattHours Imported)
{
    /// <summary>The period's net export: positive = exported more than imported.</summary>
    public KilowattHours NetExport => Exported - Imported;
}

/// <summary>The price of a settlement period: the time-weighted mean of the prices in force during it (14-ECONOMICS, Settlement).</summary>
public static class PeriodPrice
{
    /// <summary>
    /// Mean price over [<paramref name="from"/>, <paramref name="to"/>), each price weighted by how long it was in force
    /// (G13 zones and hourly market prices both change on the :00). Null for an empty period or when any moment of it has
    /// no price. The basis is the common one, else <see cref="PriceBasis.Zone"/>; the zone is kept only when it is one.
    /// </summary>
    public static PricePoint? Mean(Tariff tariff, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        if (to <= from)
        {
            return null;
        }

        var weighted = 0.0;
        PriceBasis? basis = null;
        string? zone = null;
        var mixedZones = false;
        var cursor = from;
        while (cursor < to)
        {
            if (tariff.PriceAt(cursor) is not { } price)
            {
                return null;
            }

            var end = tariff.NextChange(cursor) is { } change && change.At > cursor && change.At < to ? change.At : to;
            weighted += price.PerKwh * (end - cursor).TotalHours;
            basis = basis is null || basis == price.Basis ? price.Basis : PriceBasis.Zone;
            mixedZones |= zone is not null && zone != price.Zone;
            zone ??= price.Zone;
            cursor = end;
        }

        return new PricePoint(weighted / (to - from).TotalHours, basis!.Value, mixedZones ? null : zone);
    }
}
