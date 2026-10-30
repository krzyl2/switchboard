using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// The calendar attributes a calendar signal keeps (EV-02): the whitelist is <c>message</c>, <c>description</c>, <c>start_time</c>, <c>end_time</c> and
/// <c>all_day</c> (plus <c>time_zone</c> or <c>timezone</c> when the entity states one), nothing else of any attribute. A state diff carries only the
/// attributes that changed, so the fields are kept between events and merged, never re-read from one event alone.
/// <list type="bullet">
/// <item>Times are naive local time (<c>2026-10-25 02:30:00</c>) and are read in the add-on's zone with the same rule as a series key: a time that
/// does not exist moves one hour on, one that happens twice is the first. A time that carries an offset is taken as written. Never throws.</item>
/// <item>A description longer than <see cref="EntityUpdate.MaxCalendarDescription"/> refuses the whole event; its text is dropped as it is read, so a
/// huge attribute is never held. The message is display only and is cut at <see cref="EntityUpdate.MaxCalendarMessage"/>.</item>
/// <item><c>all_day</c> is a JSON boolean (Home Assistant's); the text <c>true</c> or <c>false</c> is accepted too. Any other type refuses the event.</item>
/// <item>A calendar without an event (no <c>message</c> and no <c>start_time</c>) is a valid answer: no event.</item>
/// </list>
/// </summary>
internal sealed record CalendarFields
{
    private static readonly string[] ZoneAttributes = ["time_zone", "timezone"];

    public static CalendarFields Empty { get; } = new();

    private ImmutableDictionary<string, Field> Fields { get; init; } = ImmutableDictionary<string, Field>.Empty;

    /// <summary>One attribute as read: its text or boolean, or the fact that it was of the wrong type or (a description) too long.</summary>
    private readonly record struct Field(string? Text, bool? Flag, bool WrongType, bool TooLong);

    /// <summary>The fields after an event's attributes: every whitelisted attribute that the event carries replaces the one held.</summary>
    public CalendarFields Apply(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return this;
        }

        var fields = Fields;
        foreach (var name in new[] { "message", "description", "start_time", "end_time", "all_day" }.Concat(ZoneAttributes))
        {
            if (attributes.TryGetProperty(name, out var value))
            {
                fields = fields.SetItem(name, Read(name, value));
            }
        }

        return this with { Fields = fields };
    }

    /// <summary>The fields after a state diff removed attributes: a removed attribute is absent again.</summary>
    public CalendarFields Remove(IEnumerable<string?> names) => this with { Fields = Fields.RemoveRange(names.OfType<string>()) };

    private static Field Read(string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        if (name == "all_day")
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => new Field(null, true, false, false),
                JsonValueKind.False => new Field(null, false, false, false),
                JsonValueKind.String when bool.TryParse(value.GetString(), out var flag) => new Field(null, flag, false, false),
                _ => new Field(null, null, true, false),
            };
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            return new Field(null, null, true, false);
        }

        return name switch
        {
            "description" when text.Length > EntityUpdate.MaxCalendarDescription => new Field(null, null, false, true),
            "description" => new Field(text, null, false, false),
            "message" => new Field(Cut(text, EntityUpdate.MaxCalendarMessage), null, false, false),
            "time_zone" or "timezone" => new Field(Cut(text, EntityUpdate.MaxCalendarZoneName), null, false, false),
            // start_time, end_time and anything else on the whitelist: a time is never longer than MaxAttributeText, so a longer text is no time and is not held.
            _ => text.Length > EntityUpdate.MaxAttributeText ? new Field(null, null, true, false) : new Field(text, null, false, false),
        };
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>The event the fields describe, or the reason there is none; <paramref name="localZone"/> null refuses a time without an offset.</summary>
    public ParsedCalendar Parse(TimeZoneInfo? localZone)
    {
        if (Fields.Values.Any(f => f.TooLong))
        {
            return ParsedCalendar.Of(CalendarProblem.DescriptionTooLong);
        }

        if (Fields.Values.Any(f => f.WrongType))
        {
            return ParsedCalendar.Of(CalendarProblem.Malformed);
        }

        var zone = ZoneAttributes.Select(Text).FirstOrDefault(t => t is { Length: > 0 });
        var message = Text("message");
        var start = Text("start_time");
        if (string.IsNullOrEmpty(start) && string.IsNullOrEmpty(message))
        {
            return new ParsedCalendar(null, CalendarProblem.None, zone);
        }

        var allDay = Fields.TryGetValue("all_day", out var flag) && flag.Flag == true;
        if (!TryTime(start, allDay, localZone, out var from))
        {
            return ParsedCalendar.Of(CalendarProblem.Malformed);
        }

        var end = Text("end_time");
        var to = from;
        if (!string.IsNullOrEmpty(end) && !TryTime(end, allDay, localZone, out to))
        {
            return ParsedCalendar.Of(CalendarProblem.Malformed);
        }

        return new ParsedCalendar(new CalendarEvent(message ?? string.Empty, Text("description") ?? string.Empty, from, to, allDay), CalendarProblem.None, zone);
    }

    private string? Text(string name) => Fields.TryGetValue(name, out var field) ? field.Text : null;

    /// <summary>An instant with an offset as written; a local date and time in the add-on's zone; an all-day event's date alone as local midnight.</summary>
    private static bool TryTime(string? text, bool allDay, TimeZoneInfo? zone, out DateTimeOffset instant)
    {
        if (IsoInstant.TryParse(text, zone, out instant))
        {
            return true;
        }

        return allDay && text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && IsoInstant.TryParse(text + " 00:00:00", zone, out instant);
    }
}
