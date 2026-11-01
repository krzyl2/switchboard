namespace Switchboard.Domain.Economics;

/// <summary>One price at every moment: no zone, never a change, the inputs are not read.</summary>
internal sealed class FlatTariff(FlatPrice spec) : Tariff
{
    public override bool IsZoned => false;

    public override TariffZone? ZoneAt(DateTimeOffset t, PriceInputs? prices = null) => null;

    public override PricePoint? PriceAt(DateTimeOffset t, PriceInputs? prices = null) => new PricePoint(spec.PerKwh, PriceBasis.Flat, null);

    public override TariffChange? NextChange(DateTimeOffset t, PriceInputs? prices = null) => null;
}
