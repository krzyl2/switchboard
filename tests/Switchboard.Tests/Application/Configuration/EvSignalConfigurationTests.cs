using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// EV-01 and EV-02, slice S2: the optional <c>range</c>, <c>plugged</c> and <c>calendar</c> entities of the <c>ev:</c> section. They follow slice S1's style:
/// a wrong word is an error with a position, an entity another signal already reads is a <c>duplicate_entity</c>, max_age defaults come from control
/// parameters, never a literal. The plugged state map is the user's list of states, so no vendor's wording is in code. Declared or not, none of them is a
/// protection or dispatch input.
/// </summary>
public sealed class EvSignalConfigurationTests
{
    private const string Charger =
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    sources: pv_and_zone:offpeak\n";

    private const string Soc = "ev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level }\n";

    private static ConfigurationResult Parse(string evExtra, string control = "", string soc = Soc) =>
        TestConfigurations.Parse(TestDevices.Document(Charger, control: control, extra: "planner:\n  dispatch: narrow\n\n" + soc + evExtra, defaultStart: "headroom"));

    private static ConfigurationIssue[] EvIssues(ConfigurationResult result) =>
        [.. result.Errors.Concat(result.Warnings).Where(i => i.Path == "ev" || i.Path.StartsWith("ev.", StringComparison.Ordinal))];

    private static SignalDefinition Signal(ConfigurationResult result, SignalRole role) => result.Configuration!.Signals.Single(s => s.Role == role);

    [Fact]
    public void A_section_without_the_new_blocks_creates_only_the_soc_signal_as_in_s1()
    {
        // Why: the new blocks are optional; a file written for S1 must keep its meaning and add no signal.
        var configuration = Parse(string.Empty).Configuration!;

        Assert.Single(configuration.Signals, s => s.Role is SignalRole.EvSoc or SignalRole.EvRange or SignalRole.EvPlugged or SignalRole.EvCalendar);
        Assert.Null(configuration.Ev!.Range);
        Assert.Null(configuration.Ev.Plugged);
        Assert.Null(configuration.Ev.Calendar);
        Assert.True(configuration.Ev.PluggedStates.IsDefaultOrEmpty);
    }

    [Fact]
    public void All_three_blocks_are_read_and_each_creates_one_signal_with_a_name_the_user_cannot_take()
    {
        var result = Parse("  range: { entity: sensor.car_range, unit: mi, max_age: 2h }\n  plugged: { entity: sensor.charger_car_state, states: [Charging, \"Wait for car\"] }\n  calendar: { entity: calendar.kursy, max_age: 12h }\n");
        var ev = result.Configuration!.Ev!;

        Assert.Empty(EvIssues(result));
        var range = Signal(result, SignalRole.EvRange);
        Assert.Equal((SignalKind.Distance, "mi", TimeSpan.FromHours(2), "ev.range"), (range.Kind, range.Unit, range.MaxAge, range.Path));
        var plugged = Signal(result, SignalRole.EvPlugged);
        Assert.Equal((SignalKind.State, StateFreshness.OnChange, "ev.plugged"), (plugged.Kind, plugged.Freshness, plugged.Path));
        var calendar = Signal(result, SignalRole.EvCalendar);
        Assert.Equal((SignalKind.CalendarEvent, TimeSpan.FromHours(12)), (calendar.Kind, calendar.MaxAge));
        Assert.Equal(new[] { range.Name, plugged.Name, calendar.Name }, new[] { ev.Range!.Value, ev.Plugged!.Value, ev.Calendar!.Value });
        Assert.All(new[] { range.Name, plugged.Name, calendar.Name }, n => Assert.StartsWith("device_", n.Value, StringComparison.Ordinal));
        Assert.Equal(new[] { "Charging", "Wait for car" }, ev.PluggedStates.ToArray());
    }

    [Fact]
    public void The_range_unit_defaults_to_km_and_the_age_defaults_follow_the_control_parameters()
    {
        // Why: no literal for a control parameter. The range is judged like the SoC (the goal input age); the calendar re-reports rarely, so its default is the
        // longest age the signal layer allows. An override of the parameter must move both.
        var shipped = Parse("  range: { entity: sensor.car_range }\n  calendar: { entity: calendar.kursy }\n");
        var overridden = Parse(
            "  range: { entity: sensor.car_range }\n  calendar: { entity: calendar.kursy }\n",
            control: "  planner:\n    goal:\n      input_max_age: 2h\n  signals:\n    max_age_limit: 48h\n");

        Assert.Equal(("km", TimeSpan.FromHours(1), TimeSpan.FromHours(24)), (Signal(shipped, SignalRole.EvRange).Unit, Signal(shipped, SignalRole.EvRange).MaxAge, Signal(shipped, SignalRole.EvCalendar).MaxAge));
        Assert.Equal((TimeSpan.FromHours(2), TimeSpan.FromHours(48)), (Signal(overridden, SignalRole.EvRange).MaxAge, Signal(overridden, SignalRole.EvCalendar).MaxAge));
    }

    [Fact]
    public void A_max_age_above_the_signal_limit_is_an_error_for_every_block()
    {
        var result = Parse("  range: { entity: sensor.car_range, max_age: 30h }\n  calendar: { entity: calendar.kursy, max_age: 30h }\n");

        Assert.Equal(["ev.calendar.max_age", "ev.range.max_age"], result.Errors.Where(e => e.Code == ConfigurationIssueCodes.OutOfRange).Select(e => e.Path).Order());
        Assert.Null(result.Configuration);
    }

    [Fact]
    public void A_distance_unit_other_than_km_or_mi_is_an_error_at_its_position()
    {
        var result = Parse("  range: { entity: sensor.car_range, unit: m }\n");

        var error = Assert.Single(result.Errors, e => e.Path == "ev.range.unit");
        Assert.Equal(ConfigurationIssueCodes.UnitNotAllowedForKind, error.Code);
        Assert.NotNull(error.Line);
    }

    [Theory]
    [InlineData("  range: { entity: calendar.kursy }\n", "ev.range.entity")]
    [InlineData("  calendar: { entity: sensor.kursy }\n", "ev.calendar.entity")]
    [InlineData("  plugged: { entity: number.x, states: [on] }\n", "ev.plugged.entity")]
    [InlineData("  range: { entity: car_range }\n", "ev.range.entity")]
    public void An_entity_of_the_wrong_domain_or_form_is_an_error_at_its_position(string block, string path)
    {
        var result = Parse(block);

        Assert.Contains(result.Errors, e => e.Path == path);
    }

    [Fact]
    public void An_entity_another_signal_or_block_already_reads_is_a_duplicate_entity()
    {
        // Why: one entity feeds one signal; the same sensor as SoC and range would be read twice under two meanings.
        var result = Parse("  range: { entity: sensor.car_battery_level }\n");

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.DuplicateEntity && e.Path == "ev.range.entity");
    }

    [Fact]
    public void The_same_entity_in_two_new_blocks_is_a_duplicate_entity_too()
    {
        var result = Parse("  range: { entity: sensor.shared }\n  plugged: { entity: sensor.shared, states: [on] }\n");

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.DuplicateEntity && e.Path == "ev.plugged.entity");
    }

    [Fact]
    public void The_charger_s_actuator_cannot_be_the_plugged_state()
    {
        var result = Parse("  plugged: { entity: number.charger_current, states: [on] }\n");

        Assert.Contains(result.Errors, e => e.Path == "ev.plugged.entity");
    }

    [Fact]
    public void Plugged_needs_its_states_and_an_empty_list_or_an_unavailable_state_is_refused()
    {
        // Why: the states are the user's; an empty map would never be plugged, and "unavailable" as plugged would turn a lost link into a car.
        Assert.Contains(Parse("  plugged: { entity: sensor.s }\n").Errors, e => e.Code == ConfigurationIssueCodes.MissingKey && e.Path.StartsWith("ev.plugged", StringComparison.Ordinal));
        Assert.Contains(Parse("  plugged: { entity: sensor.s, states: [] }\n").Errors, e => e.Path == "ev.plugged.states");
        Assert.Contains(Parse("  plugged: { entity: sensor.s, states: [unavailable] }\n").Errors, e => e.Path == "ev.plugged.states[0]");
        Assert.Contains(Parse("  plugged: { entity: sensor.s, states: [\" \"] }\n").Errors, e => e.Path == "ev.plugged.states[0]");
    }

    [Fact]
    public void A_single_state_may_be_written_without_a_list()
    {
        Assert.Equal(new[] { "on" }, Parse("  plugged: { entity: binary_sensor.plug, states: \"on\" }\n").Configuration!.Ev!.PluggedStates.ToArray());
    }

    [Fact]
    public void Plugged_reports_on_change_by_default_and_age_is_an_explicit_choice_with_its_own_max_age()
    {
        var byAge = Parse("  plugged: { entity: sensor.s, states: [on], state_reports: age, max_age: 15m }\n");

        var plugged = Signal(byAge, SignalRole.EvPlugged);
        Assert.Equal((StateFreshness.Age, TimeSpan.FromMinutes(15)), (plugged.Freshness, plugged.MaxAge));
    }

    [Fact]
    public void A_max_age_next_to_on_change_is_refused_as_it_is_for_a_device_state()
    {
        var result = Parse("  plugged: { entity: sensor.s, states: [on], max_age: 15m }\n");

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.KeyNotAllowed && e.Path == "ev.plugged.max_age");
    }

    [Fact]
    public void An_unknown_key_in_a_new_block_is_an_error()
    {
        var result = Parse("  calendar: { entity: calendar.kursy, mode: x }\n");

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.UnknownKey && e.Path == "ev.calendar.mode");
    }

    [Fact]
    public void On_change_is_refused_for_the_other_new_roles()
    {
        // Why: only the plugged state is a discrete state that writes on change; a range or a calendar that stops reporting must go stale.
        foreach (var role in new[] { SignalRole.EvSoc, SignalRole.EvRange, SignalRole.EvCalendar })
        {
            var signal = Signal(Parse("  range: { entity: sensor.car_range }\n  calendar: { entity: calendar.kursy }\n"), role);
            Assert.Throws<ArgumentException>(() => signal with { Freshness = StateFreshness.OnChange });
        }
    }

    [Fact]
    public void A_file_without_ev_declares_no_ev_signal_at_all()
    {
        var result = TestConfigurations.Valid(TestDevices.Document(Charger, defaultStart: "headroom"));

        Assert.DoesNotContain(result.Configuration!.Signals, s => s.Role is SignalRole.EvSoc or SignalRole.EvRange or SignalRole.EvPlugged or SignalRole.EvCalendar);
    }
}
