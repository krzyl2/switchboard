using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// A price in the configured currency per kilowatt-hour (the signal layer divided per-MWh prices already). The currency label belongs to
/// configuration. Its only cross-unit product is with energy, which is <see cref="Money"/>.
/// </summary>
public readonly record struct PricePerKwh(double Value) : IComparable<PricePerKwh>
{
    public static PricePerKwh Zero => new(0);

    public static PricePerKwh operator +(PricePerKwh left, PricePerKwh right) => new(left.Value + right.Value);

    public static PricePerKwh operator -(PricePerKwh left, PricePerKwh right) => new(left.Value - right.Value);

    public static PricePerKwh operator -(PricePerKwh value) => new(-value.Value);

    public static PricePerKwh operator *(PricePerKwh value, double factor) => new(value.Value * factor);

    public static PricePerKwh operator *(double factor, PricePerKwh value) => new(value.Value * factor);

    public static PricePerKwh operator /(PricePerKwh value, double divisor) => new(value.Value / divisor);

    /// <summary>What <paramref name="energy"/> costs at this price.</summary>
    public static Money operator *(PricePerKwh price, KilowattHours energy) => new(price.Value * energy.Value);

    public static Money operator *(KilowattHours energy, PricePerKwh price) => price * energy;

    public static bool operator <(PricePerKwh left, PricePerKwh right) => left.Value < right.Value;

    public static bool operator >(PricePerKwh left, PricePerKwh right) => left.Value > right.Value;

    public static bool operator <=(PricePerKwh left, PricePerKwh right) => left.Value <= right.Value;

    public static bool operator >=(PricePerKwh left, PricePerKwh right) => left.Value >= right.Value;

    public static PricePerKwh Max(PricePerKwh left, PricePerKwh right) => left.Value >= right.Value ? left : right;

    public static PricePerKwh Min(PricePerKwh left, PricePerKwh right) => left.Value <= right.Value ? left : right;

    public int CompareTo(PricePerKwh other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.####", CultureInfo.InvariantCulture) + "/kWh";
}
