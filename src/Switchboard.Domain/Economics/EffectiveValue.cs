namespace Switchboard.Domain.Economics;

/// <summary>Which price a device's value per kWh follows (<c>value_follows</c>).</summary>
public enum ValueFollows
{
    Static,
    ImportPrice,
    ExportPrice,
}

/// <summary>How a device's value at an instant came about, for the log.</summary>
public enum ValueBasis
{
    /// <summary>The static value_per_kwh: nothing is followed.</summary>
    Static,

    /// <summary>factor times the followed price.</summary>
    Followed,

    /// <summary>
    /// The followed price does not exist at this instant (the static value stands in), or it is a dynamic price at its configured
    /// fallback (factor times the fallback spec's value); never a last known value.
    /// </summary>
    Fallback,
}

/// <summary>A device's value model: the static value (and fallback), the price it follows and the factor (a heat pump's COP, a conversion).</summary>
public readonly record struct ValueSpec(double Static, ValueFollows Follows, double Factor)
{
    /// <summary>value_factor without a conversion: the identity, not a control parameter.</summary>
    public const double NeutralFactor = 1.0;
}

/// <summary>v_eff of a device at an instant.</summary>
public readonly record struct EffectiveValue(double PerKwh, ValueBasis Basis)
{
    /// <summary>
    /// <c>value_per_kwh</c> when static or when the followed price does not exist; else <c>value_factor * price</c>.
    /// </summary>
    public static EffectiveValue At(ValueSpec spec, PriceBook prices)
    {
        if (spec.Follows == ValueFollows.Static)
        {
            return new EffectiveValue(spec.Static, ValueBasis.Static);
        }

        var followed = spec.Follows == ValueFollows.ImportPrice ? prices.Import : prices.Export;
        return followed is { } price
            ? new EffectiveValue(spec.Factor * price.PerKwh, price.Basis == PriceBasis.Fallback ? ValueBasis.Fallback : ValueBasis.Followed)
            : new EffectiveValue(spec.Static, ValueBasis.Fallback);
    }
}

/// <summary>Which energy a kWh is: surplus (otherwise exported) or bought in a tariff zone.</summary>
public enum EnergyClass
{
    Pv,
    Zone,
}

/// <summary>What a kWh of one class costs a device at an instant.</summary>
/// <param name="Class">Surplus or zone.</param>
/// <param name="Zone">The zone id for the zone class; null for surplus.</param>
/// <param name="PerKwh">Currency per kWh.</param>
public readonly record struct EnergyCost(EnergyClass Class, string? Zone, double PerKwh)
{
    /// <summary>Surplus costs the forgone export income; null (the test is off, stated in the API) when no export price exists.</summary>
    public static EnergyCost? Pv(ISettlementModel model, DateTimeOffset t, PriceBook prices)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.ExportValue(t, prices) is { } price ? new EnergyCost(EnergyClass.Pv, null, price.PerKwh) : null;
    }

    /// <summary>Energy bought in the active zone costs the import price; null when no import price exists.</summary>
    public static EnergyCost? ZoneEnergy(PriceBook prices) =>
        prices.Import is { } price ? new EnergyCost(EnergyClass.Zone, price.Zone, price.PerKwh) : null;
}

public static class ValueTest
{
    // Floating-point noise in a difference of two prices (0.7 - 0.2 is 0.4999999999999999), not a control parameter.
    private const double Noise = 1e-9;

    /// <summary>A class is worth using while <c>v_eff - cost &gt;= min_margin</c>.</summary>
    public static bool Worth(EffectiveValue value, EnergyCost cost, double minMargin) =>
        value.PerKwh - cost.PerKwh >= minMargin - Noise;
}
