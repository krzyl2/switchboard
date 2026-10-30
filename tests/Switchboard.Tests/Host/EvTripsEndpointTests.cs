using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/ev, slice S2 (EV-01 to EV-03): besides the configuration it shows how each EV input was read, the next trip, the events the parser ignored and
/// the add-on zone. Why it matters: the user must see why a trip is not planned (no km, all day, a typo) and that a trip may be hidden behind an ignored event;
/// a calendar that states another zone than the add-on would start every trip at the wrong hour; and an answer built from entities must never leak one.
/// </summary>
public sealed class EvTripsEndpointTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 6, 0, 0, TimeSpan.FromHours(1));

    private const string Charger =
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n";

    private const string Ev =
        "planner:\n  dispatch: narrow\n\nev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level }\n  range: { entity: sensor.car_range }\n  plugged: { entity: sensor.car_state, states: [Charging] }\n  calendar: { entity: calendar.kursy }\n";

    private static ConfigurationResult Config(string extra = Ev) => TestConfigurations.Parse(TestDevices.Document(Charger, extra: extra));

    private static EvInputs Inputs(CalendarEvent? calendarEvent, string? zoneName = null, bool calendarFresh = true) =>
        new(
            new EvRead(true, SignalState.Fresh, SignalDetail.None, Now.AddMinutes(-5), TimeSpan.FromHours(1)),
            55,
            new EvRead(true, SignalState.Fresh, SignalDetail.None, Now.AddMinutes(-4), TimeSpan.FromHours(1)),
            49.9,
            new EvRead(true, SignalState.Fresh, SignalDetail.None, Now.AddDays(-2), TimeSpan.FromHours(1)),
            true,
            "Charging",
            new EvRead(true, calendarFresh ? SignalState.Fresh : SignalState.Stale, calendarFresh ? SignalDetail.None : SignalDetail.MaxAgeExceeded, Now.AddHours(-3), TimeSpan.FromHours(24)),
            calendarFresh ? calendarEvent : null,
            zoneName);

    private static CalendarEvent Departure(string description, bool allDay = false, double hoursAhead = 3, string message = "Praca") =>
        new(message, description, Now.AddHours(hoursAhead), Now.AddHours(hoursAhead + 1), allDay);

    [Fact]
    public void The_next_trip_the_zone_and_the_read_times_are_shown()
    {
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure("km: 42,5")), Warsaw);

        Assert.Equal("Europe/Warsaw", response.AddOnZone);
        var trip = response.Trips!.NextTrip!;
        Assert.Equal(("Praca", 42.5, Now.AddHours(3)), (trip.Title, trip.Km, trip.Start));
        Assert.Empty(response.Trips.Unparsed);
        Assert.Empty(response.Trips.Warnings);
        Assert.Equal((55.0, 49.9, true), (response.Inputs!.SocPercent, response.Inputs.RangeKm, response.Inputs.IsPlugged));
        Assert.Equal((300.0, "fresh"), (response.Inputs.Soc.AgeS, response.Inputs.Soc.State));
        Assert.Equal(172800.0, response.Inputs.Plugged.AgeS);
        Assert.Equal(86400.0, response.Inputs.Calendar.MaxAgeS);
    }

    [Fact]
    public void An_ignored_event_is_listed_with_its_reason_and_warns_that_a_trip_may_be_hidden()
    {
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure("urlop", allDay: true, message: "Urlop")), Warsaw);

        Assert.Null(response.Trips!.NextTrip);
        var unparsed = Assert.Single(response.Trips.Unparsed);
        Assert.Equal(("Urlop", "all_day"), (unparsed.Title, unparsed.Reason));
        Assert.Equal("next_trip_may_be_hidden", Assert.Single(response.Trips.Warnings).Kind);
    }

    [Theory]
    [InlineData("bez kilometrow", "no_km")]
    [InlineData("km: 9000", "km_out_of_bounds")]
    public void The_reason_codes_are_the_closed_snake_case_list(string description, string reason)
    {
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure(description)), Warsaw);

        Assert.Equal(reason, Assert.Single(response.Trips!.Unparsed).Reason);
    }

    [Fact]
    public void The_km_bound_is_the_control_parameter_in_force()
    {
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure("km: 120")), Warsaw);
        var tight = EvEndpoint.Build(
            TestConfigurations.Parse(TestDevices.Document(Charger, control: "  ev:\n    max_trip_km: 100\n", extra: Ev)),
            Now,
            Inputs(Departure("km: 120")),
            Warsaw);

        Assert.NotNull(response.Trips!.NextTrip);
        Assert.Equal("km_out_of_bounds", Assert.Single(tight.Trips!.Unparsed).Reason);
    }

    [Fact]
    public void A_trip_whose_start_has_passed_is_gone_from_the_answer()
    {
        // Why: B4 at the edge the user sees. The same input, a minute after the start, shows no trip and no unparsed event.
        var calendarEvent = Departure("km: 42");
        var after = EvEndpoint.Build(Config(), calendarEvent.Start, Inputs(calendarEvent), Warsaw);

        Assert.Null(after.Trips!.NextTrip);
        Assert.Empty(after.Trips.Unparsed);
    }

    [Fact]
    public void A_stale_calendar_shows_its_state_and_no_trip()
    {
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure("km: 42"), calendarFresh: false), Warsaw);

        Assert.Null(response.Trips!.NextTrip);
        Assert.Equal(("stale", "max_age_exceeded"), (response.Inputs!.Calendar.State, response.Inputs.Calendar.Detail));
    }

    [Fact]
    public void A_calendar_that_states_another_zone_raises_an_issue()
    {
        // Why: the entity's own zone attribute, when it exists, is the only evidence that the naive local times are not the add-on's. Mismatched, every trip starts
        // an hour or more off.
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure("km: 42"), zoneName: "America/New_York"), Warsaw);

        var issue = Assert.Single(response.Issues, i => i.Code == ConfigurationIssueCodes.EvCalendarZoneMismatch);
        Assert.Contains("America/New_York", issue.Message, StringComparison.Ordinal);
        Assert.Equal("America/New_York", response.Trips!.CalendarZone);
    }

    [Theory]
    [InlineData("Europe/Warsaw")]
    [InlineData("europe/warsaw")]
    [InlineData("Europe/Berlin")]
    [InlineData(null)]
    [InlineData("")]
    public void The_same_zone_a_same_offset_zone_and_no_zone_raise_no_issue(string? stated)
    {
        // Why: no attribute is the normal case (most calendars state none); another name for the same offset is not a disagreement.
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure("km: 42"), zoneName: stated), Warsaw);

        Assert.DoesNotContain(response.Issues, i => i.Code == ConfigurationIssueCodes.EvCalendarZoneMismatch);
    }

    [Fact]
    public void A_zone_name_this_host_does_not_know_is_a_disagreement()
    {
        Assert.True(EvEndpoint.ZoneDisagrees("Mars/Olympus_Mons", Warsaw, Now));
    }

    [Fact]
    public void The_config_echo_names_the_new_signals_by_logical_name_and_never_an_entity_id()
    {
        var response = EvEndpoint.Build(Config(), Now, Inputs(Departure("km: 42")), Warsaw);
        var json = JsonSerializer.Serialize(response);

        Assert.StartsWith("device_", response.Config!.RangeSignal, StringComparison.Ordinal);
        Assert.StartsWith("device_", response.Config.PluggedSignal, StringComparison.Ordinal);
        Assert.StartsWith("device_", response.Config.CalendarSignal, StringComparison.Ordinal);
        Assert.Equal(new[] { "Charging" }, response.Config.PluggedStates!.ToArray());
        Assert.DoesNotContain("calendar.kursy", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensor.", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_inputs_or_without_a_section_the_answer_has_no_trips()
    {
        // Why: the S1 contract holds: a file without ev: is "not enabled", and the answer built from the configuration alone shows no trips or inputs.
        Assert.Null(EvEndpoint.Build(Config(string.Empty), Now, Inputs(Departure("km: 42")), Warsaw).Trips);
        Assert.Null(EvEndpoint.Build(Config(), Now).Trips);
        Assert.Null(EvEndpoint.Build(Config(), Now).Inputs);
    }
}
