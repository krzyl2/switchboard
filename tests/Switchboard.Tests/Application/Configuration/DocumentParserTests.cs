using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// switchboard.yaml is untrusted input that decides what protection reads. Every mistake must be
/// refused with a precise code, path and line, all of them at once, and never silently defaulted.
/// </summary>
public sealed class DocumentParserTests
{
    // Line numbers below refer to this document: 3 topology, 9 entity, 12 sign, 13 max_age, 14 role, 15 phase.
    private static readonly string Base = """
        schema_version: 1
        connection:
          topology: single_phase
          breaker_rating_a: 32
          characteristic: B
          voltage_v: 230
        signals:
          grid_current:
            entity: sensor.meter_current
            kind: current
            unit: A
            sign: positive_import
            max_age: 60s
            role: phase_current
            phase: L1

        """.ReplaceLineEndings("\n");

    [Fact]
    public void The_base_document_is_valid()
    {
        var result = TestConfigurations.Valid(Base);

        var configuration = result.Configuration!;
        Assert.Equal(Topology.SinglePhase, configuration.Connection.Topology);
        Assert.Equal(2500, configuration.Connection.Reserve[Phase.L1].Value);
        Assert.Equal(0.1, configuration.Signals.Single().Sigma);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_example_for_the_maintainer_installation_is_valid()
    {
        var result = TestConfigurations.Valid(TestConfigurations.Example);

        // 13 mapped signals plus one applied-state signal per device stage (three heaters, the charger).
        Assert.Empty(result.Warnings);
        Assert.Equal(13, result.Configuration!.Signals.Count(s => s.Role != SignalRole.DeviceState));
        Assert.Equal(4, result.Configuration.Signals.Count(s => s.Role == SignalRole.DeviceState));
    }

    [Fact]
    public void Misspelt_key_is_an_error_with_its_line_because_typos_must_not_be_ignored()
    {
        AssertError(Base.Replace("    max_age: 60s", "    max_agee: 60s"), "unknown_key", "signals.grid_current.max_agee", 13);
    }

    [Fact]
    public void Every_error_reported_not_only_the_first()
    {
        var result = TestConfigurations.Parse(Base
            .Replace("topology: single_phase", "topology: four_phase")
            .Replace("sensor.meter_current", "Sensor Meter")
            .Replace("max_age: 60s", "max_age: soon"));

        Assert.Equal(["invalid_value", "invalid_entity_id", "invalid_duration"], result.Errors.Select(e => e.Code).Take(3));
        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Null(result.Configuration);
    }

    [Fact]
    public void Missing_required_key_names_the_key()
    {
        AssertError(Base.Replace("  voltage_v: 230\n", string.Empty), "missing_key", "connection.voltage_v", 3);
    }

    [Fact]
    public void An_empty_document_lists_what_is_required()
    {
        var result = TestConfigurations.Parse(string.Empty);

        Assert.Equal(["schema_version", "connection", "signals"], result.Errors.Where(e => e.Code == "missing_key").Select(e => e.Path));
    }

    [Fact]
    public void Unsupported_schema_version_is_refused()
    {
        AssertError(Base.Replace("schema_version: 1", "schema_version: 2"), "unsupported_schema_version", "schema_version", 1);
    }

    [Fact]
    public void A_quoted_number_is_text_not_a_number()
    {
        AssertError(Base.Replace("breaker_rating_a: 32", "breaker_rating_a: \"32\""), "wrong_type", "connection.breaker_rating_a", 4);
    }

    [Fact]
    public void A_list_where_a_mapping_belongs_is_the_wrong_type()
    {
        AssertError(Base.Replace("signals:\n  grid_current:", "signals:\n  - grid_current:"), "wrong_type", "signals", 8);
    }

    [Theory]
    [InlineData("breaker_rating_a: 32", "breaker_rating_a: 320", "connection.breaker_rating_a")]
    [InlineData("voltage_v: 230", "voltage_v: 23", "connection.voltage_v")]
    [InlineData("max_age: 60s", "max_age: 0s", "signals.grid_current.max_age")]
    public void Implausible_values_are_out_of_range(string from, string to, string path)
    {
        AssertError(Base.Replace(from, to), "out_of_range", path);
    }

    [Theory]
    [InlineData("1.5m")]
    [InlineData("90")]
    [InlineData("1m30s")]
    [InlineData("unlimited")]
    public void Durations_have_one_whole_number_and_one_unit(string value)
    {
        AssertError(Base.Replace("max_age: 60s", $"max_age: {value}"), "invalid_duration", "signals.grid_current.max_age", 13);
    }

    [Theory]
    [InlineData("Grid-Current")]
    [InlineData("none")]
    [InlineData("1st")]
    public void Signal_names_are_snake_case_and_none_is_reserved(string name)
    {
        AssertError(Base.Replace("  grid_current:", $"  {name}:"), "invalid_signal_name", $"signals.{name}", 8);
    }

    [Theory]
    [InlineData("2400h")]
    [InlineData("99999999h")]
    public void Max_age_beyond_the_limit_is_refused_with_its_line(string maxAge)
    {
        // A frozen sensor counts as fresh for its whole max_age; 99999999h also overflowed the
        // store's deadline arithmetic and took the add-on down instead of naming the line.
        AssertError(Base.Replace("max_age: 60s", $"max_age: {maxAge}"), "out_of_range", "signals.grid_current.max_age", 13);
    }

    [Fact]
    public void Max_age_up_to_the_limit_is_accepted_and_the_limit_can_be_raised()
    {
        Assert.Equal(TimeSpan.FromHours(24), TestConfigurations.Valid(Base.Replace("max_age: 60s", "max_age: 24h")).Configuration!.Signals.Single().MaxAge);

        TestConfigurations.Valid(Base.Replace("max_age: 60s", "max_age: 30h") + "control:\n  signals:\n    max_age_limit: 48h\n");
    }

    [Fact]
    public void Max_age_limit_cannot_pass_the_longest_timer_the_store_can_arm()
    {
        AssertError(Base + "control:\n  signals:\n    max_age_limit: 1200h\n", "out_of_range", "control.signals.max_age_limit", 18);
    }

    [Fact]
    public void Entity_id_must_look_like_one()
    {
        AssertError(Base.Replace("sensor.meter_current", "meter_current"), "invalid_entity_id", "signals.grid_current.entity", 9);
    }

    [Theory]
    [InlineData("sensor.meter_current__a")]
    [InlineData("sensor._meter_current")]
    [InlineData("sensor.meter_current_")]
    [InlineData("_sensor.meter_current")]
    [InlineData("sensor_.meter_current")]
    public void Entity_id_follows_home_assistants_own_rule(string entity)
    {
        // HA refuses the whole subscribe_entities call for one such ID, which blinded every signal
        // and reconnected every second; here it is one error with its line.
        AssertError(Base.Replace("sensor.meter_current", entity), "invalid_entity_id", "signals.grid_current.entity", 9);
    }

    [Fact]
    public void One_entity_feeds_one_signal()
    {
        AssertError(Base + Signal("copy", "entity: sensor.meter_current", "kind: current", "unit: A", "sign: positive_import", "max_age: 60s"), "duplicate_entity", "signals.copy.entity");
    }

    [Theory]
    [InlineData("current", "W")]
    [InlineData("power", "A")]
    [InlineData("voltage", "kV")]
    [InlineData("energy_counter", "W")]
    [InlineData("percentage", "pct")]
    public void Units_are_checked_against_the_kind(string kind, string unit)
    {
        string[] lines = kind is "current" or "power"
            ? ["entity: sensor.extra", $"kind: {kind}", $"unit: {unit}", "sign: positive_import", "max_age: 60s"]
            : ["entity: sensor.extra", $"kind: {kind}", $"unit: {unit}", "max_age: 60s"];
        AssertError(Base + Signal("extra", lines), "unit_not_allowed_for_kind", "signals.extra.unit");
    }

    [Fact]
    public void A_state_signal_has_no_unit()
    {
        AssertError(Base + Signal("car", "entity: binary_sensor.car", "kind: state", "unit: A", "max_age: 60s"), "unit_not_allowed_for_kind", "signals.car.unit");
    }

    [Fact]
    public void Current_and_power_need_a_sign_convention()
    {
        AssertError(Base.Replace("    sign: positive_import\n", string.Empty), "sign_required", "signals.grid_current.sign");
    }

    [Fact]
    public void Voltage_has_no_sign_convention()
    {
        AssertError(Base + Signal("v", "entity: sensor.v", "kind: voltage", "unit: V", "sign: positive_import", "max_age: 60s"), "sign_not_allowed_for_kind", "signals.v.sign");
    }

    [Fact]
    public void Unsigned_signal_needs_direction_from()
    {
        AssertError(Base.Replace("sign: positive_import", "sign: unsigned"), "direction_from_required", "signals.grid_current.direction_from");
    }

    [Fact]
    public void Signed_signal_takes_no_direction_from()
    {
        AssertError(Base.Replace("    sign: positive_import\n", "    sign: positive_import\n    direction_from: grid_power\n"), "direction_from_not_allowed", "signals.grid_current.direction_from");
    }

    [Fact]
    public void Direction_from_must_name_a_declared_signal()
    {
        AssertError(UnsignedWith("ghost"), "direction_from_unknown_signal", "signals.grid_current.direction_from", 13);
    }

    [Fact]
    public void Direction_from_must_name_signed_power()
    {
        var yaml = UnsignedWith("grid_voltage") + Signal("grid_voltage", "entity: sensor.v", "kind: voltage", "unit: V", "max_age: 60s");
        AssertError(yaml, "direction_from_not_signed_power", "signals.grid_current.direction_from");

        var unsignedPower = UnsignedWith("grid_power") + Signal("grid_power", "entity: sensor.p", "kind: power", "unit: W", "sign: unsigned", "direction_from: none", "max_age: 60s");
        AssertError(unsignedPower, "direction_from_not_signed_power", "signals.grid_current.direction_from");
    }

    [Fact]
    public void Phase_current_direction_cannot_come_from_total_power()
    {
        // Recorded data: one phase imports while the total exports in about 10% of samples, some
        // above 5 A. A phase current signed by the total would read that import as export.
        var yaml = TestConfigurations.ThreePhase(extra: Signal("grid_power_total", "entity: sensor.meter_power", "kind: power", "unit: W", "sign: positive_export", "max_age: 90s"))
            .Replace("direction_from: grid_power_l2", "direction_from: grid_power_total", StringComparison.Ordinal);

        AssertError(yaml, "direction_from_phase_mismatch", "signals.grid_current_l2.direction_from");
    }

    [Fact]
    public void Phase_current_direction_cannot_come_from_another_phase()
    {
        var yaml = TestConfigurations.ThreePhase().Replace("direction_from: grid_power_l2", "direction_from: grid_power_l3", StringComparison.Ordinal);

        AssertError(yaml, "direction_from_phase_mismatch", "signals.grid_current_l2.direction_from");
    }

    [Fact]
    public void Inverter_phase_currents_may_take_direction_from_the_inverter_total()
    {
        TestConfigurations.Valid(TestConfigurations.ThreePhase());
    }

    [Fact]
    public void Role_must_match_the_kind()
    {
        AssertError(Base.Replace("role: phase_current", "role: phase_power"), "role_kind_mismatch", "signals.grid_current.role", 14);
    }

    [Fact]
    public void A_single_phase_connection_has_no_summed_signals()
    {
        AssertError(Base + Signal("total", "entity: sensor.t", "kind: current", "unit: A", "sign: positive_import", "max_age: 60s", "role: total_current"), "role_not_in_topology", "signals.total.role");
    }

    [Fact]
    public void Per_phase_role_needs_a_phase()
    {
        AssertError(Base.Replace("    phase: L1\n", string.Empty), "phase_required", "signals.grid_current.phase");
    }

    [Fact]
    public void A_signal_without_a_per_phase_role_has_no_phase()
    {
        AssertError(Base + Signal("v", "entity: sensor.v", "kind: voltage", "unit: V", "max_age: 60s", "phase: L1"), "phase_not_allowed", "signals.v.phase");
    }

    [Fact]
    public void Phase_must_exist_in_the_topology()
    {
        AssertError(Base.Replace("phase: L1", "phase: L2"), "phase_not_in_topology", "signals.grid_current.phase", 15);
    }

    [Fact]
    public void A_role_on_a_phase_is_filled_once()
    {
        AssertError(Base + Signal("grid_current_2", "entity: sensor.c2", "kind: current", "unit: A", "sign: positive_import", "max_age: 60s", "role: phase_current", "phase: L1"), "duplicate_role", "signals.grid_current_2.role");
    }

    [Fact]
    public void Three_phase_needs_a_current_on_every_phase()
    {
        var yaml = TestConfigurations.ThreePhase().Replace("role: phase_current\n    phase: L3", "phase: L3\n    role: phase_power", StringComparison.Ordinal);

        var result = TestConfigurations.Parse(yaml);

        Assert.Contains(result.Errors, e => e.Code == "required_signal_missing" && e.Message.Contains("L3", StringComparison.Ordinal));
    }

    [Fact]
    public void Summed_topology_needs_a_total_current()
    {
        AssertError(Base.Replace("topology: single_phase", "topology: three_phase_summed"), "required_signal_missing", "signals");
    }

    [Fact]
    public void Inverter_phase_currents_need_every_phase_and_an_inverter()
    {
        var partial = TestConfigurations.ThreePhase().Replace("role: inverter_phase_current\n    phase: L3", "phase: L3", StringComparison.Ordinal);
        AssertError(partial, "required_signal_missing", "signals");
    }

    [Fact]
    public void Reserve_must_leave_capacity_on_the_phase()
    {
        AssertError(Base.Replace("  voltage_v: 230\n", "  voltage_v: 230\n  reserve_w: { L1: 7360 }\n"), "reserve_out_of_range", "connection.reserve_w.L1", 7);
    }

    [Fact]
    public void Reserve_for_a_phase_the_connection_does_not_have_is_an_error()
    {
        AssertError(Base.Replace("  voltage_v: 230\n", "  voltage_v: 230\n  reserve_w: { L2: 1000 }\n"), "phase_not_in_topology", "connection.reserve_w.L2");
    }

    [Theory]
    [InlineData("sigma: 1", "kind: state", "key_not_allowed", "signals.car.sigma")]
    [InlineData("unavailable_as_zero: true", "kind: state", "key_not_allowed", "signals.car.unavailable_as_zero")]
    public void Keys_that_do_not_apply_to_a_signal_are_refused(string key, string kind, string code, string path)
    {
        AssertError(Base + Signal("car", "entity: binary_sensor.car", kind, "max_age: 60s", key), code, path);
    }

    [Fact]
    public void Reserved_sections_are_ignored_with_a_warning()
    {
        // devices: is read since Stage 2; circuit_groups: stays reserved until dispatch.
        var result = TestConfigurations.Valid(Base + "circuit_groups:\n  - id: carport\n");

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("reserved_key_ignored", "circuit_groups", 17), (warning.Code, warning.Path, warning.Line));
    }

    [Fact]
    public void Override_replaces_one_key_keeps_the_rest()
    {
        var result = TestConfigurations.Valid(Base + "control:\n  signals:\n    balance:\n      residual_threshold_w: 450\n");

        var signals = result.Configuration!.Control.Signals;
        Assert.Equal(450, signals.Balance.ResidualThreshold.Value);
        Assert.Equal(TimeSpan.FromSeconds(5), signals.Balance.MaxSkew);
        Assert.Equal(TimeSpan.FromSeconds(5), signals.Direction.MaxSkew);
    }

    [Fact]
    public void Override_of_a_key_the_defaults_do_not_have_is_an_error()
    {
        AssertError(Base + "control:\n  signals:\n    balance:\n      residual_treshold_w: 450\n", "unknown_key", "control.signals.balance.residual_treshold_w", 19);
    }

    [Fact]
    public void Override_values_are_validated_like_defaults()
    {
        AssertError(Base + "control:\n  signals:\n    freshness_probe:\n      after_fraction: 1.5\n", "out_of_range", "control.signals.freshness_probe.after_fraction", 19);
        AssertError(Base + "control:\n  signals:\n    direction:\n      pairing_wait: 10s\n", "invalid_value", "control.signals.direction.pairing_wait");
        AssertError(Base + "control:\n  dispatch:\n    start_hysteresis_w: -300\n", "invalid_value", "control.dispatch.start_hysteresis_w");
    }

    [Fact]
    public void Tier_bands_must_ascend_with_non_increasing_durations()
    {
        // A higher current may never be tolerated longer than a lower one.
        const string tiers = """
            control:
              breaker:
                tiers:
                  B:
                    - { up_to: 0.90, allowed: unlimited, reaction: may_add }
                    - { up_to: 1.13, allowed: 3m, reaction: reduce_next_cycle }
                    - { up_to: 1.25, allowed: 15m, reaction: reduce_ordinary_cycle }
                    - { up_to: null, allowed: 0s, reaction: shed_all_on_phase }

            """;
        AssertError(Base + tiers, "tier_bands_invalid", "control.breaker.tiers.B[2]", 22);

        AssertError(Base + tiers.Replace("up_to: 1.25", "up_to: 1.10").Replace("allowed: 15m", "allowed: 1m"), "tier_bands_invalid", "control.breaker.tiers.B[2]");
        AssertError(Base + tiers.Replace("up_to: null", "up_to: 2.0").Replace("allowed: 15m", "allowed: 1m"), "tier_bands_invalid", "control.breaker.tiers.B[3]");
    }

    private static string UnsignedWith(string direction) =>
        Base.Replace("    sign: positive_import\n", $"    sign: unsigned\n    direction_from: {direction}\n");

    private static string Signal(string name, params string[] lines) =>
        $"  {name}:\n" + string.Concat(lines.Select(l => $"    {l}\n"));

    private static void AssertError(string yaml, string code, string path, int? line = null)
    {
        var result = TestConfigurations.Parse(yaml.ReplaceLineEndings("\n"));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        var issue = result.Errors.FirstOrDefault(e => e.Code == code && e.Path == path);
        Assert.True(issue is not null, $"Expected {code} at {path}; got: {string.Join("; ", result.Errors)}");
        if (line is not null)
        {
            Assert.Equal(line, issue!.Line);
        }
    }
}
