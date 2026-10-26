using System.Collections.Immutable;

namespace Switchboard.Domain.Economics;

/// <summary>What a rule's <c>days</c> names: a class of days or one weekday.</summary>
public enum DayClass
{
    /// <summary>Monday to Friday and not a holiday.</summary>
    Workday,

    /// <summary>Saturday and Sunday.</summary>
    Weekend,

    /// <summary>A date of the holiday list, whatever its weekday.</summary>
    Holiday,

    Monday,
    Tuesday,
    Wednesday,
    Thursday,
    Friday,
    Saturday,
    Sunday,
}

/// <summary>
/// One <c>when:</c> entry of a zone: days, months and hours must all match. An empty list means "all"
/// (every day, every month, the whole day). The date (day and month) is the local date of the instant,
/// also for the early-morning part of a range that wraps past midnight.
/// </summary>
public sealed record ZoneRule(ImmutableArray<DayClass> Days, ImmutableArray<int> Months, ImmutableArray<HoursRange> Hours)
{
    /// <summary>Whether the rule matches a local date and minute of the day. Months are 1 to 12.</summary>
    public bool Matches(DateOnly date, int minuteOfDay, HolidayCalendar holidays)
    {
        ArgumentNullException.ThrowIfNull(holidays);
        return MatchesDay(date, holidays) && MatchesMonth(date.Month) && MatchesMinute(minuteOfDay);
    }

    public bool MatchesMonth(int month) => Months.IsDefaultOrEmpty || Months.Contains(month);

    public bool MatchesMinute(int minuteOfDay) => Hours.IsDefaultOrEmpty || Hours.Any(h => h.Contains(minuteOfDay));

    public bool MatchesDay(DateOnly date, HolidayCalendar holidays)
    {
        ArgumentNullException.ThrowIfNull(holidays);
        if (Days.IsDefaultOrEmpty)
        {
            return true;
        }

        var holiday = holidays.IsHoliday(date);
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return Days.Any(d => d switch
        {
            DayClass.Workday => !weekend && !holiday,
            DayClass.Weekend => weekend,
            DayClass.Holiday => holiday,
            DayClass.Monday => date.DayOfWeek == DayOfWeek.Monday,
            DayClass.Tuesday => date.DayOfWeek == DayOfWeek.Tuesday,
            DayClass.Wednesday => date.DayOfWeek == DayOfWeek.Wednesday,
            DayClass.Thursday => date.DayOfWeek == DayOfWeek.Thursday,
            DayClass.Friday => date.DayOfWeek == DayOfWeek.Friday,
            DayClass.Saturday => date.DayOfWeek == DayOfWeek.Saturday,
            _ => date.DayOfWeek == DayOfWeek.Sunday,
        });
    }

    /// <summary>The wall-clock minutes (0 to 1440) at which this rule can start or stop matching on some day.</summary>
    internal IEnumerable<int> Boundaries() =>
        Hours.IsDefaultOrEmpty ? [] : Hours.SelectMany(h => new[] { h.StartMinute, h.EndMinute % HoursRange.MinutesPerDay });
}

/// <summary>A tariff zone: an id (the name of a price band, never a vendor), its price per kWh and the rules that put a moment in it.</summary>
public sealed record TariffZone(string Id, double Price, ImmutableArray<ZoneRule> Rules)
{
    public bool Matches(DateOnly date, int minuteOfDay, HolidayCalendar holidays) =>
        !Rules.IsDefaultOrEmpty && Rules.Any(r => r.Matches(date, minuteOfDay, holidays));
}
