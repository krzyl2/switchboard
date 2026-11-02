using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// Active power in watts. Dispatch works on power (SIG-04). Arithmetic exists only within the unit: there is no
/// operator or conversion to any other unit, so a value cannot silently change meaning, except the named product with a duration and
/// the kilowatt view.
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

    public static Watts Min(Watts left, Watts right) => left.Value <= right.Value ? left : right;

    /// <summary>
    /// The energy of this power held for <paramref name="duration"/>: W x h / 1000. The multiplication comes before the division, the order the
    /// meters and the planner's minimum-run arithmetic used by hand, so a migrated site keeps its floating-point result.
    /// </summary>
    public static KilowattHours operator *(Watts power, TimeSpan duration) => new(power.Value * duration.TotalHours / 1000);

    public static KilowattHours operator *(TimeSpan duration, Watts power) => power * duration;

    /// <summary>The same power in kilowatts (W / 1000), for the planner's capacity and time arithmetic; the only conversion out of watts.</summary>
    public Kilowatts ToKilowatts() => new(Value / 1000);

    public Watts Abs() => new(Math.Abs(Value));

    public int CompareTo(Watts other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture) + " W";
}
