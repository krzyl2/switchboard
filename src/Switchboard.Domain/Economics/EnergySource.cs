using System.Diagnostics.CodeAnalysis;

namespace Switchboard.Domain.Economics;

/// <summary>A tariff zone's id. The configuration spells it, the signal and plan layers carry it; the wrapper only keeps it from being any other string.</summary>
public readonly record struct ZoneId : IComparable<ZoneId>
{
    public ZoneId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; } = "";

    /// <summary>The zone of a price or slot that may name none.</summary>
    public static ZoneId? Of(string? text) => text is null ? null : new ZoneId(text);

    public int CompareTo(ZoneId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;
}

/// <summary>
/// Which energy a kWh is: <see cref="Surplus"/> (otherwise exported) or <see cref="Grid"/> (bought, in a tariff zone when one is known). The only
/// representation of an energy source in the domain; its text forms (<c>pv</c>, <c>zone</c>, <c>zone:&lt;id&gt;</c>) exist at the edges only, in <see cref="EnergySourceText"/>.
/// </summary>
public abstract record EnergySource
{
    private EnergySource()
    {
    }

    /// <summary>Surplus first, then grid energy by zone id (ordinal; a grid energy without a zone first): the tie-break the planner uses.</summary>
    public static IComparer<EnergySource> Ordering { get; } = Comparer<EnergySource>.Create(static (a, b) => (a, b) switch
    {
        (Surplus, Surplus) => 0,
        (Surplus, _) => -1,
        (_, Surplus) => 1,
        (Grid { Zone: { } x }, Grid { Zone: { } y }) => x.CompareTo(y),
        (Grid { Zone: null }, Grid { Zone: null }) => 0,
        (Grid { Zone: null }, _) => -1,
        _ => 1,
    });

    /// <summary>Energy that would otherwise be exported.</summary>
    public sealed record Surplus : EnergySource
    {
        public static Surplus Instance { get; } = new();
    }

    /// <summary>Energy bought from the grid; <paramref name="Zone"/> is null where no single zone is known.</summary>
    public sealed record Grid(ZoneId? Zone) : EnergySource;
}

/// <summary>
/// The text forms of an <see cref="EnergySource"/>, for the edges (parsing and serialization) only. A plan entry's class is <c>pv</c>, <c>zone</c> or
/// <c>zone:&lt;id&gt;</c> (15-PLANNER); an economics class names the kind alone, <c>pv</c> or <c>zone</c>.
/// </summary>
public static class EnergySourceText
{
    public const string Pv = "pv";
    public const string Zone = "zone";

    private const string ZonePrefix = Zone + ":";

    /// <summary>A plan entry's class.</summary>
    public static string Plan(EnergySource source) => source switch
    {
        EnergySource.Surplus => Pv,
        EnergySource.Grid { Zone: { } zone } => ZonePrefix + zone.Value,
        EnergySource.Grid => Zone,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>The class a plan entry's text names; false for anything but <c>pv</c>, <c>zone</c> and <c>zone:&lt;id&gt;</c>.</summary>
    public static bool TryParsePlan(string? text, [NotNullWhen(true)] out EnergySource? source)
    {
        source = text switch
        {
            Pv => EnergySource.Surplus.Instance,
            Zone => new EnergySource.Grid(null),
            not null when text.StartsWith(ZonePrefix, StringComparison.Ordinal) => new EnergySource.Grid(new ZoneId(text[ZonePrefix.Length..])),
            _ => null,
        };
        return source is not null;
    }

    /// <summary>An economics class: the kind of energy, with no zone.</summary>
    public static string Class(EnergySource source) => source is EnergySource.Surplus ? Pv : Zone;

    /// <summary>The economics class a text names (grid with no zone); false for anything but <c>pv</c> and <c>zone</c>.</summary>
    public static bool TryParseClass(string? text, [NotNullWhen(true)] out EnergySource? source)
    {
        source = text switch
        {
            Pv => EnergySource.Surplus.Instance,
            Zone => new EnergySource.Grid(null),
            _ => null,
        };
        return source is not null;
    }
}
