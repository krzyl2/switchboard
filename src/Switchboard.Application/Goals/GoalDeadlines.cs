using System.Globalization;
using System.Text.RegularExpressions;

namespace Switchboard.Application.Goals;

/// <summary>
/// Turns what a goal's deadline says into an instant on the local calendar. The zone is injected (never the machine's), so the
/// 23- and 25-hour days are exercised on a fixed zone: a local time that does not exist is moved to the end of the gap, a local
/// time that happens twice is the first one.
/// </summary>
public static partial class GoalDeadlines
{
    /// <summary>
    /// The next occurrence of a recurring local time strictly after <paramref name="now"/>. A recurring goal therefore rolls to the next
    /// day the moment its deadline passes: it is never "missed", the next cycle starts (the ledger starts it at zero).
    /// </summary>
    public static DateTimeOffset NextOccurrence(TimeOnly at, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var day = 0; day < 3; day++)
        {
            var candidate = ToInstant(date.AddDays(day).ToDateTime(at), zone);
            if (candidate > now)
            {
                return candidate;
            }
        }

        // Unreachable: three consecutive local days always contain an instant after now.
        return ToInstant(date.AddDays(3).ToDateTime(at), zone);
    }

    /// <summary>
    /// A deadline entity's state: an ISO instant with an offset (a timestamp sensor), a local date and time (an input_datetime with both),
    /// or a local time of day alone (an input_datetime with a time), which recurs like <c>at</c>. Anything else is not a deadline.
    /// </summary>
    public static DateTimeOffset? Parse(string state, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(zone);
        var text = state.Trim();
        if (WithOffset().IsMatch(text))
        {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ? instant : null;
        }

        if (LocalDateTime().IsMatch(text))
        {
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ? ToInstant(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone) : null;
        }

        if (TimeOfDay().IsMatch(text))
        {
            return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? NextOccurrence(time, now, zone) : null;
        }

        return null;
    }

    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // The hour that does not exist: the first valid moment after the gap is one hour on.
            local = local.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}[T ]\\d{2}:\\d{2}(:\\d{2}(\\.\\d{1,7})?)?(Z|[+-]\\d{2}:?\\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex WithOffset();

    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}[T ]\\d{2}:\\d{2}(:\\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalDateTime();

    [GeneratedRegex("^\\d{2}:\\d{2}(:\\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeOfDay();
}
