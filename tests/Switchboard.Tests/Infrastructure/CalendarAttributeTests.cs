using System.Text.Json;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// EV-02: a calendar entity's event as the transport keeps it. The attributes are the whitelist message, description, start_time, end_time and all_day; Home
/// Assistant writes the times as naive local time ("2026-10-30 08:00:00"), all_day as a JSON boolean, and a state diff carries only the attributes that
/// changed. Why it matters: a time read as UTC or in the server's zone moves a departure by one or two hours; an hour that does not exist or happens twice
/// must neither throw nor leave a gap; a description cut at the 64 characters of an ordinary attribute could lose the km: that sets the energy bought.
/// </summary>
public sealed class CalendarAttributeTests
{
    private const string Calendar = "calendar.kursy";

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private const decimal Lc = 1792836054.020123m;

    private static SubscribeEntitiesParser Parser(TimeZoneInfo? zone = null) =>
        new([EntityId.Parse(Calendar)], localZone: zone ?? Warsaw, calendars: new HashSet<EntityId> { EntityId.Parse(Calendar) });

    private static EntityUpdateBatch Initial(SubscribeEntitiesParser parser, string attributes, string state = "off") =>
        Parse(parser, "{\"a\":{\"" + Calendar + "\":{\"s\":\"" + state + "\",\"a\":{" + attributes + "},\"c\":\"x\",\"lc\":" + Time(0) + "}}}");

    private static EntityUpdateBatch Diff(SubscribeEntitiesParser parser, string body) => Parse(parser, body);

    private static string Time(int plusSeconds) => (Lc + plusSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static EntityUpdateBatch Parse(SubscribeEntitiesParser parser, string json)
    {
        using var document = JsonDocument.Parse(json);
        var batch = parser.Parse(document.RootElement.Clone(), out var problem);
        Assert.Null(problem);
        return batch!;
    }

    private static ParsedCalendar Calendar_(EntityUpdateBatch batch) => batch.Updates.Single().Calendar!;

    private const string Full = """
        "message":"Praca","description":"km: 42","start_time":"2026-10-30 08:00:00","end_time":"2026-10-30 09:00:00","all_day":false,"location":"Gdansk","friendly_name":"Kursy"
        """;

    [Fact]
    public void The_five_attributes_make_an_event_with_instants_in_the_add_on_zone()
    {
        var parsed = Calendar_(Initial(Parser(), Full));

        Assert.Equal(CalendarProblem.None, parsed.Problem);
        var calendarEvent = parsed.Event!;
        Assert.Equal(("Praca", "km: 42", false), (calendarEvent.Message, calendarEvent.Description, calendarEvent.AllDay));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.FromHours(1)), calendarEvent.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.FromHours(1)), calendarEvent.End);
    }

    [Fact]
    public void Another_zone_gives_another_instant_for_the_same_text()
    {
        // Why: the zone is the add-on's, never the machine's; the same wall-clock text is a different instant elsewhere.
        var utc = Calendar_(Initial(Parser(TimeZoneInfo.Utc), Full)).Event!;

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), utc.Start);
    }

    [Fact]
    public void A_time_with_its_own_offset_is_taken_as_written()
    {
        var calendarEvent = Calendar_(Initial(Parser(), """
            "message":"x","description":"km: 5","start_time":"2026-10-30T08:00:00+02:00","end_time":"2026-10-30T09:00:00+02:00","all_day":false
            """)).Event!;

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.FromHours(2)), calendarEvent.Start);
    }

    [Fact]
    public void A_local_time_in_the_spring_gap_moves_one_hour_on_and_does_not_throw()
    {
        // Why: 2026-03-29 02:30 does not exist in Europe/Warsaw (the clocks jump 02:00 to 03:00). The event must land on the first valid moment after the gap.
        var calendarEvent = Calendar_(Initial(Parser(), """
            "message":"Wczesny","description":"km: 30","start_time":"2026-03-29 02:30:00","end_time":"2026-03-29 03:30:00","all_day":false
            """)).Event!;

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)), calendarEvent.Start);
        Assert.Equal(TimeSpan.FromHours(2), calendarEvent.Start.Offset);
    }

    [Fact]
    public void A_local_time_in_the_repeated_autumn_hour_is_the_first_occurrence()
    {
        // Why: 2026-10-25 02:30 happens twice (+02:00, then +01:00). The earlier one is chosen: a car that must be ready for the first 02:30 must not wait for the second.
        var calendarEvent = Calendar_(Initial(Parser(), """
            "message":"Nocny","description":"km: 30","start_time":"2026-10-25 02:30:00","end_time":"2026-10-25 04:00:00","all_day":false
            """)).Event!;

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), calendarEvent.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 4, 0, 0, TimeSpan.FromHours(1)), calendarEvent.End);
        Assert.Equal(TimeSpan.FromHours(2.5), calendarEvent.End - calendarEvent.Start);
    }

    [Fact]
    public void The_day_the_clocks_change_has_23_and_25_hours_in_the_instants()
    {
        // Why: end of the local day minus its start is the day's real length; a wall-clock subtraction would be wrong by an hour twice a year.
        TimeSpan Length(string day) =>
            Calendar_(Initial(Parser(), $$"""
                "message":"d","description":"km: 1","start_time":"{{day}} 00:00:00","end_time":"{{NextDay(day)}} 00:00:00","all_day":false
                """)).Event is { } e ? e.End - e.Start : default;

        Assert.Equal(TimeSpan.FromHours(23), Length("2026-03-29"));
        Assert.Equal(TimeSpan.FromHours(25), Length("2026-10-25"));
    }

    private static string NextDay(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd").AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_boolean_all_day_is_read_and_a_date_only_time_of_an_all_day_event_is_local_midnight()
    {
        // Why: all_day is a JSON boolean, which the generic text attributes read as empty; reading it as "not all day" would plan a deadline at midnight.
        var calendarEvent = Calendar_(Initial(Parser(), """
            "message":"Urlop","description":"km: 40","start_time":"2026-10-31","end_time":"2026-11-01","all_day":true
            """)).Event!;

        Assert.True(calendarEvent.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.FromHours(1)), calendarEvent.Start);
    }

    [Fact]
    public void A_date_only_time_without_all_day_is_malformed()
    {
        Assert.Equal(CalendarProblem.Malformed, Calendar_(Initial(Parser(), """
            "message":"x","description":"km: 1","start_time":"2026-10-31","end_time":"2026-11-01","all_day":false
            """)).Problem);
    }

    [Theory]
    [InlineData("\"all_day\":\"maybe\"")]
    [InlineData("\"all_day\":1")]
    [InlineData("\"start_time\":42")]
    [InlineData("\"start_time\":\"not a time\"")]
    [InlineData("\"end_time\":\"2026-13-40 99:00:00\"")]
    [InlineData("\"description\":42")]
    [InlineData("\"message\":[\"x\"]")]
    public void An_attribute_of_the_wrong_shape_refuses_the_whole_event(string broken)
    {
        // Why: a half-read event could plan a trip with a wrong hour; refuse it whole and say why.
        var baseline = Full.Replace("\"location\":\"Gdansk\",", string.Empty, StringComparison.Ordinal);
        var key = broken[..broken.IndexOf(':', StringComparison.Ordinal)];
        var attributes = $"{{{baseline}}}";
        using var document = JsonDocument.Parse(attributes);
        var kept = document.RootElement.EnumerateObject().Where(p => $"\"{p.Name}\"" != key).Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}");

        var parsed = Calendar_(Initial(Parser(), string.Join(",", kept.Append(broken))));

        Assert.Equal(CalendarProblem.Malformed, parsed.Problem);
        Assert.Null(parsed.Event);
    }

    [Fact]
    public void A_description_beyond_the_bound_refuses_the_whole_event()
    {
        // Why: the generic attribute limit is 64 characters, too short for a description; this one has its own bound, and an event over it is refused whole,
        // never truncated, because a cut description could lose the km: or cut its number.
        var tooLong = new string('a', EntityUpdate.MaxCalendarDescription + 1) + " km: 42";
        var parsed = Calendar_(Initial(Parser(), $$"""
            "message":"x","description":"{{tooLong}}","start_time":"2026-10-30 08:00:00","end_time":"2026-10-30 09:00:00","all_day":false
            """));

        Assert.Equal(CalendarProblem.DescriptionTooLong, parsed.Problem);
        Assert.Null(parsed.Event);
    }

    [Fact]
    public void A_description_at_the_bound_is_kept_whole_and_longer_than_the_generic_limit()
    {
        var text = new string('a', EntityUpdate.MaxCalendarDescription - 8) + " km: 42";
        var calendarEvent = Calendar_(Initial(Parser(), $$"""
            "message":"x","description":"{{text}}","start_time":"2026-10-30 08:00:00","end_time":"2026-10-30 09:00:00","all_day":false
            """)).Event!;

        Assert.True(calendarEvent.Description.Length > EntityUpdate.MaxAttributeText);
        Assert.Equal(text, calendarEvent.Description);
    }

    [Fact]
    public void A_message_beyond_the_bound_is_cut_because_nothing_is_decided_by_it()
    {
        var calendarEvent = Calendar_(Initial(Parser(), $$"""
            "message":"{{new string('m', EntityUpdate.MaxCalendarMessage + 50)}}","description":"km: 4","start_time":"2026-10-30 08:00:00","end_time":"2026-10-30 09:00:00","all_day":false
            """)).Event!;

        Assert.Equal(EntityUpdate.MaxCalendarMessage, calendarEvent.Message.Length);
    }

    [Fact]
    public void A_calendar_without_an_event_is_no_event_not_an_error()
    {
        // Why: HA writes no event attributes while the calendar is empty; that is a valid answer ("no trip"), not a malformed one.
        var parsed = Calendar_(Initial(Parser(), "\"friendly_name\":\"Kursy\""));

        Assert.Equal(CalendarProblem.None, parsed.Problem);
        Assert.Null(parsed.Event);
    }

    [Fact]
    public void A_null_description_is_an_empty_one()
    {
        var calendarEvent = Calendar_(Initial(Parser(), """
            "message":"x","description":null,"start_time":"2026-10-30 08:00:00","end_time":"2026-10-30 09:00:00","all_day":false
            """)).Event!;

        Assert.Equal(string.Empty, calendarEvent.Description);
    }

    [Fact]
    public void A_diff_that_changes_one_attribute_keeps_the_others()
    {
        // Why: subscribe_entities diffs carry only what changed. Reading the event from the diff alone would lose the start time and refuse a good event.
        var parser = Parser();
        Initial(parser, Full);

        var batch = Diff(parser, "{\"c\":{\"" + Calendar + "\":{\"+\":{\"a\":{\"description\":\"km: 77\"},\"lc\":" + Time(60) + "}}}}");

        var calendarEvent = Calendar_(batch).Event!;
        Assert.Equal(("km: 77", "Praca"), (calendarEvent.Description, calendarEvent.Message));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.FromHours(1)), calendarEvent.Start);
    }

    [Fact]
    public void A_diff_can_repair_an_oversized_description_and_a_removal_empties_the_event()
    {
        var parser = Parser();
        var tooLong = new string('a', EntityUpdate.MaxCalendarDescription + 1);
        Initial(parser, Full.Replace("km: 42", tooLong, StringComparison.Ordinal));

        var repaired = Diff(parser, "{\"c\":{\"" + Calendar + "\":{\"+\":{\"a\":{\"description\":\"km: 5\"},\"lc\":" + Time(60) + "}}}}");
        Assert.Equal("km: 5", Calendar_(repaired).Event!.Description);

        var removed = Diff(parser, "{\"c\":{\"" + Calendar + "\":{\"-\":{\"a\":[\"message\",\"description\",\"start_time\",\"end_time\",\"all_day\"]},\"+\":{\"s\":\"off\",\"lc\":" + Time(120) + "}}}}");
        Assert.Equal(CalendarProblem.None, Calendar_(removed).Problem);
        Assert.Null(Calendar_(removed).Event);
    }

    [Fact]
    public void The_zone_the_entity_states_for_its_times_is_kept_when_it_states_one()
    {
        var parsed = Calendar_(Initial(Parser(), Full + ",\"time_zone\":\"America/New_York\""));

        Assert.Equal("America/New_York", parsed.ZoneName);
    }

    [Fact]
    public void No_zone_attribute_means_no_stated_zone()
    {
        Assert.Null(Calendar_(Initial(Parser(), Full)).ZoneName);
    }

    [Fact]
    public void Attributes_outside_the_whitelist_are_never_kept()
    {
        // Why: a bounded whitelist is the contract; a location or an organizer must not travel with the signal.
        var parsed = Calendar_(Initial(Parser(), Full + ",\"organizer\":\"x@y.z\""));

        Assert.DoesNotContain("x@y.z", parsed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Gdansk", parsed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_without_a_calendar_signal_carries_no_calendar()
    {
        var parser = new SubscribeEntitiesParser([EntityId.Parse(Calendar)], localZone: Warsaw);

        Assert.Null(Calendar_Or_Null(Initial(parser, Full)));
    }

    private static ParsedCalendar? Calendar_Or_Null(EntityUpdateBatch batch) => batch.Updates.Single().Calendar;

    [Fact]
    public void Without_a_zone_a_naive_time_is_refused_not_read_in_the_machine_zone()
    {
        var parser = new SubscribeEntitiesParser([EntityId.Parse(Calendar)], localZone: null, calendars: new HashSet<EntityId> { EntityId.Parse(Calendar) });

        Assert.Equal(CalendarProblem.Malformed, Calendar_(Initial(parser, Full)).Problem);
    }

    [Theory]
    [InlineData("0001-01-01 00:00:00")]
    [InlineData("0001-01-01 00:30:00")]
    [InlineData("9999-12-31 23:59:59")]
    [InlineData("9999-12-31 00:00:00")]
    public void A_time_at_the_edge_of_the_representable_range_is_malformed_and_never_throws(string edge)
    {
        // Why: in Europe/Warsaw (positive offset) 0001-01-01 00:00 local is before the first representable instant; the exception used to leave the parser and
        // end the Home Assistant session, so one bad event meant a reconnect loop. The edges are refused whole, so a later "start minus margin" cannot overflow either.
        var parsed = Calendar_(Initial(Parser(), $"\"message\":\"x\",\"description\":\"km: 5\",\"start_time\":\"{edge}\",\"end_time\":\"2026-10-30 09:00:00\",\"all_day\":false"));

        Assert.Equal(CalendarProblem.Malformed, parsed.Problem);
    }

    [Theory]
    [InlineData("0001-01-01 00:00:00")]
    [InlineData("9999-12-31 23:59:59")]
    [InlineData("0001-01-01T00:00:00+05:00")]
    [InlineData("9999-12-31T23:59:59-05:00")]
    public void Iso_instant_refuses_the_unrepresentable_edges_with_or_without_a_zone(string edge)
    {
        Assert.False(IsoInstant.TryParse(edge, Warsaw, out _));
        Assert.False(IsoInstant.TryParse(edge, TimeZoneInfo.Utc, out _));
    }

    [Fact]
    public void An_ordinary_time_far_from_the_edges_is_still_read()
    {
        Assert.True(IsoInstant.TryParse("2099-01-01 00:00:00", Warsaw, out var instant));
        Assert.Equal(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)), instant);
    }

    [Fact]
    public void A_time_text_longer_than_the_attribute_bound_refuses_the_event_and_is_not_held()
    {
        var parsed = Calendar_(Initial(Parser(), $"\"message\":\"x\",\"description\":\"km: 5\",\"start_time\":\"{new string('1', EntityUpdate.MaxAttributeText + 1)}\",\"end_time\":\"2026-10-30 09:00:00\",\"all_day\":false"));

        Assert.Equal(CalendarProblem.Malformed, parsed.Problem);
    }
}
