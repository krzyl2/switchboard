using System.Globalization;

namespace Switchboard.Domain.Economics;

/// <summary>
/// A span of the local wall clock in whole minutes: <c>"HH:MM-HH:MM"</c>, the end exclusive, <c>24:00</c>
/// allowed as an end. A start after the end wraps past midnight (<c>22:00-06:00</c> is the evening and the
/// early morning); the start may not equal the end (use <c>00:00-24:00</c> for a whole day).
/// </summary>
public readonly record struct HoursRange
{
    public const int MinutesPerDay = 24 * 60;

    public HoursRange(int startMinute, int endMinute)
    {
        if (startMinute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(startMinute), startMinute, "The start must be within the day (00:00 to 23:59).");
        }

        if (endMinute is <= 0 or > MinutesPerDay || endMinute == startMinute)
        {
            throw new ArgumentOutOfRangeException(nameof(endMinute), endMinute, "The end must be after 00:00, at most 24:00 and differ from the start.");
        }

        StartMinute = startMinute;
        EndMinute = endMinute;
    }

    public int StartMinute { get; }

    public int EndMinute { get; }

    /// <summary>True when the range runs past midnight.</summary>
    public bool Wraps => StartMinute > EndMinute;

    /// <summary>Whether the wall-clock minute of the day (0 to 1439) is inside the range.</summary>
    public bool Contains(int minuteOfDay) =>
        Wraps ? minuteOfDay >= StartMinute || minuteOfDay < EndMinute : minuteOfDay >= StartMinute && minuteOfDay < EndMinute;

    public static bool TryParse(string? text, out HoursRange range)
    {
        range = default;
        if (text is null)
        {
            return false;
        }

        var parts = text.Split('-');
        if (parts.Length != 2 || !TryClock(parts[0], out var start) || !TryClock(parts[1], out var end) || start >= MinutesPerDay || end == 0 || start == end)
        {
            return false;
        }

        range = new HoursRange(start, end);
        return true;
    }

    public override string ToString() => $"{Clock(StartMinute)}-{Clock(EndMinute)}";

    private static string Clock(int minute) => string.Create(CultureInfo.InvariantCulture, $"{minute / 60:00}:{minute % 60:00}");

    private static bool TryClock(string text, out int minutes)
    {
        minutes = 0;
        if (text.Length != 5 || text[2] != ':'
            || !int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mins)
            || mins > 59 || hours > 24 || (hours == 24 && mins != 0))
        {
            return false;
        }

        minutes = (hours * 60) + mins;
        return true;
    }
}
