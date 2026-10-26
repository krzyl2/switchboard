using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Signals;

/// <summary>
/// Units accepted per signal kind and their factor to the internal unit (A, W, V, kWh, %). The
/// only place a unit is converted (CLAUDE.md: unit conversion happens only in the signal layer).
/// </summary>
public static class SignalUnits
{
    private static readonly ImmutableDictionary<string, double> Factors = new Dictionary<string, double>
    {
        ["A"] = 1,
        ["mA"] = 0.001,
        ["W"] = 1,
        ["kW"] = 1000,
        ["V"] = 1,
        ["Wh"] = 0.001,
        ["kWh"] = 1,
        ["MWh"] = 1000,
        ["%"] = 1,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public static ImmutableArray<string> AllowedFor(SignalKind kind) => kind switch
    {
        SignalKind.Current => ["A", "mA"],
        SignalKind.Power => ["W", "kW"],
        SignalKind.Voltage => ["V"],
        SignalKind.EnergyCounter => ["Wh", "kWh", "MWh"],
        SignalKind.Percentage => ["%"],
        _ => [],
    };

    /// <summary>The factor from <paramref name="unit"/> to the internal unit of its kind (not for a price unit, see <see cref="TryParsePrice"/>).</summary>
    public static double Factor(string unit) => Factors[unit];

    private const string PerKwh = "/kWh";
    private const string PerMwh = "/MWh";
    private const double KwhPerMwh = 1000;

    /// <summary>
    /// A price unit is <c>&lt;label&gt;/kWh</c> or <c>&lt;label&gt;/MWh</c> (the label is the currency, written as in <c>economics.currency</c>).
    /// <paramref name="perMwh"/> says the number is per MWh and must be divided by 1000 to be per kWh (<see cref="PriceToKwh"/>).
    /// </summary>
    public static bool TryParsePrice(string? unit, out string label, out bool perMwh)
    {
        label = string.Empty;
        perMwh = false;
        if (unit is null)
        {
            return false;
        }

        var mwh = unit.EndsWith(PerMwh, StringComparison.Ordinal);
        if (!mwh && !unit.EndsWith(PerKwh, StringComparison.Ordinal))
        {
            return false;
        }

        label = unit[..^PerKwh.Length];
        perMwh = mwh;
        return label.Length > 0 && label.Trim() == label && !label.Contains('/', StringComparison.Ordinal);
    }

    /// <summary>A price in the declared unit as currency per kWh: the one place a per-MWh price is converted (a division, so 450 per MWh is exactly 0.45).</summary>
    public static double PriceToKwh(double raw, bool perMwh) => perMwh ? raw / KwhPerMwh : raw;

    /// <summary>
    /// A configured power expressed as the current it takes at the nominal voltage (unity power
    /// factor): how the reserve, configured in W, becomes the amperes protection compares with.
    /// </summary>
    public static Amperes CurrentAt(Watts power, Volts voltage) => new(power.Value / voltage.Value);

    /// <summary>A current expressed as power at the nominal voltage; for display at the presentation edge only, never for a decision.</summary>
    public static Watts PowerAt(Amperes current, Volts voltage) => new(current.Value * voltage.Value);

    /// <summary>The energy a constant power delivers over a duration (W × h ÷ 1000): the statistics' only integration step.</summary>
    public static KilowattHours EnergyOf(Watts power, TimeSpan duration) => new(power.Value * duration.TotalHours / WattsPerKilowatt);

    private const double WattsPerKilowatt = 1000;
}

public enum NormalizationOutcome
{
    Accepted,
    Unavailable,
    Rejected,
}

/// <summary>
/// A normalized sample. For an unsigned signal <see cref="Value"/> is the magnitude; its direction
/// is resolved later from the direction signal (SIG-03).
/// </summary>
public sealed record Normalization(NormalizationOutcome Outcome, SignalValue? Value, SignalDetail Detail);

/// <summary>Turns one Home Assistant state into a normalized value, or says precisely why it cannot (SIG-02).</summary>
public static class SignalNormalizer
{
    public const string UnavailableState = "unavailable";
    public const string UnknownState = "unknown";

    public static Normalization Normalize(SignalDefinition definition, string state, string? unit)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);

        switch (state)
        {
            case UnavailableState:
                return new Normalization(NormalizationOutcome.Unavailable, null, SignalDetail.StateUnavailable);
            case UnknownState:
                return new Normalization(NormalizationOutcome.Unavailable, null, SignalDetail.StateUnknown);
        }

        if (definition.Kind == SignalKind.State)
        {
            return Accept(new StateValue(state));
        }

        // A unit that differs from the declaration (or is missing) is a 1000× error waiting to happen
        // (W read as kW), so the sample is refused rather than converted on a guess.
        if (definition.Unit is not { } declared || !string.Equals(unit, declared, StringComparison.Ordinal))
        {
            return Reject(SignalDetail.UnitMismatch);
        }

        if (definition.Kind == SignalKind.Price)
        {
            return NormalizePrice(definition, declared, state);
        }

        // Never read as zero: a non-numeric state is no measurement at all.
        if (!double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) || !double.IsFinite(raw))
        {
            return Reject(SignalDetail.NotNumeric);
        }

        var value = raw * SignalUnits.Factor(declared);
        switch (definition.Sign)
        {
            case SignConvention.PositiveExport:
                value = -value;
                break;
            case SignConvention.Unsigned when value < 0:
                return Reject(SignalDetail.SignViolation);
        }

        return Accept(definition.Kind switch
        {
            SignalKind.Current => new CurrentValue(new Amperes(value)),
            SignalKind.Power => new PowerValue(new Watts(value)),
            SignalKind.Voltage => new VoltageValue(new Volts(value)),
            SignalKind.EnergyCounter => new EnergyValue(new KilowattHours(value)),
            _ => new PercentageValue(new Percent(value)),
        });
    }

    /// <summary>
    /// A price: the currency label of the unit must be the configured <c>economics.currency</c> (a price in another currency
    /// compared with the user's would be a silent error), and a per-MWh number is converted to per kWh here and nowhere else.
    /// Negative values pass: markets publish them; what a negative total means is the price spec's business.
    /// </summary>
    private static Normalization NormalizePrice(SignalDefinition definition, string declared, string state)
    {
        if (!SignalUnits.TryParsePrice(declared, out var label, out var perMwh)
            || !string.Equals(label, definition.PriceCurrency, StringComparison.Ordinal))
        {
            return Reject(SignalDetail.UnitMismatch);
        }

        if (!double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) || !double.IsFinite(raw))
        {
            return Reject(SignalDetail.NotNumeric);
        }

        var perKwh = SignalUnits.PriceToKwh(raw, perMwh);
        return double.IsFinite(perKwh) ? Accept(new PriceValue(perKwh)) : Reject(SignalDetail.NotNumeric);
    }

    /// <summary>The number in the internal unit, or null for a state value.</summary>
    public static double? Magnitude(SignalValue? value) => value switch
    {
        CurrentValue c => c.Current.Value,
        PowerValue p => p.Power.Value,
        VoltageValue v => v.Voltage.Value,
        EnergyValue e => e.Energy.Value,
        PriceValue p => p.PerKwh,
        PercentageValue p => p.Percentage.Value,
        _ => null,
    };

    private static Normalization Accept(SignalValue value) => new(NormalizationOutcome.Accepted, value, SignalDetail.None);

    private static Normalization Reject(SignalDetail detail) => new(NormalizationOutcome.Rejected, null, detail);
}
