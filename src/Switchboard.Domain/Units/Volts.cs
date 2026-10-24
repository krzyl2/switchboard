using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// Voltage in volts. Arithmetic exists only within the unit: there is no
/// operator or conversion to any other unit, so a value cannot silently change meaning.
/// </summary>
public readonly record struct Volts(double Value) : IComparable<Volts>
{
    public static Volts Zero => new(0);

    public static Volts operator +(Volts left, Volts right) => new(left.Value + right.Value);

    public static Volts operator -(Volts left, Volts right) => new(left.Value - right.Value);

    public static Volts operator -(Volts value) => new(-value.Value);

    public static Volts operator *(Volts value, double factor) => new(value.Value * factor);

    public static Volts operator *(double factor, Volts value) => new(value.Value * factor);

    public static Volts operator /(Volts value, double divisor) => new(value.Value / divisor);

    public static bool operator <(Volts left, Volts right) => left.Value < right.Value;

    public static bool operator >(Volts left, Volts right) => left.Value > right.Value;

    public static bool operator <=(Volts left, Volts right) => left.Value <= right.Value;

    public static bool operator >=(Volts left, Volts right) => left.Value >= right.Value;

    public static Volts Max(Volts left, Volts right) => left.Value >= right.Value ? left : right;

    public Volts Abs() => new(Math.Abs(Value));

    public int CompareTo(Volts other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture) + " V";
}
