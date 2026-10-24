using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// A percentage (0-100 for a state of charge, for example). Arithmetic exists only within the unit: there is no
/// operator or conversion to any other unit, so a value cannot silently change meaning.
/// </summary>
public readonly record struct Percent(double Value) : IComparable<Percent>
{
    public static Percent Zero => new(0);

    public static Percent operator +(Percent left, Percent right) => new(left.Value + right.Value);

    public static Percent operator -(Percent left, Percent right) => new(left.Value - right.Value);

    public static Percent operator -(Percent value) => new(-value.Value);

    public static Percent operator *(Percent value, double factor) => new(value.Value * factor);

    public static Percent operator *(double factor, Percent value) => new(value.Value * factor);

    public static Percent operator /(Percent value, double divisor) => new(value.Value / divisor);

    public static bool operator <(Percent left, Percent right) => left.Value < right.Value;

    public static bool operator >(Percent left, Percent right) => left.Value > right.Value;

    public static bool operator <=(Percent left, Percent right) => left.Value <= right.Value;

    public static bool operator >=(Percent left, Percent right) => left.Value >= right.Value;

    public static Percent Max(Percent left, Percent right) => left.Value >= right.Value ? left : right;

    public Percent Abs() => new(Math.Abs(Value));

    public int CompareTo(Percent other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture) + " %";
}
