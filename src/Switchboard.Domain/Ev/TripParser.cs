using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Ev;

/// <summary>The next trip the calendar names: when it starts and how far it goes.</summary>
/// <param name="Title">The event's message, for display only.</param>
/// <param name="Start">The instant the trip starts (the deadline of the trip's goal); the car is ready before it by the configured margin, which is the goal's business.</param>
/// <param name="Km">The distance written as <c>km: 42</c> in the description, within the configured bound.</param>
public sealed record Trip(string Title, DateTimeOffset Start, double Km);

public enum UnparsedReason
{
    /// <summary>An all-day event is no departure time: it is ignored even when its description has a <c>km:</c>.</summary>
    AllDay,

    /// <summary>The description has no <c>km:</c>.</summary>
    NoKm,

    /// <summary>The <c>km:</c> is zero or above <c>ev.max_trip_km</c> (a typo such as 420 for 42).</summary>
    KmOutOfBounds,

    /// <summary>The first <c>km:</c> is not followed by one clear number (abc, 1,200, 1e3, 1 200, a very long digit string): ignored, never read as a smaller number.</summary>
    KmUnreadable,
}

/// <summary>An event the parser ignored, listed so the user can see why a trip is not planned.</summary>
public sealed record UnparsedEvent(string Title, DateTimeOffset Start, UnparsedReason Reason, double? Km);

public enum TripWarningKind
{
    /// <summary>
    /// The calendar entity shows only the current or next event. This one was ignored and is still ahead, so a trip behind it, which the entity does not
    /// show yet, is hidden from the plan.
    /// </summary>
    NextTripMayBeHidden,
}

public sealed record TripWarning(TripWarningKind Kind, string Title, DateTimeOffset Start);

/// <summary>What the calendar's event means for the plan: the next trip if there is one, what was ignored, and what that may hide.</summary>
public sealed record TripAssessment(Trip? Next, ImmutableArray<UnparsedEvent> Unparsed, ImmutableArray<TripWarning> Warnings)
{
    public static TripAssessment None { get; } = new(null, [], []);
}

/// <summary>
/// Reads the calendar's current or next event as a trip (EV-03, docs/EV.md section 2 question 2, B4 and section 3 point 7). Pure: the time is an
/// argument, the entity is not known.
/// <list type="bullet">
/// <item>The rule for the distance: the FIRST <c>km:</c> in the description decides (case-insensitive, <c>KM:42</c>; a word boundary before <c>km</c>, so
/// <c>okm:</c> and <c>5km:</c> are no hit). Its number is one clear number with a comma or a dot as the decimal sign (<c>42,5</c>). If what follows is not
/// that (no number, <c>1,200</c> or <c>1.500</c> which could be a thousands separator, <c>1e3</c>, <c>1 200</c>, two separators, a digit string longer than
/// 12 characters) the event is unparsed as <see cref="UnparsedReason.KmUnreadable"/>; a later <c>km:</c> is never consulted and a number is never read as a
/// smaller one than written.</item>
/// <item>An all-day event, one without <c>km:</c>, an unreadable one and one beyond the bound are ignored and listed as unparsed.</item>
/// <item>A valid trip disappears the moment its start passes (B4): a deadline in the past would turn the goal into a missed, urgent one that charges at full
/// power in any zone. Nothing is listed for it.</item>
/// <item>The entity exposes only the current or next event, so only the next trip is ever planned. An ignored event that is still ahead, or that has started
/// and has not ended (an all-day leave, a long event without <c>km:</c>), may hide the trip behind it, and that is a warning.</item>
/// </list>
/// </summary>
public static partial class TripParser
{
    /// <summary>The longest number text accepted; anything longer is not a distance anyone wrote.</summary>
    private const int MaxNumberText = 12;

    /// <param name="calendarEvent">The calendar's current or next event; null when it has none.</param>
    /// <param name="now">The instant to judge against; the trip is removed once <paramref name="now"/> reaches its start.</param>
    /// <param name="maxTripKm">A <c>km:</c> above this is a typo (<c>ev.max_trip_km</c>).</param>
    public static TripAssessment Assess(CalendarEvent? calendarEvent, DateTimeOffset now, double maxTripKm)
    {
        if (calendarEvent is null)
        {
            return TripAssessment.None;
        }

        var started = calendarEvent.Start <= now;
        TripAssessment ignored;
        if (calendarEvent.AllDay)
        {
            ignored = Ignored(calendarEvent, UnparsedReason.AllDay, null);
        }
        else
        {
            var (read, km) = ReadKm(calendarEvent.Description);
            if (read == KmRead.Absent)
            {
                ignored = Ignored(calendarEvent, UnparsedReason.NoKm, null);
            }
            else if (read == KmRead.Unreadable)
            {
                ignored = Ignored(calendarEvent, UnparsedReason.KmUnreadable, null);
            }
            else if (!(km > 0 && km <= maxTripKm))
            {
                ignored = Ignored(calendarEvent, UnparsedReason.KmOutOfBounds, km);
            }
            else
            {
                return started ? TripAssessment.None : new TripAssessment(new Trip(calendarEvent.Message, calendarEvent.Start, km), [], []);
            }
        }

        // An event that is over hides nothing; one that is running or ahead may hide the trip the entity does not show yet.
        return started && calendarEvent.End <= now ? TripAssessment.None : ignored;
    }

    /// <summary>The distance of a description by the rule above, or null when there is none or it is unreadable.</summary>
    public static double? Km(string? description) => ReadKm(description) is (KmRead.Found, var km) ? km : null;

    private enum KmRead
    {
        Absent,
        Found,
        Unreadable,
    }

    private static (KmRead Read, double Km) ReadKm(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return (KmRead.Absent, 0);
        }

        var hit = KmPattern().Match(description);
        if (!hit.Success)
        {
            return (KmRead.Absent, 0);
        }

        var start = hit.Index + hit.Length;
        var number = NumberPattern().Match(description, start);
        if (!number.Success || number.Length > MaxNumberText)
        {
            return (KmRead.Unreadable, 0);
        }

        var after = description[(number.Index + number.Length)..];
        if (ThousandsLike().IsMatch(number.Value) || ExponentFollows().IsMatch(after) || GroupFollows().IsMatch(after) || SeparatorFollows().IsMatch(after))
        {
            return (KmRead.Unreadable, 0);
        }

        return double.TryParse(number.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var km) && double.IsFinite(km)
            ? (KmRead.Found, km)
            : (KmRead.Unreadable, 0);
    }

    private static TripAssessment Ignored(CalendarEvent calendarEvent, UnparsedReason reason, double? km) =>
        new(
            null,
            [new UnparsedEvent(calendarEvent.Message, calendarEvent.Start, reason, km)],
            [new TripWarning(TripWarningKind.NextTripMayBeHidden, calendarEvent.Message, calendarEvent.Start)]);

    // The first "km:" only; a word boundary before "km" so that "okm:" is not a hit, and "5km:" is not either (a digit and a letter are both word characters).
    [GeneratedRegex(@"\bkm\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KmPattern();

    // One number right after the colon: digits, optionally one decimal sign and digits.
    [GeneratedRegex(@"\G[0-9]+(?:[.,][0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    // 1,200 or 1.500: one to three digits, a separator, exactly three digits: a thousands separator is as likely as a decimal sign.
    [GeneratedRegex(@"^[1-9][0-9]{0,2}[.,][0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThousandsLike();

    // 1e3 / 1E+3.
    [GeneratedRegex(@"^[eE][+-]?[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex ExponentFollows();

    // 1 200 (a space or no-break space as a group separator).
    [GeneratedRegex(@"^[   ][0-9]{3}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex GroupFollows();

    // 1,200,5 or 1.2.3: a second separator glued to the number.
    [GeneratedRegex(@"^[.,][0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorFollows();
}
