using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// Electric current in amperes. Protection works on current (SIG-04). Arithmetic exists only within the unit: there is no
/// operator or conversion to any other unit, so a value cannot silently change meaning.
/// </summary>
public readonly record struct Amperes(double Value) : IComparable<Amperes>
{
    public static Amperes Zero => new(0);

    public static Amperes operator +(Amperes left, Amperes right) => new(left.Value + right.Value);

    public static Amperes operator -(Amperes left, Amperes right) => new(left.Value - right.Value);

    public static Amperes operator -(Amperes value) => new(-value.Value);

    public static Amperes operator *(Amperes value, double factor) => new(value.Value * factor);

    public static Amperes operator *(double factor, Amperes value) => new(value.Value * factor);

    public static Amperes operator /(Amperes value, double divisor) => new(value.Value / divisor);

    public static bool operator <(Amperes left, Amperes right) => left.Value < right.Value;

    public static bool operator >(Amperes left, Amperes right) => left.Value > right.Value;

    public static bool operator <=(Amperes left, Amperes right) => left.Value <= right.Value;

    public static bool operator >=(Amperes left, Amperes right) => left.Value >= right.Value;

    public static Amperes Max(Amperes left, Amperes right) => left.Value >= right.Value ? left : right;

    public Amperes Abs() => new(Math.Abs(Value));

    public int CompareTo(Amperes other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture) + " A";
}
