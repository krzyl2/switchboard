using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>
/// Power in kilowatts, the planner's view of a capacity (energy per hour). Reached only through <see cref="Watts.ToKilowatts"/> and left only
/// through <see cref="ToWatts"/>; it multiplies a duration into energy and divides energy into a duration, nothing else crosses units.
/// </summary>
public readonly record struct Kilowatts(double Value) : IComparable<Kilowatts>
{
    public static Kilowatts Zero => new(0);

    public static Kilowatts operator +(Kilowatts left, Kilowatts right) => new(left.Value + right.Value);

    public static Kilowatts operator -(Kilowatts left, Kilowatts right) => new(left.Value - right.Value);

    public static Kilowatts operator -(Kilowatts value) => new(-value.Value);

    public static Kilowatts operator *(Kilowatts value, double factor) => new(value.Value * factor);

    public static Kilowatts operator *(double factor, Kilowatts value) => new(value.Value * factor);

    public static Kilowatts operator /(Kilowatts value, double divisor) => new(value.Value / divisor);

    /// <summary>The energy of this power held for <paramref name="duration"/>: kW x h.</summary>
    public static KilowattHours operator *(Kilowatts power, TimeSpan duration) => new(power.Value * duration.TotalHours);

    public static KilowattHours operator *(TimeSpan duration, Kilowatts power) => power * duration;

    public static bool operator <(Kilowatts left, Kilowatts right) => left.Value < right.Value;

    public static bool operator >(Kilowatts left, Kilowatts right) => left.Value > right.Value;

    public static bool operator <=(Kilowatts left, Kilowatts right) => left.Value <= right.Value;

    public static bool operator >=(Kilowatts left, Kilowatts right) => left.Value >= right.Value;

    public static Kilowatts Max(Kilowatts left, Kilowatts right) => left.Value >= right.Value ? left : right;

    public static Kilowatts Min(Kilowatts left, Kilowatts right) => left.Value <= right.Value ? left : right;

    /// <summary>The same power in watts (kW x 1000), for a comparison with a watt figure.</summary>
    public Watts ToWatts() => new(Value * 1000);

    public Kilowatts Abs() => new(Math.Abs(Value));

    public int CompareTo(Kilowatts other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture) + " kW";
}
