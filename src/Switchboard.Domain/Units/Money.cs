using System.Globalization;

namespace Switchboard.Domain.Units;

/// <summary>An amount in the configured currency; the label belongs to configuration, never to code. Arithmetic exists only within the unit.</summary>
public readonly record struct Money(double Amount) : IComparable<Money>
{
    public static Money Zero => new(0);

    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);

    public static Money operator -(Money left, Money right) => new(left.Amount - right.Amount);

    public static Money operator -(Money value) => new(-value.Amount);

    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;

    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;

    public static bool operator <=(Money left, Money right) => left.Amount <= right.Amount;

    public static bool operator >=(Money left, Money right) => left.Amount >= right.Amount;

    public static Money Max(Money left, Money right) => left.Amount >= right.Amount ? left : right;

    public static Money Min(Money left, Money right) => left.Amount <= right.Amount ? left : right;

    public int CompareTo(Money other) => Amount.CompareTo(other.Amount);

    public override string ToString() => Amount.ToString("0.####", CultureInfo.InvariantCulture);
}
