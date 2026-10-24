using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// Active power in watts. Dispatch works on power (SIG-04). Arithmetic exists only within the unit: there is no
/// operator or conversion to any other unit, so a value cannot silently change meaning.
/// </summary>
public readonly record struct Watts(double Value) : IComparable<Watts>
{
    public static Watts Zero => new(0);

    public static Watts operator +(Watts left, Watts right) => new(left.Value + right.Value);

    public static Watts operator -(Watts left, Watts right) => new(left.Value - right.Value);

    public static Watts operator -(Watts value) => new(-value.Value);

    public static Watts operator *(Watts value, double factor) => new(value.Value * factor);

    public static Watts operator *(double factor, Watts value) => new(value.Value * factor);

    public static Watts operator /(Watts value, double divisor) => new(value.Value / divisor);

    public static bool operator <(Watts left, Watts right) => left.Value < right.Value;

    public static bool operator >(Watts left, Watts right) => left.Value > right.Value;

    public static bool operator <=(Watts left, Watts right) => left.Value <= right.Value;

    public static bool operator >=(Watts left, Watts right) => left.Value >= right.Value;

    public static Watts Max(Watts left, Watts right) => left.Value >= right.Value ? left : right;

    public Watts Abs() => new(Math.Abs(Value));

    public int CompareTo(Watts other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture) + " W";
}
