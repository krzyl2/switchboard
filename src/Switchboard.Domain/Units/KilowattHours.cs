using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// Energy in kilowatt-hours. Arithmetic exists only within the unit: there is no
/// operator or conversion to any other unit, so a value cannot silently change meaning.
/// </summary>
public readonly record struct KilowattHours(double Value) : IComparable<KilowattHours>
{
    public static KilowattHours Zero => new(0);

    public static KilowattHours operator +(KilowattHours left, KilowattHours right) => new(left.Value + right.Value);

    public static KilowattHours operator -(KilowattHours left, KilowattHours right) => new(left.Value - right.Value);

    public static KilowattHours operator -(KilowattHours value) => new(-value.Value);

    public static KilowattHours operator *(KilowattHours value, double factor) => new(value.Value * factor);

    public static KilowattHours operator *(double factor, KilowattHours value) => new(value.Value * factor);

    public static KilowattHours operator /(KilowattHours value, double divisor) => new(value.Value / divisor);

    public static bool operator <(KilowattHours left, KilowattHours right) => left.Value < right.Value;

    public static bool operator >(KilowattHours left, KilowattHours right) => left.Value > right.Value;

    public static bool operator <=(KilowattHours left, KilowattHours right) => left.Value <= right.Value;

    public static bool operator >=(KilowattHours left, KilowattHours right) => left.Value >= right.Value;

    public static KilowattHours Max(KilowattHours left, KilowattHours right) => left.Value >= right.Value ? left : right;

    public KilowattHours Abs() => new(Math.Abs(Value));

    public int CompareTo(KilowattHours other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture) + " kWh";
}
