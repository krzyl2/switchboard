namespace Switchboard.Domain.Economics;

/// <summary>
/// A price that follows a signal: live while the signal is usable, else the static fallback. The fallback is always there; the
/// add is optional (null adds nothing and names no zone while live).
/// </summary>
internal sealed class DynamicTariff(DynamicPrice spec, Tariff fallback, Tariff? add) : Tariff
{
    public override bool IsZoned => add?.IsZoned == true || fallback.IsZoned;

    public override TariffZone? ZoneAt(DateTimeOffset t, PriceInputs? prices = null) => Schedule(t, prices)?.ZoneAt(t);

    /// <summary>
    /// The signal plus add while the signal is usable and the sum is a price (finite, not negative: a market may publish a negative
    /// value, but a negative total is not a price the rest of the system can compare, so it is not guessed at and the fallback stands
    /// in); otherwise the fallback's price with the basis fallback.
    /// </summary>
    public override PricePoint? PriceAt(DateTimeOffset t, PriceInputs? prices = null)
    {
        if (prices?.Usable(spec.Signal) is { } live)
        {
            var addPoint = add?.PriceAt(t);
            if (add is null || addPoint is not null)
            {
                var total = live + (addPoint?.PerKwh ?? 0);
                if (double.IsFinite(total) && total >= 0)
                {
                    return new PricePoint(total, PriceBasis.Live, addPoint?.Zone);
                }
            }
        }

        return fallback.PriceAt(t) is { } point ? point with { Basis = PriceBasis.Fallback } : null;
    }

    public override TariffChange? NextChange(DateTimeOffset t, PriceInputs? prices = null) => Schedule(t, prices)?.NextChange(t);

    /// <summary>The schedule that names the zone now: the add while live, the fallback otherwise.</summary>
    private Tariff? Schedule(DateTimeOffset t, PriceInputs? prices) =>
        PriceAt(t, prices) is { Basis: PriceBasis.Live } ? add : fallback;
}
