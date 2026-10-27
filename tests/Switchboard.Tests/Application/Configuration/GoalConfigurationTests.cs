using Switchboard.Application.Configuration;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// PLAN-01 and PLAN-02: a device's <c>goal:</c> (15-PLANNER, 0.11 slice 1). It is typed by hand, so every mistake must be a named issue
/// with a position and never a silently wrong goal; it is optional and additive, so a 0.10 file (the whole installed base, whose
/// <c>goal:</c> key was a reserved warning) keeps its signals, devices and decisions; and a goal input is a device-scoped signal, so no
/// entity ID leaves the signal layer.
/// </summary>
public sealed class GoalConfigurationTests
{
    private const string Tariff = """
        economics:
          currency: PLN
          holidays: [2026-11-01]
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

    private const string SocGoal = """
              kind: soc
              hard: true
              capacity_kwh: 18
              loss_fraction: 0.10
              soc: { entity: sensor.car_battery_level, max_age: 30m }
              target: { value: 90 }
              deadline: { entity: input_datetime.car_departure, margin: 30m }
              sources: pv_and_zone:offpeak
        """;

    private static string Charger(string goal, string extraKeys = "") =>
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n"
        + extraKeys
        + "    goal:\n" + goal.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";

    private static ConfigurationResult Parse(string goal, string extraKeys = "", string economics = Tariff) =>
        TestConfigurations.Parse(TestDevices.Document(Charger(goal, extraKeys), extra: economics));

    private static SwitchboardConfiguration Valid(string goal, string extraKeys = "", string economics = Tariff) =>
        TestConfigurations.Valid(TestDevices.Document(Charger(goal, extraKeys), extra: economics)).Configuration!;

    // The specs hold immutable arrays, which compare by reference: the text of their properties is what must not move.
    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static ConfigurationIssue Error(ConfigurationResult result, string code, string path) =>
        Assert.Single(result.Errors, e => e.Code == code && e.Path == path);

    private static string Soc(string replaceKey, string? replacement)
    {
        var lines = SocGoal.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var index = lines.FindIndex(l => l.TrimStart().StartsWith(replaceKey + ":", StringComparison.Ordinal));
        Assert.True(index >= 0, replaceKey);
        if (replacement is null)
        {
            lines.RemoveAt(index);
        }
        else
        {
            lines[index] = "      " + replacement;
        }

        return string.Join('\n', lines);
    }

    [Fact]
    public void A_goal_is_read_as_the_design_writes_it_and_its_entities_become_device_scoped_signals()
    {
        // Why: the planner must read soc, target, deadline and want like any measurement (freshness, units), with the entity ID kept in
        // the signal layer. Everything else about the goal is what the design's example says.
        var configuration = Valid(SocGoal);

        var goal = Assert.IsType<GoalSpec>(configuration.Devices.Single().Goal);
        Assert.Equal((GoalKind.Soc, true, 18.0, 0.10), (goal.Kind, goal.Hard, goal.CapacityKwh, goal.LossFraction));
        Assert.Equal("device_ev_charger_soc", goal.Soc!.Signal!.Value.Value);
        Assert.Equal(90, goal.Target.Literal);
        Assert.Equal(("device_ev_charger_goal_deadline", TimeSpan.FromMinutes(30)), (goal.Deadline!.Signal!.Value.Value, goal.Deadline.Margin));
        Assert.Equal("pv_and_zone:offpeak", goal.Sources!.ToText());
        Assert.True(goal.Escalate);
        Assert.Equal((GoalOnMet.Opportunistic, GoalOrigin.Entity), (goal.OnMet, goal.Origin));

        var soc = configuration.Signals.Single(s => s.Name.Value == "device_ev_charger_soc");
        Assert.Equal(("sensor.car_battery_level", SignalKind.Percentage, "%", SignalRole.DeviceSoc, TimeSpan.FromMinutes(30)), (soc.Entity.Value, soc.Kind, soc.Unit, soc.Role, soc.MaxAge));
        var deadline = configuration.Signals.Single(s => s.Name.Value == "device_ev_charger_goal_deadline");
        Assert.Equal((SignalKind.State, SignalRole.GoalDeadline), (deadline.Kind, deadline.Role));
        Assert.Equal("devices[0].goal.deadline.max_age", deadline.MaxAgePath);
        Assert.DoesNotContain(configuration.Signals, s => s.Name.Value.Contains("goal_target", StringComparison.Ordinal));
    }

    [Fact]
    public void An_input_entity_without_max_age_takes_the_control_parameter_not_a_literal()
    {
        // Why: no literal for a control parameter (CLAUDE.md): the default freshness of a goal input is control.planner.goal.input_max_age.
        var configuration = Valid(Soc("deadline", "deadline: { entity: input_datetime.car_departure }"));

        var deadline = configuration.Signals.Single(s => s.Role == SignalRole.GoalDeadline);
        Assert.Equal(configuration.Control.Planner.Goal.InputMaxAge, deadline.MaxAge);
    }

    [Fact]
    public void A_goal_of_literals_is_a_config_goal_and_creates_no_signal()
    {
        // Why: a literal needs no entity, so a goal of literals must not create a signal a device could not feed.
        var configuration = Valid("      kind: energy\n      target_kwh: 12\n      deadline: { at: \"06:30\" }\n");

        var goal = configuration.Devices.Single().Goal!;
        Assert.Equal((GoalKind.Energy, GoalOrigin.Config, false, 12.0), (goal.Kind, goal.Origin, goal.Hard, goal.Target.Literal));
        Assert.Equal(new TimeOnly(6, 30), goal.Deadline!.At);
        Assert.Null(goal.Deadline.Signal);
        Assert.False(goal.Escalate);
        Assert.DoesNotContain(configuration.Signals, s => s.Role is SignalRole.DeviceSoc or SignalRole.GoalTarget or SignalRole.GoalDeadline or SignalRole.GoalWant);
    }

    [Fact]
    public void An_energy_target_may_be_an_entity_with_its_own_unit_and_a_want_gate_makes_an_entity_goal()
    {
        var configuration = Valid("      kind: energy\n      target_kwh: { entity: input_number.tank_target, unit: Wh, max_age: 2h }\n      want: { entity: input_boolean.charge_wanted }\n");

        var target = configuration.Signals.Single(s => s.Role == SignalRole.GoalTarget);
        Assert.Equal((SignalKind.EnergyCounter, "Wh", TimeSpan.FromHours(2)), (target.Kind, target.Unit, target.MaxAge));
        var want = configuration.Signals.Single(s => s.Role == SignalRole.GoalWant);
        Assert.Equal((SignalKind.State, "device_ev_charger_goal_want"), (want.Kind, want.Name.Value));
        Assert.Equal(GoalOrigin.Entity, configuration.Devices.Single().Goal!.Origin);
    }

    [Fact]
    public void Kind_none_is_no_goal_and_no_warning()
    {
        // Why: 0.10 warned that goal was reserved; a file that wrote kind: none to say "no goal" must now say it quietly.
        var result = Parse("      kind: none\n");

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Null(result.Configuration!.Devices.Single().Goal);
        Assert.DoesNotContain(result.Warnings, w => w.Path.StartsWith("devices[0].goal", StringComparison.Ordinal));
    }

    [Fact]
    public void A_010_file_loads_unchanged_with_no_goal_no_planner_no_new_signal_and_no_new_warning()
    {
        // Why: the installed base has neither key. Adding the feature must not move a signal, a device or a warning, or a 0.10 file would
        // decide differently (the digest tests then fail too).
        var result = TestConfigurations.Valid(TestConfigurations.Example);

        Assert.Null(result.Configuration!.Planner);
        Assert.All(result.Configuration.Devices, d => Assert.Null(d.Goal));
        Assert.DoesNotContain(result.Configuration.Signals, s => s.Role is SignalRole.DeviceSoc or SignalRole.GoalTarget or SignalRole.GoalDeadline or SignalRole.GoalWant);
        Assert.DoesNotContain(result.Warnings, w => w.Code is ConfigurationIssueCodes.GoalDropped or ConfigurationIssueCodes.GoalWithoutStart);
    }

    [Fact]
    public void A_goal_never_reaches_what_the_guard_and_dispatch_are_built_from()
    {
        // Why: the planner decides when energy is wanted, never whether it is allowed (D2 to D4). If a goal changed the protection setup or the
        // dispatch setup, a file with a goal would decide differently from the same file without one, which this slice promises it never does.
        var without = Valid("      kind: none\n");
        var with = Valid(SocGoal);

        Assert.Equal(Json(ProtectionSetupFactory.Build(without).Devices), Json(ProtectionSetupFactory.Build(with).Devices));
        Assert.Equal(Json(DispatchSetupFactory.Build(without).Devices.Select(d => d.Spec)), Json(DispatchSetupFactory.Build(with).Devices.Select(d => d.Spec)));
        Assert.Equal(without.Devices.Single().ToSpec().ActuatorRevision, with.Devices.Single().ToSpec().ActuatorRevision);
    }

    [Theory]
    [InlineData("capacity_kwh")]
    [InlineData("soc")]
    [InlineData("target")]
    public void A_soc_goal_needs_its_capacity_soc_and_target(string key)
    {
        var result = Parse(Soc(key, null));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Error(result, ConfigurationIssueCodes.MissingKey, $"devices[0].goal.{key}");
    }

    [Fact]
    public void An_energy_goal_needs_its_target_kwh()
    {
        var result = Parse("      kind: energy\n      hard: true\n");

        Error(result, ConfigurationIssueCodes.MissingKey, "devices[0].goal.target_kwh");
    }

    [Fact]
    public void Keys_of_the_other_kind_are_refused_not_ignored()
    {
        // Why: a soc goal carrying target_kwh would read as if the energy were the target; saying so beats guessing which one was meant.
        var soc = Parse(SocGoal + "\n      target_kwh: 5\n");
        var energy = Parse("      kind: energy\n      target_kwh: 5\n      capacity_kwh: 18\n");

        Error(soc, ConfigurationIssueCodes.KeyNotAllowed, "devices[0].goal.target_kwh");
        Error(energy, ConfigurationIssueCodes.KeyNotAllowed, "devices[0].goal.capacity_kwh");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    public void Loss_fraction_stays_in_zero_to_below_one(string value)
    {
        // Why: a fraction of 1 or more would plan for at least twice the energy needed; it is a typo for a percentage.
        var result = Parse(Soc("loss_fraction", $"loss_fraction: {value}"));

        Error(result, ConfigurationIssueCodes.OutOfRange, "devices[0].goal.loss_fraction");
    }

    [Theory]
    [InlineData("deadline: { at: \"6.30\" }", "devices[0].goal.deadline.at")]
    [InlineData("deadline: { at: \"25:00\" }", "devices[0].goal.deadline.at")]
    [InlineData("deadline: { at: \"06:30\", margin: soon }", "devices[0].goal.deadline.margin")]
    public void A_deadline_that_cannot_be_read_is_an_error_with_its_position(string deadline, string path)
    {
        var result = Parse(Soc("deadline", deadline));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == path);
    }

    [Fact]
    public void A_deadline_names_at_or_an_entity_and_not_both()
    {
        Error(Parse(Soc("deadline", "deadline: { margin: 30m }")), ConfigurationIssueCodes.MissingKey, "devices[0].goal.deadline");
        Error(Parse(Soc("deadline", "deadline: { at: \"06:30\", entity: input_datetime.car_departure }")), ConfigurationIssueCodes.InvalidValue, "devices[0].goal.deadline");
    }

    [Theory]
    [InlineData("soc: { entity: switch.relay }", "devices[0].goal.soc.entity")]
    [InlineData("soc: { entity: not_an_entity }", "devices[0].goal.soc.entity")]
    [InlineData("target: { entity: input_boolean.flag }", "devices[0].goal.target.entity")]
    [InlineData("deadline: { entity: sensor.x_y, max_age: 90d }", "devices[0].goal.deadline.max_age")]
    public void An_input_entity_must_be_a_domain_that_can_carry_it(string replacement, string path)
    {
        // Why: an actuator or a helper of the wrong kind can never stand in for a measurement (the same rule the draw follows).
        var key = replacement[..replacement.IndexOf(':', StringComparison.Ordinal)];
        var result = Parse(Soc(key, replacement));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == path);
    }

    [Fact]
    public void A_goal_entity_may_not_also_feed_another_signal()
    {
        // Why: one entity feeds one signal (SIG-01); the diagnostics could not say which freshness is which otherwise.
        var result = Parse(Soc("deadline", "deadline: { entity: sensor.car_battery_level }"));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.DuplicateEntity);
    }

    [Fact]
    public void An_unknown_goal_key_is_an_error()
    {
        Error(Parse(SocGoal + "\n      colour: red\n"), ConfigurationIssueCodes.UnknownKey, "devices[0].goal.colour");
    }

    [Fact]
    public void A_goal_zone_the_tariff_does_not_have_is_warned_and_dropped_never_widened()
    {
        // Why: a configuration problem may only narrow (14-ECONOMICS): the goal keeps pv and loses the zone it cannot price.
        var result = Parse(Soc("sources", "sources: pv_and_zone:offpeak,night"));

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal("pv_and_zone:offpeak", result.Configuration!.Devices.Single().Goal!.Sources!.ToText());
        Assert.Single(result.Warnings, w => w.Code == ConfigurationIssueCodes.SourcesUnusable && w.Path == "devices[0].goal.sources");
    }

    [Fact]
    public void A_goal_on_a_restore_only_device_is_warned_because_the_planner_never_starts_it()
    {
        var result = Parse(SocGoal, extraKeys: "    start: restore_only\n");

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Single(result.Warnings, w => w.Code == ConfigurationIssueCodes.GoalWithoutStart && w.Path == "devices[0].goal");
        Assert.NotNull(result.Configuration!.Devices.Single().Goal);
    }

    [Theory]
    [InlineData("target: { value: 150 }")]
    [InlineData("soc: { value: -5 }")]
    public void A_goal_that_can_never_be_evaluated_is_dropped_with_a_warning_and_the_file_stays_valid(string replacement)
    {
        // Why: the failure table of the design: a goal invalid in a semantic way is dropped with a warning, never an invalid document.
        // Narrowing (no goal) is the only direction a configuration problem may move a device.
        var key = replacement[..replacement.IndexOf(':', StringComparison.Ordinal)];
        var result = Parse(Soc(key, replacement));

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Null(result.Configuration!.Devices.Single().Goal);
        Assert.Single(result.Warnings, w => w.Code == ConfigurationIssueCodes.GoalDropped && w.Path == "devices[0].goal");
        Assert.DoesNotContain(result.Configuration.Signals, s => s.Role == SignalRole.DeviceSoc);
    }

    [Fact]
    public void A_literal_and_an_entity_together_or_neither_is_an_error()
    {
        Error(Parse(Soc("target", "target: { value: 90, entity: input_number.t }")), ConfigurationIssueCodes.InvalidValue, "devices[0].goal.target");
        Error(Parse(Soc("target", "target: { max_age: 1h }")), ConfigurationIssueCodes.InvalidValue, "devices[0].goal.target");
    }

    [Fact]
    public void The_goal_revision_moves_with_what_the_goal_is_and_not_with_when_it_was_read()
    {
        // Why: a plan entry made for another revision must not be applied; the fingerprint has to change when the goal changes and stay
        // put across reloads of the same text.
        var a = Valid(SocGoal).Devices.Single().Goal!;
        var again = Valid(SocGoal).Devices.Single().Goal!;
        var other = Valid(Soc("target", "target: { value: 80 }")).Devices.Single().Goal!;

        Assert.Equal(a.Revision, again.Revision);
        Assert.NotEqual(a.Revision, other.Revision);
    }
}
