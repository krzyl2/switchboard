using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// CONN-06 before the wizard completes: entities exist, are primary measurements (never a derived
/// sensor, 07-PITFALLS), report in the declared unit and device class, carry plausible values for
/// this connection, the phases sum to the total, and the detected refresh fits each max_age. Every
/// refusal names the exact draft field (UI-05). The passive self-test evidence is judged against the
/// draft's own declaration, so fixing a phase clears its mismatch.
/// </summary>
public sealed class SetupValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 7, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);

    public static DiscoverySnapshot Fixture(bool registry = true, int max = 5000)
    {
        static JsonElement Read(string file)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("tests", "fixtures", "discovery", "synthetic-house", file)));
            return document.RootElement.Clone();
        }

        return DiscoveryResponseParser.Parse(Read("states.json"), registry ? Read("entity_registry.json") : null, Read("device_registry.json"), Now, max);
    }

    [Fact]
    public void The_maintainers_installation_validates_without_errors()
    {
        var validation = Validate(SetupFixtures.MaintainerDraft());

        Assert.Empty(Errors(validation));
        Assert.True(validation.Valid);
    }

    [Fact]
    public void Missing_entity_is_refused_at_its_field()
    {
        var draft = WithCurrent(SetupFixtures.MaintainerDraft(), Phase.L2, "sensor.solaredge_m1_ac_current_x");

        var issue = Assert.Single(Errors(Validate(draft)));

        Assert.Equal((SetupIssueCodes.EntityNotFound, "meter.phases.L2.current.entity"), (issue.Code, issue.Field));
    }

    [Fact]
    public void Unit_or_device_class_mismatch_is_refused()
    {
        // A power entity picked for a current row, and a current row declared in mA while HA says A.
        var wrongClass = WithCurrent(SetupFixtures.MaintainerDraft(), Phase.L1, "sensor.solaredge_m1_ac_power_a");
        var draft = SetupFixtures.MaintainerDraft();
        var wrongUnit = draft with
        {
            Meter = draft.Meter with { Phases = draft.Meter.Phases with { L3 = draft.Meter.Phases.L3! with { Current = draft.Meter.Phases.L3.Current! with { Unit = "mA" } } } },
        };

        Assert.Contains(Errors(Validate(wrongClass)), i => i.Code == SetupIssueCodes.DeviceClassMismatch && i.Field == "meter.phases.L1.current.entity");
        Assert.Contains(Errors(Validate(wrongUnit)), i => i.Code == SetupIssueCodes.UnitMismatch && i.Field == "meter.phases.L3.current.unit");
    }

    [Fact]
    public void Implausible_current_for_breaker_is_refused()
    {
        // 61 A on a 25 A connection (2 × 25 A plausible): a wrong entity, or mA read as A.
        var readouts = Healthy();
        readouts.Set("sensor.solaredge_m1_ac_current_a", "61", "A", refresh: TimeSpan.FromSeconds(10));

        var issue = Assert.Single(Errors(Validate(SetupFixtures.MaintainerDraft(), readouts)));

        Assert.Equal((SetupIssueCodes.ValueImplausible, "meter.phases.L1.current.entity"), (issue.Code, issue.Field));
    }

    [Fact]
    public void Unavailable_phase_current_blocks_sleeping_inverter_with_unavailable_as_zero_does_not()
    {
        var readouts = Healthy();
        readouts.Set("sensor.solaredge_m1_ac_current_b", "unavailable", "A");

        var errors = Errors(Validate(SetupFixtures.MaintainerDraft(), readouts));

        // The asleep inverter (unavailable, counted as 0 W) is not an error; the meter current is.
        var issue = Assert.Single(errors);
        Assert.Equal((SetupIssueCodes.EntityUnavailable, "meter.phases.L2.current.entity"), (issue.Code, issue.Field));
    }

    [Fact]
    public void Sleeping_inverter_without_unavailable_as_zero_is_only_a_warning()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var awake = draft with { Sources = new SourcesDraft { Inverter = draft.Sources.Inverter! with { UnavailableAsZero = false } } };

        var validation = Validate(awake);

        Assert.Empty(Errors(validation));
        Assert.Contains(validation.Issues, i => i.Code == SetupIssueCodes.EntityUnavailable && i.Field == "sources.inverter.power.entity" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Phase_sum_off_by_more_than_tolerance_is_refused()
    {
        // The total reads 1500 W of import while the phases add up to 1500 W of export: a sign error.
        var readouts = Healthy();
        var draft = WithTotal(SetupFixtures.MaintainerDraft());
        for (var i = 0; i < 4; i++)
        {
            var at = Now - TimeSpan.FromSeconds(40 - (10 * i));
            readouts.Report("sensor.solaredge_m1_ac_power", "1500", "W", at);
            Report(readouts, at, -600, -400, -500, offset: TimeSpan.FromMilliseconds(5));
        }

        var validation = Validate(draft, readouts);

        Assert.Contains(Errors(validation), i => i.Code == SetupIssueCodes.PhaseSumMismatch && i.Field == "meter.total");
        Assert.Equal("failed", validation.PhaseSum.State);
    }

    [Fact]
    public void Phase_sum_passes_when_the_phases_add_up()
    {
        var readouts = Healthy();
        var draft = WithTotal(SetupFixtures.MaintainerDraft());
        for (var i = 0; i < 4; i++)
        {
            var at = Now - TimeSpan.FromSeconds(40 - (10 * i));
            readouts.Report("sensor.solaredge_m1_ac_power", "-1500", "W", at);
            Report(readouts, at, -600, -400, -500, offset: TimeSpan.FromMilliseconds(5));
        }

        Assert.Equal("passed", Validate(draft, readouts).PhaseSum.State);
    }

    [Fact]
    public void Phase_sum_is_skipped_not_passed_without_a_total()
    {
        var validation = Validate(SetupFixtures.MaintainerDraft());

        Assert.Equal(("skipped", "no_total"), (validation.PhaseSum.State, validation.PhaseSum.Reason));
        Assert.Contains(validation.Issues, i => i.Code == SetupIssueCodes.PhaseSumSkipped && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void One_lagging_poll_does_not_fail_the_phase_sum()
    {
        // The M1 lags its own power by a poll now and then: one sample off by 1.7 kW, the median holds.
        var readouts = Healthy();
        var draft = WithTotal(SetupFixtures.MaintainerDraft());
        for (var i = 0; i < 5; i++)
        {
            var at = Now - TimeSpan.FromSeconds(50 - (10 * i));
            readouts.Report("sensor.solaredge_m1_ac_power", i == 2 ? "-3200" : "-1500", "W", at);
            Report(readouts, at, -600, -400, -500, offset: TimeSpan.FromMilliseconds(5));
        }

        Assert.Equal("passed", Validate(draft, readouts).PhaseSum.State);
    }

    [Fact]
    public void Max_age_below_refresh_times_one_plus_lag_is_refused()
    {
        // A 30 s meter with one lagging poll must be allowed 60 s; 45 s would go stale between reports.
        var readouts = Healthy();
        readouts.Set("sensor.solaredge_m1_ac_current_a", "2.61", "A", refresh: TimeSpan.FromSeconds(30));
        var draft = SetupFixtures.MaintainerDraft();
        var tight = draft with
        {
            Meter = draft.Meter with { Phases = draft.Meter.Phases with { L1 = draft.Meter.Phases.L1! with { Current = draft.Meter.Phases.L1.Current! with { MaxAge = "45s" } } } },
        };

        var validation = Validate(tight, readouts);

        var issue = Assert.Single(Errors(validation));
        Assert.Equal((SetupIssueCodes.MaxAgeBelowRefresh, "meter.phases.L1.current.maxAge", "60"), (issue.Code, issue.Field, issue.Args["minimumS"]));
        Assert.Contains(validation.Refresh, r => r.Field == "meter.phases.L1.current" && r.State == "below_refresh" && r.SuggestedMaxAgeS == 180);
    }

    [Fact]
    public void Undetected_refresh_is_a_warning_and_reserve_uses_max_age()
    {
        var readouts = Healthy(detected: false);

        var validation = Validate(SetupFixtures.MaintainerDraft(), readouts);

        Assert.Empty(Errors(validation));
        Assert.Contains(validation.Issues, i => i.Code == SetupIssueCodes.RefreshNotDetected && i.Field == "meter.phases.L1.current.maxAge");
        Assert.All(validation.Reserve!.PerPhase, p => Assert.Equal((90.0, false), (p.RefreshS, p.RefreshDetected)));
    }

    [Fact]
    public void Runtime_signal_refresh_is_used_when_entity_already_mapped()
    {
        var readouts = Healthy(detected: false);
        var runtime = new Dictionary<EntityId, TimeSpan> { [EntityId.Parse("sensor.solaredge_m1_ac_current_a")] = TimeSpan.FromSeconds(10) };

        var validation = SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(readouts: readouts, runtime: runtime));

        Assert.Equal((10.0, true), (validation.Reserve!.PerPhase[0].RefreshS, validation.Reserve.PerPhase[0].RefreshDetected));
    }

    [Fact]
    public void HA_unreachable_refuses_save_entity_check_unavailable()
    {
        var validation = SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(unreachable: true, readouts: new FakeReadouts(_time)));

        Assert.Contains(Errors(validation), i => i.Code == SetupIssueCodes.EntityCheckUnavailable && i.Field == "meter.phases.L1.current.entity");
        Assert.False(validation.Valid);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("derivative")]
    [InlineData("statistics")]
    [InlineData("utility_meter")]
    [InlineData("integration")]
    [InlineData("min_max")]
    [InlineData("filter")]
    [InlineData("group")]
    [InlineData("powercalc")]
    [InlineData("history_stats")]
    [InlineData("trend")]
    [InlineData("threshold")]
    [InlineData("compensation")]
    public void Derived_platforms_are_refused_as_inputs(string platform)
    {
        var snapshot = Fixture();
        var entity = snapshot.Find(EntityId.Parse("sensor.solaredge_m1_ac_power_b"))!;
        var derived = snapshot with { Entities = snapshot.Entities.Replace(entity, entity with { Platform = platform }) };

        var issue = Assert.Single(Errors(SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(discovery: derived))));

        Assert.Equal((SetupIssueCodes.DerivedSensorRefused, "meter.phases.L2.power.entity", platform), (issue.Code, issue.Field, issue.Args["platform"]));
    }

    [Fact]
    public void Derived_state_entity_refused_derived_actuator_warned()
    {
        var snapshot = Fixture();
        var relay = snapshot.Find(EntityId.Parse("switch.grzalka_2"))!;
        var templated = snapshot with { Entities = snapshot.Entities.Replace(relay, relay with { Platform = "template" }) };
        var draft = SetupFixtures.MaintainerDraft();

        // Without a state entity the template switch is also the state input: refused.
        Assert.Contains(Errors(SetupValidator.Validate(draft, Context(discovery: templated))), i => i.Code == SetupIssueCodes.DerivedSensorRefused && i.Field == "devices[0].actuator");

        // With a primary state entity it is only the actuator: a warning.
        var withState = draft with { Devices = [draft.Devices[0] with { StateEntity = "switch.grzalka_1" }, .. draft.Devices.Skip(1)] };
        var validation = SetupValidator.Validate(withState, Context(discovery: templated));
        Assert.DoesNotContain(Errors(validation), i => i.Field == "devices[0].actuator");
        Assert.Contains(validation.Issues, i => i.Code == SetupIssueCodes.ActuatorDerived && i.Field == "devices[0].actuator" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Powercalc_draw_is_refused()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var estimated = draft with { Devices = [.. draft.Devices.Take(3), draft.Devices[3] with { Draw = draft.Devices[3].Draw! with { Power = "sensor.kettle_power" } }] };

        Assert.Contains(Errors(Validate(estimated)), i => i.Code == SetupIssueCodes.DerivedSensorRefused && i.Field == "devices[3].draw.power" && i.Args["platform"] == "powercalc");
    }

    [Fact]
    public void Entity_missing_from_registry_is_flagged_not_passed()
    {
        // A YAML template sensor without a unique_id is in no registry: its source cannot be verified.
        var draft = WithTotal(SetupFixtures.MaintainerDraft(), "sensor.house_load_yaml");

        var validation = Validate(draft);

        Assert.Contains(validation.Issues, i => i.Code == SetupIssueCodes.DerivedCheckUnavailable && i.Field == "meter.total.power.entity" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Registry_unavailable_is_a_warning_naming_unchecked_fields()
    {
        var validation = SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(discovery: Fixture(registry: false)));

        var issue = Assert.Single(validation.Issues, i => i.Code == SetupIssueCodes.DerivedCheckUnavailable);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("meter.phases.L1.current.entity", issue.Args["fields"].Split(','));
        Assert.Empty(Errors(validation));
    }

    [Fact]
    public void Entity_beyond_truncation_is_not_refused()
    {
        // The bound cut the discovery before the M1's voltages; a live readout confirms one exists.
        var truncated = Fixture(max: 5);
        Assert.True(truncated.Truncated);

        var validation = SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(discovery: truncated));

        Assert.DoesNotContain(validation.Issues, i => i.Code == SetupIssueCodes.EntityNotFound);
    }

    [Fact]
    public void Reserve_below_minimum_is_flagged_never_enforced()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var small = draft with { Connection = draft.Connection with { ReserveW = new PhaseValues { L1 = 400, L2 = 2500, L3 = 2500 } } };

        var validation = Validate(small);

        var issue = Assert.Single(validation.Issues, i => i.Code == SetupIssueCodes.ReserveBelowMinimum);
        Assert.Equal(("connection.reserveW.L1", IssueSeverity.Warning), (issue.Field, issue.Severity));
        Assert.True(validation.Valid);
    }

    [Fact]
    public void Summed_topology_requires_total_current_not_phase_rows()
    {
        // The summed topology limits the total current: total power alone is not enough.
        var draft = SetupFixtures.MaintainerDraft();
        var summed = draft with
        {
            Connection = draft.Connection with { Topology = "three_phase_summed" },
            Meter = draft.Meter with { Phases = new MeterPhases(), Total = new MeterTotal { Power = SetupFixtures.Signal("sensor.solaredge_m1_ac_power", "W") } },
        };

        var validation = Validate(summed);

        Assert.Contains(Errors(validation), i => i.Code == SetupIssueCodes.RequiredField && i.Field == "meter.total.current.entity");
        Assert.DoesNotContain(Errors(validation), i => i.Field?.StartsWith("meter.phases", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Self_test_mismatch_blocks_at_devices_i_phase()
    {
        // switch.grzalka_1 stepped meter phase B (L2) when switched on and off; a draft declaring
        // heater_l2 on L3 is refused there.
        var evidence = Evidence(("switch.grzalka_1", Phase.L2));
        var draft = SetupFixtures.MaintainerDraft();
        var swapped = draft with { Devices = [draft.Devices[0], draft.Devices[1] with { Phase = "L3" }, .. draft.Devices.Skip(2)] };

        var issue = Assert.Single(Errors(SetupValidator.Validate(swapped, Context(evidence: evidence))));

        Assert.Equal((SetupIssueCodes.PhaseSelfTestMismatch, "devices[1].phase", "L2", "L3"), (issue.Code, issue.Field, issue.Args["observed"], issue.Args["declared"]));
    }

    [Fact]
    public void One_coincident_recorded_mismatch_never_refuses_the_save()
    {
        // After a restart the evidence is empty; Node-RED switches heater_l2 off with its thermostat
        // open while an undeclared 1.7 kW load drops on L1 (the recording has this at 2026-10-23
        // 09:00:01). One observation is no verdict: following "fix the phase" would write the wrong
        // phase, so it is a warning that names what it saw and asks for a watch.
        var evidence = new PhaseEvidenceStore(20);
        evidence.Add(Observation("switch.grzalka_1", Phase.L1, -1));

        var validation = SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(evidence: evidence));

        Assert.Empty(Errors(validation));
        var issue = Assert.Single(validation.Issues, i => i.Code == SetupIssueCodes.PhaseSelfTestMissing && i.Field == "devices[1].phase");
        Assert.Equal((IssueSeverity.Warning, "unconfirmed", "L1", "L2"), (issue.Severity, issue.Args["reason"], issue.Args["observed"], issue.Args["declared"]));
        Assert.DoesNotContain("Fix the phase", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Draw_in_kilowatts_without_power_unit_is_refused_at_the_draw()
    {
        // A charger whose power sensor reports kW, declared without power_unit, would be read as W:
        // every runtime sample is then a unit mismatch and the draw is never trusted (04 §8).
        var readouts = Healthy();
        readouts.Set("sensor.goe_422752_nrg_7", "0.0", "kW", Now - TimeSpan.FromHours(5));
        var draft = SetupFixtures.MaintainerDraft();

        var issue = Assert.Single(Errors(Validate(draft, readouts)));
        var declared = draft with { Devices = [.. draft.Devices.Take(3), draft.Devices[3] with { Draw = draft.Devices[3].Draw! with { PowerUnit = "kW" } }] };

        Assert.Equal((SetupIssueCodes.UnitMismatch, "devices[3].draw.power", "kW"), (issue.Code, issue.Field, issue.Args["unit"]));
        Assert.Empty(Errors(Validate(declared, readouts)));
    }

    [Fact]
    public void Derived_sensor_known_only_from_a_readout_is_never_silent()
    {
        // Discovery failed (too large, timed out) but the live readout finds the entity the user
        // typed: whether it is a template sensor cannot be checked, and the warning says so.
        var readouts = Healthy();
        readouts.Set("sensor.usage", "1500.0", "W", Now - TimeSpan.FromSeconds(3));
        var draft = WithTotal(SetupFixtures.MaintainerDraft(), "sensor.usage");

        var validation = SetupValidator.Validate(draft, Context(unreachable: true, readouts: readouts));

        var issue = Assert.Single(validation.Issues, i => i.Code == SetupIssueCodes.DerivedCheckUnavailable);
        Assert.Contains("meter.total.power.entity", issue.Args["fields"].Split(','));
        Assert.Equal("home_assistant_unavailable", issue.Args["reason"]);
    }

    [Fact]
    public void Derived_sensor_past_the_discovery_bound_is_still_refused()
    {
        // The bound cut the entity list before sensor.usage; the registry's platform index is whole.
        var truncated = Fixture(max: 5);
        Assert.Null(truncated.Find(EntityId.Parse("sensor.usage")));
        var readouts = Healthy();
        readouts.Set("sensor.usage", "1500.0", "W", Now - TimeSpan.FromSeconds(3));

        var validation = SetupValidator.Validate(WithTotal(SetupFixtures.MaintainerDraft(), "sensor.usage"), Context(discovery: truncated, readouts: readouts));

        Assert.Contains(Errors(validation), i => i.Code == SetupIssueCodes.DerivedSensorRefused && i.Field == "meter.total.power.entity" && i.Args["platform"] == "template");
    }

    [Fact]
    public void Single_phase_checks_only_the_rows_it_shows()
    {
        // L2/L3 rows left over from a three-phase start (one of them pointing at a missing entity)
        // are neither written nor checked once the topology is single_phase.
        var draft = WithCurrent(SetupFixtures.MaintainerDraft(), Phase.L2, "sensor.does_not_exist");
        var single = draft with { Connection = draft.Connection with { Topology = "single_phase" }, Sources = new SourcesDraft(), Devices = [draft.Devices[0]] };

        var validation = Validate(single);

        Assert.Empty(Errors(validation));
        Assert.DoesNotContain(validation.Issues, i => i.Field?.StartsWith("meter.phases.L2", StringComparison.Ordinal) == true || i.Field?.StartsWith("meter.phases.L3", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Missing_reserve_counts_as_the_default_reserve_like_the_runtime()
    {
        // A valid file without reserve_w gets control.connection.default_reserve_w from the parser;
        // the wizard must not warn that 0 W is below the minimum.
        var draft = SetupFixtures.MaintainerDraft();
        var none = draft with { Connection = draft.Connection with { ReserveW = null } };

        var validation = Validate(none);

        Assert.DoesNotContain(validation.Issues, i => i.Code == SetupIssueCodes.ReserveBelowMinimum);
        Assert.All(validation.Reserve!.PerPhase, p => Assert.Equal(SetupFixtures.Control.Connection.DefaultReserve.Value, p.ReserveW));
    }

    [Fact]
    public void Declared_sigma_raises_the_minimum_safe_reserve_like_the_runtime()
    {
        // SignalStore reserves with the signal's own sigma: 3 x (0.5 - 0.1) A x 230 V more than with
        // the default, so a 700 W reserve is flagged.
        var draft = SetupFixtures.MaintainerDraft();
        var small = draft with { Connection = draft.Connection with { ReserveW = new PhaseValues { L1 = 700, L2 = 2500, L3 = 2500 } } };
        var context = Context(readouts: Healthy());
        var declared = context with { DeclaredSigma = new Dictionary<EntityId, double> { [EntityId.Parse("sensor.solaredge_m1_ac_current_a")] = 0.5 } };

        var plain = SetupValidator.Validate(small, context);
        var noisy = SetupValidator.Validate(small, declared);

        Assert.DoesNotContain(plain.Issues, i => i.Code == SetupIssueCodes.ReserveBelowMinimum);
        Assert.Contains(noisy.Issues, i => i.Code == SetupIssueCodes.ReserveBelowMinimum && i.Field == "connection.reserveW.L1");
        Assert.Equal(SetupFixtures.Control.ReserveDerivation.SigmaMultiplier * (0.5 - 0.1) * 230, noisy.Reserve!.PerPhase[0].MinimumSafeW - plain.Reserve!.PerPhase[0].MinimumSafeW, 6);
    }

    [Fact]
    public void Meter_max_age_far_above_the_refresh_is_flagged_with_the_suggestion()
    {
        // CONN-06 both ways: 30 min on a 10 s meter keeps trusting a frozen poll for half an hour.
        var draft = SetupFixtures.MaintainerDraft();
        var loose = draft with
        {
            Meter = draft.Meter with { Phases = draft.Meter.Phases with { L1 = draft.Meter.Phases.L1! with { Current = draft.Meter.Phases.L1.Current! with { MaxAge = "30m" } } } },
        };

        var validation = Validate(loose);

        var issue = Assert.Single(validation.Issues, i => i.Code == SetupIssueCodes.MaxAgeAboveRefresh);
        Assert.Equal(("meter.phases.L1.current.maxAge", IssueSeverity.Warning, "60"), (issue.Field, issue.Severity, issue.Args["suggestedS"]));
        Assert.Contains(validation.Refresh, r => r.Field == "meter.phases.L1.current" && r.State == "above_refresh" && r.SuggestedMaxAgeS == 60);
        Assert.DoesNotContain(Validate(draft).Issues, i => i.Code == SetupIssueCodes.MaxAgeAboveRefresh);
    }

    [Fact]
    public void Fixing_the_phase_in_the_draft_clears_the_recorded_mismatch()
    {
        var evidence = Evidence(("switch.grzalka_1", Phase.L2));

        var validation = SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(evidence: evidence));

        Assert.Empty(Errors(validation));
        Assert.Contains(validation.SelfTest, s => s.Field == "devices[1].phase" && s.Judgement.Summary.Verdict == PhaseStepVerdict.Confirmed);
    }

    [Fact]
    public void Evidence_from_another_meter_mapping_is_ignored()
    {
        // Measured while the meter's phase entities were mapped differently: it proves nothing now.
        var evidence = Evidence(("switch.grzalka_1", Phase.L3), meterSwap: true);
        var validation = SetupValidator.Validate(SetupFixtures.MaintainerDraft(), Context(evidence: evidence));

        Assert.Empty(Errors(validation));
        Assert.Contains(validation.Issues, i => i.Code == SetupIssueCodes.PhaseSelfTestMissing && i.Field == "devices[1].phase");
    }

    [Fact]
    public void Inconclusive_or_missing_self_test_is_a_warning()
    {
        var validation = Validate(SetupFixtures.MaintainerDraft());

        Assert.Equal(3, validation.Issues.Count(i => i.Code == SetupIssueCodes.PhaseSelfTestMissing && i.Severity == IssueSeverity.Warning));
        Assert.True(validation.Valid);
    }

    [Fact]
    public void Age_based_state_older_than_state_max_age_suggests_on_change()
    {
        // The Modbus heater lesson: a relay that reports only on change goes stale under an age limit.
        var readouts = Healthy();
        readouts.Set("switch.grzalka_2", "off", reported: Now - TimeSpan.FromHours(3));
        var draft = SetupFixtures.MaintainerDraft();
        var aged = draft with { Devices = [draft.Devices[0] with { StateReports = null }, .. draft.Devices.Skip(1)] };

        var issue = Assert.Single(Validate(aged, readouts).Issues, i => i.Code == SetupIssueCodes.MaxAgeBelowRefresh);

        Assert.Equal(("devices[0].stateReports", IssueSeverity.Warning, "on_change"), (issue.Field, issue.Severity, issue.Args["suggest"]));
    }

    [Fact]
    public void Direction_from_phase_power_needs_every_phase_power()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var noPower = draft with { Meter = draft.Meter with { Phases = draft.Meter.Phases with { L3 = draft.Meter.Phases.L3! with { Power = null } } } };

        Assert.Contains(Errors(Validate(noPower)), i => i.Code == SetupIssueCodes.RequiredField && i.Field == "meter.currentDirection" && i.Args["phases"] == "L3");
    }

    private static SetupDraft WithCurrent(SetupDraft draft, Phase phase, string entity)
    {
        var rows = draft.Meter.Phases;
        MeterPhaseDraft Swap(MeterPhaseDraft row) => row with { Current = row.Current! with { Entity = entity } };
        return draft with
        {
            Meter = draft.Meter with
            {
                Phases = phase switch
                {
                    Phase.L1 => rows with { L1 = Swap(rows.L1!) },
                    Phase.L2 => rows with { L2 = Swap(rows.L2!) },
                    _ => rows with { L3 = Swap(rows.L3!) },
                },
            },
        };
    }

    private static SetupDraft WithTotal(SetupDraft draft, string entity = "sensor.solaredge_m1_ac_power") =>
        draft with { Meter = draft.Meter with { Total = new MeterTotal { Power = SetupFixtures.Signal(entity, "W") } } };

    private static void Report(FakeReadouts readouts, DateTimeOffset at, double a, double b, double c, TimeSpan offset)
    {
        readouts.Report("sensor.solaredge_m1_ac_power_a", a.ToString(System.Globalization.CultureInfo.InvariantCulture), "W", at + offset);
        readouts.Report("sensor.solaredge_m1_ac_power_b", b.ToString(System.Globalization.CultureInfo.InvariantCulture), "W", at + offset);
        readouts.Report("sensor.solaredge_m1_ac_power_c", c.ToString(System.Globalization.CultureInfo.InvariantCulture), "W", at + offset);
    }

    /// <summary>Every entity of the maintainer's draft reporting as the synthetic morning does at 07:00, the meter every 10 s.</summary>
    private FakeReadouts Healthy(bool detected = true)
    {
        TimeSpan? interval = detected ? TimeSpan.FromSeconds(10) : null;
        var readouts = new FakeReadouts(_time);
        foreach (var (letter, amps, watts) in new[] { ("a", "2.61", "-600"), ("b", "1.74", "-400"), ("c", "2.17", "-500") })
        {
            readouts.Set($"sensor.solaredge_m1_ac_current_{letter}", amps, "A", Now - TimeSpan.FromSeconds(3), interval);
            readouts.Set($"sensor.solaredge_m1_ac_power_{letter}", watts, "W", Now - TimeSpan.FromSeconds(3), interval);
            readouts.Set($"sensor.solaredge_m1_ac_voltage_{letter}n", "231.2", "V", Now - TimeSpan.FromSeconds(3), interval);
            readouts.Set($"sensor.solaredge_ac_current_{letter}", "unavailable", "A", Now - TimeSpan.FromHours(10));
        }

        readouts.Set("sensor.solaredge_ac_power", "unavailable", "W", Now - TimeSpan.FromHours(10));
        foreach (var relay in new[] { "switch.grzalka_1", "switch.grzalka_2", "switch.grzalka_3" })
        {
            readouts.Set(relay, "off", null, Now - TimeSpan.FromHours(5));
        }

        readouts.Set("number.goe_422752_amp", "20", "A", Now - TimeSpan.FromHours(5));
        readouts.Set("sensor.goe_422752_nrg_4", "0.0", "A", Now - TimeSpan.FromHours(5));
        readouts.Set("sensor.goe_422752_nrg_7", "0", "W", Now - TimeSpan.FromHours(5));
        return readouts;
    }

    /// <summary>One clean switching on and one off of the state entity, stepping the given phase by 1.7 kW, measured with the maintainer's meter mapping.</summary>
    private static PhaseEvidenceStore Evidence((string Entity, Phase Stepped) observation, bool meterSwap = false)
    {
        var store = new PhaseEvidenceStore(20);
        store.Add(Observation(observation.Entity, observation.Stepped, 1, meterSwap));
        store.Add(Observation(observation.Entity, observation.Stepped, -1, meterSwap));
        return store;
    }

    /// <summary>One clean recorded switching of the state entity that steps only <paramref name="stepped"/> by 1.7 kW in its direction.</summary>
    private static PhaseObservation Observation(string entity, Phase stepped, int direction, bool meterSwap = false)
    {
        var terms = new Dictionary<Phase, MeterTerm>
        {
            [Phase.L1] = new(EntityId.Parse("sensor.solaredge_m1_ac_power_a"), "W", "positive_export"),
            [Phase.L2] = new(EntityId.Parse(meterSwap ? "sensor.solaredge_m1_ac_power_c" : "sensor.solaredge_m1_ac_power_b"), "W", "positive_export"),
            [Phase.L3] = new(EntityId.Parse(meterSwap ? "sensor.solaredge_m1_ac_power_b" : "sensor.solaredge_m1_ac_power_c"), "W", "positive_export"),
        }.ToImmutableDictionary();
        var phases = new[] { Phase.L1, Phase.L2, Phase.L3 }.ToImmutableDictionary(
            p => p,
            p => new PhaseStepMeasurement(2200, 2200, p == stepped ? 2200 + (direction * 1700) : 2210, 25));
        return new PhaseObservation(Now - TimeSpan.FromMinutes(direction > 0 ? 10 : 5), direction, EntityId.Parse(entity), ObservationSource.Recorded, "W", false, true, terms, phases, true, null);
    }

    private SetupValidation Validate(SetupDraft draft, FakeReadouts? readouts = null) =>
        SetupValidator.Validate(draft, Context(readouts: readouts ?? Healthy()));

    private SetupContext Context(DiscoverySnapshot? discovery = null, bool unreachable = false, FakeReadouts? readouts = null, PhaseEvidenceStore? evidence = null, Dictionary<EntityId, TimeSpan>? runtime = null) =>
        new(
            unreachable ? null : discovery ?? Fixture(),
            readouts ?? Healthy(),
            runtime ?? [],
            SetupFixtures.Defaults,
            SetupFixtures.Control,
            evidence ?? new PhaseEvidenceStore(20),
            Now);

    private static List<SetupIssue> Errors(SetupValidation validation) => [.. validation.Issues.Where(i => i.Severity == IssueSeverity.Error)];
}
