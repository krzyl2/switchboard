using System.Collections.Immutable;

namespace Switchboard.Domain.Economics;

/// <summary>The dated list of public holidays the user maintains (14-ECONOMICS: no holiday signal in 2.0).</summary>
public sealed class HolidayCalendar
{
    public static readonly HolidayCalendar Empty = new([]);

    private readonly ImmutableHashSet<DateOnly> _dates;

    public HolidayCalendar(IEnumerable<DateOnly> dates)
    {
        ArgumentNullException.ThrowIfNull(dates);
        _dates = [.. dates];
    }

    public IReadOnlyCollection<DateOnly> Dates => _dates;

    public bool IsHoliday(DateOnly date) => _dates.Contains(date);

    /// <summary>True when no date of the list is in <paramref name="localYear"/>: the user has not renewed it.</summary>
    public bool HasNoDateIn(int localYear) => !_dates.Any(d => d.Year == localYear);
}
