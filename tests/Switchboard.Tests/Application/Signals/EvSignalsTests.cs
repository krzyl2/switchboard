using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// EV-02 and the EV inputs view (docs/EV.md B7): the car's SoC, electric range, plugged state and calendar event as signals of the add-on, read only by the
/// EV module. Why it matters: a range in miles read as km overstates what the battery can do by 60 %; a plugged state that is "unavailable" read as "not
/// plugged" lets the module conclude something from a lost link; a calendar that reports once a day must not look stale to a trip that is two hours away
/// unless its declared age says so; and none of it may widen what the goal model, the guard or dispatch see.
/// </summary>
public sealed class EvSignalsTests
{
    private const string Charger =
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    sources: pv_and_zone:offpeak\n";

    private const string Tariff = """
        economics:
          currency: PLN
          tariff:
            default_zone: offpeak
            zones:
              - id: peak
                price: 1.60
                when:
                  - { days: [workday], hours: ["07:00-13:00"] }
              - id: offpeak
                price: 0.60
                when: []
          settlement:
            model: hourly_netting
            export_price: 0.35

        """;

    private const string EvBlock = """
        ev:
          device: ev_charger
          capacity_kwh: 18
          soc: { entity: sensor.car_battery_level, max_age: 30m }
          range: { entity: sensor.car_electric_range, unit: mi, max_age: 1h }
          plugged: { entity: sensor.charger_car_state, states: [Charging, "Wait for car", complete] }
          calendar: { entity: calendar.kursy, max_age: 24h }

        """;

    private static string Document(string ev = EvBlock) =>
        TestDevices.Document(Charger, extra: Tariff + "\nplanner:\n  dispatch: off\n\n" + ev, defaultStart: "headroom");

    private static StoreHarness Store(string ev = EvBlock) => new(Document(ev));

    private static EvDefinition Ev(StoreHarness store) => store.Configuration.Configuration!.Ev!;

    private static CalendarEvent Departure(StoreHarness store, string description = "km: 42") =>
        new("Praca", description, store.Now.AddHours(3), store.Now.AddHours(4), false);

    private static void PushCalendar(StoreHarness store, CalendarEvent? calendarEvent, DateTimeOffset? at = null, string state = "on", ParsedCalendar? parsed = null) =>
        store.Store.Apply(
            EntityUpdateBatch.Of(
                BatchOrigin.Change,
                new EntityUpdate(EntityId.Parse("calendar.kursy"), state, null, at ?? store.Now, at ?? store.Now) { Calendar = parsed ?? new ParsedCalendar(calendarEvent, CalendarProblem.None) }),
            store.Now);

    private static void PushAll(StoreHarness store)
    {
        store.Push("sensor.car_battery_level", "55", unit: "%");
        store.Push("sensor.car_electric_range", "31", unit: "mi");
        store.Push("sensor.charger_car_state", "Charging", unit: null);
        PushCalendar(store, Departure(store));
    }

    [Fact]
    public void The_four_signals_are_created_with_the_ev_roles_and_kinds_and_the_plugged_state_reports_on_change()
    {
        var signals = Store().Configuration.Configuration!.Signals;

        Assert.Equal(SignalRole.EvSoc, signals.Single(s => s.Entity.Value == "sensor.car_battery_level").Role);
        var range = signals.Single(s => s.Entity.Value == "sensor.car_electric_range");
        Assert.Equal((SignalKind.Distance, SignalRole.EvRange, "mi"), (range.Kind, range.Role, range.Unit));
        var plugged = signals.Single(s => s.Entity.Value == "sensor.charger_car_state");
        Assert.Equal((SignalKind.State, SignalRole.EvPlugged, StateFreshness.OnChange), (plugged.Kind, plugged.Role, plugged.Freshness));
        var calendar = signals.Single(s => s.Entity.Value == "calendar.kursy");
        Assert.Equal((SignalKind.CalendarEvent, SignalRole.EvCalendar, StateFreshness.Age), (calendar.Kind, calendar.Role, calendar.Freshness));
    }

    [Fact]
    public void A_range_in_miles_is_converted_to_km_in_the_signal_layer_and_nowhere_else()
    {
        var store = Store();
        store.Push("sensor.car_electric_range", "31", unit: "mi");

        var reading = store.Reading(Ev(store).Range!.Value.Value);

        Assert.Equal(SignalState.Fresh, reading.State);
        Assert.Equal(31 * 1.609344, ((DistanceValue)reading.Value!).Kilometers, 9);
    }

    [Fact]
    public void A_range_whose_unit_is_not_the_declared_one_is_refused()
    {
        // Why: km read as mi (or the reverse) is a 60 % error in the energy the trip needs; the unit must match the declaration exactly.
        var store = Store();
        store.Push("sensor.car_electric_range", "31", unit: "km");

        var reading = store.Reading(Ev(store).Range!.Value.Value);

        Assert.Equal((SignalState.Rejected, SignalDetail.UnitMismatch), (reading.State, reading.Detail));
    }

    [Fact]
    public void A_negative_range_is_refused()
    {
        var store = Store();
        store.Push("sensor.car_electric_range", "-4", unit: "mi");

        Assert.Equal((SignalState.Rejected, SignalDetail.SignViolation), (store.Reading(Ev(store).Range!.Value.Value).State, store.Reading(Ev(store).Range!.Value.Value).Detail));
    }

    [Fact]
    public void A_zero_range_is_a_valid_reading_and_it_is_the_consumers_business_what_it_means()
    {
        // Why: a PHEV reports 0 with a depleted buffer. The signal must not hide it as missing; S3 rejects it as a learning sample.
        var store = Store();
        store.Push("sensor.car_electric_range", "0", unit: "mi");

        Assert.Equal(0.0, ((DistanceValue)store.Reading(Ev(store).Range!.Value.Value).Value!).Kilometers);
    }

    [Fact]
    public void A_distance_signal_declares_only_km_or_mi()
    {
        Assert.Equal(new[] { "km", "mi" }, SignalUnits.AllowedFor(SignalKind.Distance).ToArray());
    }

    [Fact]
    public void A_calendar_event_is_accepted_with_its_instants_and_a_calendar_without_one_is_a_valid_empty_answer()
    {
        var store = Store();
        var name = Ev(store).Calendar!.Value.Value;

        PushCalendar(store, Departure(store));
        Assert.Equal(Departure(store), ((CalendarEventValue)store.Reading(name).Value!).Event);

        store.Advance(TimeSpan.FromMinutes(5));
        PushCalendar(store, null, state: "off");
        Assert.Equal(SignalState.Fresh, store.Reading(name).State);
        Assert.Null(((CalendarEventValue)store.Reading(name).Value!).Event);
    }

    [Fact]
    public void An_oversized_description_makes_the_calendar_signal_rejected_with_its_own_detail()
    {
        // Why: the whole event is refused, and the diagnostics say why, instead of a stale or silently shortened trip.
        var store = Store();
        var name = Ev(store).Calendar!.Value.Value;

        PushCalendar(store, null, parsed: ParsedCalendar.Of(CalendarProblem.DescriptionTooLong));

        var reading = store.Reading(name);
        Assert.Equal((SignalState.Rejected, SignalDetail.CalendarDescriptionTooLong), (reading.State, reading.Detail));
        Assert.Null(((CalendarEventValue?)reading.Value));
    }

    [Fact]
    public void A_malformed_calendar_is_rejected_and_a_state_that_is_unavailable_is_unavailable()
    {
        var store = Store();
        var name = Ev(store).Calendar!.Value.Value;

        PushCalendar(store, null, parsed: ParsedCalendar.Of(CalendarProblem.Malformed));
        Assert.Equal((SignalState.Rejected, SignalDetail.CalendarMalformed), (store.Reading(name).State, store.Reading(name).Detail));

        store.Advance(TimeSpan.FromSeconds(1));
        PushCalendar(store, null, state: "unavailable", parsed: ParsedCalendar.Of(CalendarProblem.None));
        Assert.Equal(SignalState.Unavailable, store.Reading(name).State);
    }

    [Fact]
    public void A_probe_answer_confirms_the_calendar_event_and_never_replaces_it()
    {
        // Why: a probe carries no attributes. Reading it as "no event" would erase a trip every time freshness is checked.
        var store = Store();
        var name = Ev(store).Calendar!.Value.Value;
        PushCalendar(store, Departure(store));
        var updated = store.Now;

        store.Advance(TimeSpan.FromHours(1));
        store.ProbeAnswer("calendar.kursy", "on", updated, store.Now, unit: null);

        var reading = store.Reading(name);
        Assert.Equal(SignalState.Fresh, reading.State);
        Assert.NotNull(((CalendarEventValue)reading.Value!).Event);
        Assert.Equal(store.Now, reading.LastReported);
    }

    [Fact]
    public void A_calendar_is_stale_after_its_declared_max_age_from_last_reported()
    {
        var store = Store();
        var name = Ev(store).Calendar!.Value.Value;
        PushCalendar(store, Departure(store));

        store.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(SignalState.Fresh, store.Reading(name).State);

        store.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((SignalState.Stale, SignalDetail.MaxAgeExceeded), (store.Reading(name).State, store.Reading(name).Detail));
    }

    [Fact]
    public void The_plugged_state_is_fresh_for_as_long_as_it_is_available_because_it_reports_on_change()
    {
        // Why: a car that has been plugged in for a week reports nothing for a week. Judging it by age would call a plugged car unknown every night.
        var store = Store();
        var name = Ev(store).Plugged!.Value.Value;
        store.Push("sensor.charger_car_state", "Wait for car", unit: null);

        store.Advance(TimeSpan.FromDays(30));

        Assert.Equal(SignalState.Fresh, store.Reading(name).State);
    }

    [Fact]
    public void The_plugged_state_map_is_the_configured_list_without_regard_to_case()
    {
        var store = Store();
        PushAll(store);
        store.Advance(TimeSpan.FromSeconds(1));
        store.Push("sensor.charger_car_state", "COMPLETE", unit: null);

        Assert.True(EvInputs.From(store.Snapshot(), Ev(store)).IsPlugged);

        store.Advance(TimeSpan.FromSeconds(1));
        store.Push("sensor.charger_car_state", "Idle", unit: null);
        Assert.False(EvInputs.From(store.Snapshot(), Ev(store)).IsPlugged);
    }

    [Fact]
    public void An_unavailable_plugged_state_is_no_evidence_either_way()
    {
        // Why: a lost link to the charger is not an unplugged car. Plugged is null, not false, so the module can tell "goals inactive" from "not plugged".
        var store = Store();
        PushAll(store);
        store.Advance(TimeSpan.FromSeconds(1));
        store.Push("sensor.charger_car_state", "unavailable", unit: null);

        var inputs = EvInputs.From(store.Snapshot(), Ev(store));

        Assert.Null(inputs.IsPlugged);
        Assert.Equal((true, SignalState.Unavailable), (inputs.Plugged.Configured, inputs.Plugged.State));
    }

    [Fact]
    public void The_inputs_view_carries_values_with_read_times_and_freshness()
    {
        var store = Store();
        PushAll(store);
        var readAt = store.Now;
        var departure = Departure(store);
        store.Advance(TimeSpan.FromMinutes(10));

        var inputs = EvInputs.From(store.Snapshot(), Ev(store));

        Assert.Equal(55.0, inputs.SocPercent);
        Assert.Equal(31 * 1.609344, inputs.RangeKm!.Value, 9);
        Assert.True(inputs.IsPlugged);
        Assert.Equal("Charging", inputs.PluggedState);
        Assert.Equal(departure, inputs.CalendarEvent);
        Assert.All(new[] { inputs.Soc, inputs.Range, inputs.Plugged, inputs.Calendar }, read =>
        {
            Assert.True(read.Configured);
            Assert.True(read.IsFresh);
            Assert.Equal(readAt, read.ReadAt);
        });
        Assert.Equal(TimeSpan.FromMinutes(30), inputs.Soc.MaxAge);
        Assert.Equal(TimeSpan.FromHours(24), inputs.Calendar.MaxAge);
    }

    [Fact]
    public void A_stale_soc_has_no_value_in_the_view_never_the_last_one()
    {
        // Why: B10 and the failure table: a stale SoC makes the goals inactive. The view must not hand over the old number to be used.
        var store = Store();
        PushAll(store);

        store.Advance(TimeSpan.FromMinutes(31));
        var inputs = EvInputs.From(store.Snapshot(), Ev(store));

        Assert.Null(inputs.SocPercent);
        Assert.Equal((SignalState.Stale, SignalDetail.MaxAgeExceeded), (inputs.Soc.State, inputs.Soc.Detail));
        Assert.NotNull(inputs.Soc.ReadAt);
    }

    [Fact]
    public void An_input_the_section_does_not_declare_is_not_configured_in_the_view()
    {
        var store = Store("ev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level }\n");
        store.Push("sensor.car_battery_level", "55", unit: "%");

        var inputs = EvInputs.From(store.Snapshot(), Ev(store));

        Assert.Equal(55.0, inputs.SocPercent);
        Assert.All(new[] { inputs.Range, inputs.Plugged, inputs.Calendar }, read => Assert.False(read.Configured || read.IsFresh));
        Assert.Null(inputs.IsPlugged);
    }

    [Fact]
    public void The_view_reads_the_ev_roles_only_and_goal_inputs_still_hold_the_four_goal_roles()
    {
        // Why: B7: GoalInputs is not widened. The EV signals must not appear in it, and the EV view must not pick up a goal or protection signal.
        var store = Store();
        PushAll(store);
        var snapshot = store.Snapshot();
        var evNames = snapshot.Signals.Where(s => EvInputs.Roles.Contains(s.Role)).Select(s => s.Name!.Value).ToList();

        Assert.Equal(4, evNames.Count);
        Assert.Empty(GoalInputs.From(snapshot).Samples.Keys.Intersect(evNames));
        Assert.Equal(
            new[] { SignalRole.EvSoc, SignalRole.EvRange, SignalRole.EvPlugged, SignalRole.EvCalendar },
            EvInputs.Roles.ToArray());
    }

    [Fact]
    public void The_ev_signals_do_not_change_which_phases_protection_and_dispatch_see_or_whether_their_data_is_valid()
    {
        // Why: the module's inputs are shown in diagnostics and read by the module; the guard and dispatch decide from the same inputs with or without them.
        var plain = Store("ev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level }\n");
        var full = Store();
        foreach (var store in new[] { plain, full })
        {
            store.Push("sensor.car_battery_level", "55", unit: "%");
        }

        PushAll(full);

        var a = plain.Snapshot();
        var b = full.Snapshot();
        Assert.Equal(a.Protection.Phases.Length, b.Protection.Phases.Length);
        Assert.Equal(a.Dispatch.Phases.Select(p => p.Phase), b.Dispatch.Phases.Select(p => p.Phase));
        Assert.Equal(a.Rejections, b.Rejections);
        Assert.Equal(a.Protection.Phases.Select(p => (p.Phase, p.DataValid, p.InvalidReason)), b.Protection.Phases.Select(p => (p.Phase, p.DataValid, p.InvalidReason)));
    }

    [Fact]
    public void A_lost_feed_makes_every_ev_input_unavailable_even_an_on_change_plugged_state()
    {
        // Why: an on_change state has no age limit, so after an HA link loss it would stay Fresh for ever and a car unplugged meanwhile would read as plugged.
        // Unknown, never false: the module must tell "goals inactive" from "not plugged".
        var store = Store();
        PushAll(store);
        Assert.True(EvInputs.From(store.Snapshot(), Ev(store)).IsPlugged);

        store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        store.Advance(TimeSpan.FromSeconds(5));
        var inputs = EvInputs.From(store.Snapshot(), Ev(store));

        Assert.Null(inputs.IsPlugged);
        Assert.Null(inputs.PluggedState);
        Assert.Null(inputs.SocPercent);
        Assert.Null(inputs.RangeKm);
        Assert.Null(inputs.CalendarEvent);
        Assert.All(new[] { inputs.Soc, inputs.Range, inputs.Plugged, inputs.Calendar }, read =>
        {
            Assert.True(read.Configured);
            Assert.False(read.IsFresh);
            Assert.Equal((SignalState.Unavailable, SignalDetail.SourceDisconnected), (read.State, read.Detail));
        });
        Assert.NotNull(inputs.Plugged.ReadAt);
    }

    [Fact]
    public void The_inputs_come_back_when_the_feed_does()
    {
        var store = Store();
        PushAll(store);
        store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        store.Store.ApplySourceStatus(SignalSourceStatus.Connected);

        Assert.Equal(55.0, EvInputs.From(store.Snapshot(), Ev(store)).SocPercent);
    }
}
