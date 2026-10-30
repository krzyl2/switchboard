using Switchboard.Domain.Ev;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Domain;

/// <summary>
/// EV-03 (docs/EV.md section 2 question 2, B4, section 3 point 7): the calendar event becomes the one trip the plan is made for. Why it matters: a wrong
/// number is a wrong amount of energy bought at the dearest price or a car that is not ready; a trip kept after its start becomes a goal with a deadline in
/// the past, which the planner serves at full power in any zone; an ignored event must be visible, because the entity shows one event and an ignored one may
/// hide the trip behind it.
/// </summary>
public sealed class TripParserTests
{
    private const double MaxKm = 500;

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 6, 0, 0, TimeSpan.FromHours(1));

    private static CalendarEvent Event(string description, DateTimeOffset? start = null, bool allDay = false, string message = "Praca") =>
        new(message, description, start ?? Now.AddHours(3), (start ?? Now.AddHours(3)).AddHours(1), allDay);

    private static TripAssessment Assess(CalendarEvent? calendarEvent, DateTimeOffset? now = null) => TripParser.Assess(calendarEvent, now ?? Now, MaxKm);

    [Theory]
    [InlineData("km: 42", 42.0)]
    [InlineData("km: 42,5", 42.5)]
    [InlineData("km: 42.5", 42.5)]
    [InlineData("KM:42", 42.0)]
    [InlineData("Km :   7", 7.0)]
    [InlineData("Dojazd do klienta\nkm: 120\nparking przy wejsciu", 120.0)]
    [InlineData("<b>km: 18</b><br>uwagi", 18.0)]
    public void A_km_in_the_description_is_the_trip_distance(string description, double expected)
    {
        var trip = Assess(Event(description)).Next;

        Assert.Equal(expected, trip!.Km);
    }

    [Fact]
    public void The_first_km_wins_when_the_description_has_several()
    {
        // Why: one rule, no guessing; a description that mentions a second distance must not change the plan.
        Assert.Equal(30.0, Assess(Event("km: 30, powrot inna trasa km: 80")).Next!.Km);
    }

    [Theory]
    [InlineData("")]
    [InlineData("jade do klienta")]
    [InlineData("42 km")]
    [InlineData("tkm: 12")]
    [InlineData("km 42")]
    public void A_description_without_a_km_is_ignored_and_listed_as_unparsed(string description)
    {
        var assessment = Assess(Event(description));

        Assert.Null(assessment.Next);
        var unparsed = Assert.Single(assessment.Unparsed);
        Assert.Equal(UnparsedReason.NoKm, unparsed.Reason);
    }

    [Fact]
    public void An_all_day_event_is_ignored_even_when_its_description_has_a_km()
    {
        // Why: an all-day event has no departure time; planning a deadline at its midnight would be a wrong hour with a right-looking number.
        var assessment = Assess(Event("km: 40", allDay: true));

        Assert.Null(assessment.Next);
        Assert.Equal(UnparsedReason.AllDay, Assert.Single(assessment.Unparsed).Reason);
    }

    [Theory]
    [InlineData("km: 0")]
    [InlineData("km: 500.5")]
    [InlineData("km: 4200")]
    public void A_km_outside_the_bound_is_unparsed_with_its_reason_and_the_number(string description)
    {
        var assessment = Assess(Event(description));

        Assert.Null(assessment.Next);
        var unparsed = Assert.Single(assessment.Unparsed);
        Assert.Equal(UnparsedReason.KmOutOfBounds, unparsed.Reason);
        Assert.NotNull(unparsed.Km);
    }

    [Fact]
    public void The_bound_itself_is_a_valid_trip()
    {
        Assert.Equal(MaxKm, Assess(Event("km: 500")).Next!.Km);
    }

    [Fact]
    public void The_bound_is_the_parameter_not_a_literal()
    {
        // Why: ev.max_trip_km is configuration; the same event is a trip under one bound and a typo under another.
        var calendarEvent = Event("km: 120");

        Assert.NotNull(TripParser.Assess(calendarEvent, Now, 200).Next);
        Assert.Null(TripParser.Assess(calendarEvent, Now, 100).Next);
    }

    [Fact]
    public void An_ignored_event_still_ahead_warns_that_it_may_hide_the_next_trip()
    {
        // Why: the calendar entity shows one event. A no-km event in front means the trip behind it is invisible, and the user must be told.
        var assessment = Assess(Event("bez kilometrow", message: "Spotkanie"));

        var warning = Assert.Single(assessment.Warnings);
        Assert.Equal((TripWarningKind.NextTripMayBeHidden, "Spotkanie"), (warning.Kind, warning.Title));
    }

    [Fact]
    public void A_planned_trip_raises_no_warning_and_lists_nothing_as_unparsed()
    {
        var assessment = Assess(Event("km: 42"));

        Assert.Empty(assessment.Warnings);
        Assert.Empty(assessment.Unparsed);
    }

    [Fact]
    public void No_event_is_no_trip_and_no_warning()
    {
        Assert.Equal(TripAssessment.None, Assess(null));
    }

    [Fact]
    public void A_trip_is_removed_the_moment_its_start_passes_on_the_virtual_clock()
    {
        // Why: B4. A trip kept past its start is a goal with a deadline in the past: Missed, then Urgent in every zone, full power in the peak.
        var clock = new SteppableTime(Now);
        var start = Now.AddMinutes(90);
        var calendarEvent = Event("km: 42", start);

        Assert.NotNull(TripParser.Assess(calendarEvent, clock.GetUtcNow(), MaxKm).Next);

        clock.Advance(TimeSpan.FromMinutes(90) - TimeSpan.FromTicks(1));
        Assert.NotNull(TripParser.Assess(calendarEvent, clock.GetUtcNow(), MaxKm).Next);

        clock.Advance(TimeSpan.FromTicks(1));
        var atStart = TripParser.Assess(calendarEvent, clock.GetUtcNow(), MaxKm);
        Assert.Equal(TripAssessment.None, atStart);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(TripAssessment.None, TripParser.Assess(calendarEvent, clock.GetUtcNow(), MaxKm));
    }

    [Fact]
    public void A_trip_in_progress_when_the_add_on_starts_is_not_a_trip()
    {
        // Why: a restart in the middle of a drive reads the current event; it must not become a missed goal.
        var inProgress = Event("km: 42", Now.AddMinutes(-20));

        Assert.Equal(TripAssessment.None, Assess(inProgress));
    }

    [Fact]
    public void An_ignored_event_that_is_over_is_neither_listed_nor_warned_about()
    {
        // Why: it hides nothing any more, and listing a past event would show a stale problem.
        var over = new CalendarEvent("Urlop", "brak", Now.AddDays(-3), Now.AddMinutes(-5), false);

        Assert.Equal(TripAssessment.None, Assess(over));
    }

    [Theory]
    [InlineData(true, "urlop")]
    [InlineData(false, "dlugi dzien bez kilometrow")]
    [InlineData(false, "km: 1,200")]
    [InlineData(false, "km: 9000")]
    public void An_ignored_event_in_progress_warns_that_the_next_trip_may_be_hidden(bool allDay, string description)
    {
        // Why: EV.md section 3 point 7. The entity shows the current event until it ends; an all-day leave or a long event without km: that is running hides the
        // trip behind it, and the user must be told, exactly as for one that is still ahead.
        var running = new CalendarEvent("Urlop", description, Now.AddHours(-2), Now.AddHours(30), allDay);

        var assessment = Assess(running);

        Assert.Null(assessment.Next);
        Assert.Equal(TripWarningKind.NextTripMayBeHidden, Assert.Single(assessment.Warnings).Kind);
        Assert.Single(assessment.Unparsed);
    }

    [Fact]
    public void A_valid_trip_that_has_started_is_dropped_silently_even_though_it_has_not_ended()
    {
        // Why: B4. The drive in progress is not a trip and not a problem; only an ignored event hides something.
        var driving = new CalendarEvent("Praca", "km: 42", Now.AddMinutes(-10), Now.AddHours(1), false);

        Assert.Equal(TripAssessment.None, Assess(driving));
    }

    [Theory]
    [InlineData("okm: 40")]
    [InlineData("5km: 40")]
    public void A_km_glued_to_another_word_or_digit_is_no_hit(string description)
    {
        Assert.Equal(UnparsedReason.NoKm, Assert.Single(Assess(Event(description)).Unparsed).Reason);
    }

    [Theory]
    [InlineData("km: abc")]
    [InlineData("km: 1,200")]
    [InlineData("km: 1.500")]
    [InlineData("km: 1e3")]
    [InlineData("km: 2E+2")]
    [InlineData("km: 1 200")]
    [InlineData("km: 1 200")]
    [InlineData("km: 1,200,5")]
    [InlineData("km: 1.2.3")]
    [InlineData("km: 12345678901234567890")]
    [InlineData("km: -5")]
    public void An_unreadable_or_ambiguous_number_is_unparsed_with_its_reason_never_a_smaller_number(string description)
    {
        // Why: 1,200 read as 1.2 km would plan almost no energy for a 1200 km (or 1.2 thousand) claim; 1e3 as 1 likewise. The event is ignored and visible instead.
        var assessment = Assess(Event(description));

        Assert.Null(assessment.Next);
        Assert.Equal(UnparsedReason.KmUnreadable, Assert.Single(assessment.Unparsed).Reason);
        Assert.Null(TripParser.Km(description));
    }

    [Theory]
    [InlineData("km: abc, potem km: 30")]
    [InlineData("km: 1,200 albo km: 30")]
    public void The_first_km_decides_and_a_later_one_is_never_consulted(string description)
    {
        Assert.Equal(UnparsedReason.KmUnreadable, Assert.Single(Assess(Event(description)).Unparsed).Reason);
    }

    [Theory]
    [InlineData("km: 42 km dojazd", 42.0)]
    [InlineData("km: 0,125", 0.125)]
    [InlineData("km: 12,34", 12.34)]
    [InlineData("km: 1,5 i 20 minut", 1.5)]
    [InlineData("km: 42.", 42.0)]
    public void A_clear_number_is_still_read_when_text_follows(string description, double expected)
    {
        Assert.Equal(expected, Assess(Event(description)).Next!.Km);
        Assert.Equal(expected, TripParser.Km(description));
    }
}
