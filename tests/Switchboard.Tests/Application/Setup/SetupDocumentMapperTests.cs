using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// UI-06: the configuration persists and stays editable, and re-running the wizard never loses an
/// existing device, signal, circuit group or control override. The wizard edits switchboard.yaml
/// through the parsed tree: what it does not own is kept exactly, signals are found by role and
/// phase (never by name), and the written file parses back to the same tree and configuration.
/// </summary>
public sealed class SetupDocumentMapperTests
{
    /// <summary>The live example plus a circuit group and a control override the wizard does not edit.</summary>
    private static string Rich => DispatchSyntheticReplayRun.Configuration.Replace(
        "\n# control: overrides",
        "\ncontrol:\n  signals:\n    balance:\n      residual_threshold_w: 400\n\n# control: overrides",
        StringComparison.Ordinal);

    [Fact]
    public void Example_round_trips_keeping_every_device_signal_group_and_control_override()
    {
        var original = TestConfigurations.Valid(Rich).Configuration!;
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich));

        var (_, result, _) = SetupFixtures.Apply(Rich, load.Draft);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        var written = result.Configuration!;
        Assert.Equal(original.Signals.Select(s => s.ToString()), written.Signals.Select(s => s.ToString()));
        Assert.Equal(original.Devices.Select(d => (d.Id, d.Name, d.Type, d.RotationGroup, d.CircuitGroup, d.StateReports)), written.Devices.Select(d => (d.Id, d.Name, d.Type, d.RotationGroup, d.CircuitGroup, d.StateReports)));
        Assert.Equal(original.Groups.Select(g => g.Id), written.Groups.Select(g => g.Id));
        Assert.Equal(400, written.Control.Signals.Balance.ResidualThreshold.Value);
        Assert.Contains(load.Preserved, p => p.Path == "circuit_groups");
        Assert.Contains(load.Preserved, p => p.Path == "control");
    }

    [Fact]
    public void Select_options_round_trip_and_keep_the_files_spelling()
    {
        // 0.7.1: the wizard reads and writes stop_option and resume_option; an option the file wrote
        // as a plain 1 stays exactly as written, a new one is written quoted so it reads back as text.
        var yaml = Rich.Replace("stop_option: \"1\"", "stop_option: 1", StringComparison.Ordinal);
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml)).Draft;
        var charger = draft.Devices.Single(d => d.Id == "ev_charger");
        Assert.Equal(("select.goe_422752_frc", "1", "0"), (charger.StopActuator, charger.StopOption, charger.ResumeOption));

        var (unchanged, result, _) = SetupFixtures.Apply(yaml, draft);
        Assert.Contains("stop_option: 1\n", unchanged.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(("1", "0"), (result.Configuration!.Devices.Single(d => d.Id.Value == "ev_charger").StopOptions!.StopOption.Value, result.Configuration.Devices.Single(d => d.Id.Value == "ev_charger").StopOptions!.ResumeOption.Value));

        var edited = draft with { Devices = [.. draft.Devices.Select(d => d.Id == "ev_charger" ? d with { StopOption = "2", ResumeOption = "0" } : d)] };
        var (text, reparsed, _) = SetupFixtures.Apply(yaml, edited);
        Assert.Contains("stop_option: \"2\"", text, StringComparison.Ordinal);
        Assert.Equal("2", reparsed.Configuration!.Devices.Single(d => d.Id.Value == "ev_charger").StopOptions!.StopOption.Value);
    }

    [Fact]
    public void Switch_stop_removes_options()
    {
        // The options belong to a select stop only: picking a switch stop (or none) removes them, so
        // the parser never sees an option next to a switch.
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var toSwitch = draft with { Devices = [.. draft.Devices.Select(d => d.Id == "ev_charger" ? d with { StopActuator = "switch.goe_422752_charging" } : d)] };
        var toNone = draft with { Devices = [.. draft.Devices.Select(d => d.Id == "ev_charger" ? d with { StopActuator = null } : d)] };

        var (switchText, switchResult, _) = SetupFixtures.Apply(Rich, toSwitch);
        var (noneText, noneResult, _) = SetupFixtures.Apply(Rich, toNone);

        Assert.DoesNotContain("stop_option", switchText, StringComparison.Ordinal);
        Assert.DoesNotContain("resume_option", switchText, StringComparison.Ordinal);
        Assert.Equal(ConfigurationStatus.Valid, switchResult.Status);
        Assert.DoesNotContain("stop_", noneText, StringComparison.Ordinal);
        Assert.Equal(ConfigurationStatus.Valid, noneResult.Status);
    }

    [Fact]
    public void Written_document_reparses_to_same_tree_and_configuration()
    {
        var tree = SetupFixtures.Tree(Rich);
        var draft = SetupDocumentMapper.Read(tree).Draft;

        var (text, _, mapped) = SetupFixtures.Apply(Rich, draft);

        // An unchanged draft changes nothing: the written file is the same tree, key for key.
        Assert.Equal(SetupFixtures.Canon(tree), SetupFixtures.Canon(SetupFixtures.Tree(text)));
        Assert.Equal(SetupFixtures.Canon(mapped.Root), SetupFixtures.Canon(SetupFixtures.Tree(text)));
    }

    [Fact]
    public void Unedited_keys_preserved_verbatim()
    {
        // sigma, the unedited sign of the inverter currents, 7.0 written as 7.0, the group.
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var edited = draft with { Devices = [draft.Devices[0] with { Name = "Grzałka kuchnia" }, .. draft.Devices.Skip(1)] };

        var (text, result, _) = SetupFixtures.Apply(Rich, edited);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Contains("    sigma: 0.1\n", text, StringComparison.Ordinal);
        Assert.Contains("    rated_current_a: 7.0\n", text, StringComparison.Ordinal);
        Assert.Contains("  - id: carport\n", text, StringComparison.Ordinal);
        Assert.Contains("      residual_threshold_w: 400\n", text, StringComparison.Ordinal);
        Assert.Equal("Grzałka kuchnia", result.Configuration!.Devices[0].Name);
        Assert.Equal("Grzałka CWU L2", result.Configuration.Devices[1].Name);
    }

    [Fact]
    public void Signals_found_by_role_and_phase_not_name()
    {
        // A hand-written file names its signals freely; the wizard edits them where they are.
        var renamed = Rich.Replace("grid_current_l1", "meter_a_amps", StringComparison.Ordinal);
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(renamed)).Draft;
        var moved = draft with
        {
            Meter = draft.Meter with { Phases = draft.Meter.Phases with { L1 = draft.Meter.Phases.L1! with { Current = draft.Meter.Phases.L1.Current! with { MaxAge = "60s" } } } },
        };

        var (text, result, _) = SetupFixtures.Apply(renamed, moved);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        var signal = result.Configuration!.Signals.Single(s => s.Role == SignalRole.PhaseCurrent && s.Phase == Phase.L1);
        Assert.Equal(("meter_a_amps", TimeSpan.FromSeconds(60)), (signal.Name.Value, signal.MaxAge));
        Assert.DoesNotContain("grid_current_l1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_signal_takes_the_example_name_with_a_suffix_on_a_collision()
    {
        var yaml = Rich.Replace("  grid_voltage_l1:\n", "  grid_total_power:\n", StringComparison.Ordinal);
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml)).Draft;
        var withTotal = draft with { Meter = draft.Meter with { Total = new MeterTotal { Power = SetupFixtures.Signal("sensor.solaredge_m1_ac_power", "W") } } };

        var (_, result, _) = SetupFixtures.Apply(yaml, withTotal);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal("grid_total_power_2", result.Configuration!.Signals.Single(s => s.Role == SignalRole.TotalPower).Name.Value);
    }

    [Fact]
    public void Existing_device_type_is_immutable()
    {
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var retyped = draft with { Devices = [draft.Devices[0] with { Type = "modulating" }, .. draft.Devices.Skip(1)] };

        var (_, result, mapped) = SetupFixtures.Apply(Rich, retyped);

        Assert.Contains(mapped.Issues, i => i.Code == SetupIssueCodes.ExistingDeviceImmutable && i.Field == "devices[0].type");
        Assert.Equal(Switchboard.Domain.Devices.DeviceType.OnOff, result.Configuration!.Devices[0].Type);
    }

    [Fact]
    public void A_device_claiming_to_exist_is_new_when_the_file_does_not_have_it()
    {
        // The server decides existing and editable from the file by id: a claim never makes a device
        // existing (and so never lets it keep keys of an entry that is not there).
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var claims = draft with
        {
            Devices = [.. draft.Devices, SetupFixtures.Heater("heater_extra", "switch.extra_relay", "L3") with { Existing = true, Editable = false, RotationGroup = null }],
        };

        var (_, result, mapped) = SetupFixtures.Apply(Rich, claims);

        Assert.Empty(mapped.Issues);
        Assert.Equal(["heater_l1", "heater_l2", "heater_l3", "ev_charger", "heater_extra"], result.Configuration!.Devices.Select(d => d.Id.Value));
    }

    [Fact]
    public void A_new_device_reusing_a_removed_device_id_is_refused_at_its_id()
    {
        // Remove the charger, add the EV-charger template, which suggests the id ev_charger: merged
        // into the removed entry, the "new" charger would silently keep keys of the old one (its
        // select stop and options from the example, a shed priority, a draw unit), an actuator the
        // user believes is gone.
        var yaml = Rich.Replace("    actuator: number.goe_422752_amp\n", "    actuator: number.goe_422752_amp\n    shed_priority: 1\n", StringComparison.Ordinal);
        Assert.Contains("stop_actuator: select.goe_422752_frc", yaml, StringComparison.Ordinal);
        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(yaml).Status);
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml)).Draft;
        var template = new DeviceDraft { Id = "ev_charger", Template = "ev_charger", Type = "modulating", Actuator = "number.other_charger_amp", Phase = "L1", Min = 6, Max = 16, Step = 1, ValuePerKwh = 1 };
        var readded = draft with { Devices = [.. draft.Devices.Where(d => d.Id != "ev_charger"), template] };

        var (text, _, mapped) = SetupFixtures.Apply(yaml, readded);

        var issue = Assert.Single(mapped.Issues);
        Assert.Equal((SetupIssueCodes.DeviceIdTaken, "devices[3].id", "ev_charger"), (issue.Code, issue.Field, issue.Args["device"]));
        Assert.DoesNotContain("stop_actuator", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stop_option", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Kept_as_written_device_is_not_resurrected_by_a_new_device_with_its_id()
    {
        // A stepped device (kept as written) removed and a relay added under its id: the old entry
        // must not come back while step 5 lists the device as new.
        var yaml = Rich.Replace("\ncircuit_groups:", "\n  - id: heat_pump\n    type: stepped\n    stages:\n      - { actuator: switch.hp_stage_1, phase: L2, rated_current_a: 5 }\n\ncircuit_groups:", StringComparison.Ordinal);
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml)).Draft;
        Assert.Contains(draft.Devices, d => d.Id == "heat_pump" && !d.Editable);
        var readded = draft with { Devices = [.. draft.Devices.Where(d => d.Id != "heat_pump"), SetupFixtures.Heater("heat_pump", "switch.hp_relay", "L2") with { RotationGroup = null }] };

        var (text, _, mapped) = SetupFixtures.Apply(yaml, readded);

        Assert.Contains(mapped.Issues, i => i.Code == SetupIssueCodes.DeviceIdTaken && i.Field == "devices[4].id");
        Assert.DoesNotContain("switch.hp_stage_1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Draw_unit_follows_the_picked_entity()
    {
        // A charger power sensor in kW read as W would make every draw sample a unit mismatch at run
        // time: the unit is written when it is not the default and removed when it is.
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var charger = draft.Devices[3];
        Assert.Null(charger.Draw!.PowerUnit);
        var kilowatts = draft with { Devices = [.. draft.Devices.Take(3), charger with { Draw = charger.Draw with { Power = "sensor.charger_kw", PowerUnit = "kW", CurrentUnit = "A" } }] };

        var (text, result, _) = SetupFixtures.Apply(Rich, kilowatts);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Contains("      power_unit: kW\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("current_unit", text, StringComparison.Ordinal);
        var reread = SetupDocumentMapper.Read(SetupFixtures.Tree(text)).Draft.Devices[3].Draw!;
        Assert.Equal(("kW", (string?)null), (reread.PowerUnit, reread.CurrentUnit));

        // Back to a W sensor: the kW key of the file is removed, never kept as written.
        var watts = draft with { Devices = [.. draft.Devices.Take(3), charger with { Draw = charger.Draw with { PowerUnit = "W" } }] };
        var (back, _, _) = SetupFixtures.Apply(text, watts);
        Assert.DoesNotContain("power_unit", back, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_phase_writes_only_L1_rows_and_reserve()
    {
        // The user mapped L1-L3 and their reserves, then chose single_phase: the wizard shows L1 only,
        // so L2/L3 are not written (and never refused at fields the user cannot see).
        var draft = SetupFixtures.MaintainerDraft();
        var single = draft with
        {
            Connection = draft.Connection with { Topology = "single_phase" },
            Sources = new SourcesDraft(),
            Devices = [draft.Devices[0] with { RotationGroup = null }],
        };

        var (text, result, mapped) = SetupFixtures.Apply(null, single);

        Assert.True(result.Status == ConfigurationStatus.Valid, string.Join("; ", result.Errors));
        Assert.Empty(mapped.Issues);
        Assert.DoesNotContain("_l2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_l3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("    L2:", text, StringComparison.Ordinal);
        Assert.Equal([Phase.L1], result.Configuration!.Signals.Where(s => s.Role == SignalRole.PhaseCurrent).Select(s => s.Phase!.Value));
    }

    [Fact]
    public void Removing_a_device_removes_only_that_device()
    {
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var (_, result, _) = SetupFixtures.Apply(Rich, draft with { Devices = [.. draft.Devices.Where(d => d.Id != "heater_l3")] });

        Assert.Equal(["heater_l1", "heater_l2", "ev_charger"], result.Configuration!.Devices.Select(d => d.Id.Value));
    }

    [Fact]
    public void Cleared_optional_field_removes_key()
    {
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var cleared = draft with { Devices = [draft.Devices[0] with { Name = null, RotationGroup = null }, .. draft.Devices.Skip(1)] };

        var (text, result, _) = SetupFixtures.Apply(Rich, cleared);

        Assert.Null(result.Configuration!.Devices[0].Name);
        Assert.Null(result.Configuration.Devices[0].RotationGroup);
        Assert.DoesNotContain("name: Grzałka CWU L1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Quoted_scalars_stay_strings()
    {
        // A name that a YAML reader would take for a boolean or a number is written quoted.
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;
        var names = draft with
        {
            Devices = [draft.Devices[0] with { Name = "on" }, draft.Devices[1] with { Name = "0.35" }, draft.Devices[2] with { Name = "a: b # c" }, draft.Devices[3]],
        };

        var (text, result, _) = SetupFixtures.Apply(Rich, names);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal(["on", "0.35", "a: b # c"], result.Configuration!.Devices.Take(3).Select(d => d.Name));
        Assert.Contains("name: \"on\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Setting_on_change_removes_state_max_age()
    {
        // state_max_age next to on_change is refused by the parser at a key the wizard would not own.
        var yaml = Rich.Replace("    state_reports: on_change\n    rotation_group: cwu_heaters\n  - id: heater_l2", "    state_max_age: 24h\n    rotation_group: cwu_heaters\n  - id: heater_l2", StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromHours(24), TestConfigurations.Valid(yaml).Configuration!.Devices[0].StateMaxAge);
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml)).Draft;

        var (text, result, _) = SetupFixtures.Apply(yaml, draft with { Devices = [draft.Devices[0] with { StateReports = "on_change" }, .. draft.Devices.Skip(1)] });

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.DoesNotContain("state_max_age", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Kept_as_written_device_content_ignores_draft_edits()
    {
        var yaml = Rich.Replace("\ndevices:\n", "\ndevices:\n  - id: boiler\n    type: stepped\n    value_per_kwh: 0.3\n    stages:\n      - { actuator: switch.boiler_1, phase: L1, rated_current_a: 4, rated_power_w: 900 }\n      - { actuator: switch.boiler_2, phase: L1, rated_current_a: 4, rated_power_w: 900 }\n", StringComparison.Ordinal);
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml));
        Assert.False(load.Draft.Devices[0].Editable);
        var edited = load.Draft with { Devices = [load.Draft.Devices[0] with { Name = "changed", ValuePerKwh = 9 }, .. load.Draft.Devices.Skip(1)] };

        var (_, result, _) = SetupFixtures.Apply(yaml, edited);

        var boiler = result.Configuration!.Devices[0];
        Assert.Equal((null, 0.3, 2), (boiler.Name, boiler.ValuePerKwh, boiler.Stages.Length));
    }

    [Theory]
    [InlineData("three_phase")]
    [InlineData("three_phase_summed")]
    [InlineData("single_phase")]
    public void Topology_choices_round_trip(string topology)
    {
        var draft = SetupFixtures.MaintainerDraft() with { Devices = [] };
        var meter = draft.Meter;
        draft = topology switch
        {
            "three_phase_summed" => draft with
            {
                Connection = draft.Connection with { Topology = topology },
                Meter = meter with { CurrentSign = "positive_import", CurrentDirection = null, Phases = new MeterPhases(), Total = new MeterTotal { Current = SetupFixtures.Signal("sensor.meter_total_current", "A") } },
                Sources = new SourcesDraft(),
            },
            "single_phase" => draft with
            {
                Connection = draft.Connection with { Topology = topology, ReserveW = new PhaseValues { L1 = 2000 } },
                Meter = meter with { Phases = new MeterPhases { L1 = meter.Phases.L1 } },
                Sources = new SourcesDraft { Inverter = draft.Sources.Inverter! with { PhaseCurrents = null } },
            },
            _ => draft,
        };

        var (text, result, _) = SetupFixtures.Apply(null, draft);

        Assert.True(result.Status == ConfigurationStatus.Valid, string.Join("; ", result.Errors) + "\n" + text);
        Assert.Equal(topology, SetupDocumentMapper.Read(SetupFixtures.Tree(text)).Draft.Connection.Topology);
    }

    [Fact]
    public void Clean_install_draft_writes_a_valid_file()
    {
        var (text, result, _) = SetupFixtures.Apply(null, SetupFixtures.MaintainerDraft());

        Assert.True(result.Status == ConfigurationStatus.Valid, string.Join("; ", result.Errors) + "\n" + text);
        Assert.Equal(19, result.Configuration!.Signals.Length);
        Assert.StartsWith("# Written by the Switchboard setup wizard.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_phase_or_three_phase_round_trips()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var three = draft with { Devices = [.. draft.Devices, SetupFixtures.Heater("boiler", "switch.boiler", "three_phase") with { RotationGroup = null, RatedCurrentA = 9, RatedPowerW = 6000 }] };

        var (text, result, _) = SetupFixtures.Apply(null, three);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal([Phase.L1, Phase.L2, Phase.L3], result.Configuration!.Devices[^1].Stages[0].Phases);
        Assert.Equal("three_phase", SetupDocumentMapper.Read(SetupFixtures.Tree(text)).Draft.Devices[^1].Phase);
    }

    [Fact]
    public void Single_phase_inverter_phase_round_trips()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var single = draft with { Sources = new SourcesDraft { Inverter = draft.Sources.Inverter! with { Phase = "L2", PhaseCurrents = null } } };

        var (text, result, _) = SetupFixtures.Apply(null, single);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal(Phase.L2, result.Configuration!.Signals.Single(s => s.Role == SignalRole.InverterPower).Phase);
        Assert.Equal("L2", SetupDocumentMapper.Read(SetupFixtures.Tree(text)).Draft.Sources.Inverter!.Phase);
    }

    [Fact]
    public void Invalid_preserved_section_can_be_dropped_with_confirmation()
    {
        // A broken control: override the wizard does not edit would otherwise block every save.
        var yaml = Rich.Replace("      residual_threshold_w: 400\n", "      residual_threshold: 400\n", StringComparison.Ordinal);
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml));
        Assert.Equal(ConfigurationStatus.Invalid, SetupFixtures.Apply(yaml, load.Draft).Result.Status);

        var (_, result, mapped) = SetupFixtures.Apply(yaml, load.Draft with { DropPreserved = ["control"] });

        Assert.Empty(mapped.Issues);
        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal(300, result.Configuration!.Control.Signals.Balance.ResidualThreshold.Value);
    }

    [Fact]
    public void Dropping_a_path_that_is_not_preserved_is_refused()
    {
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich)).Draft;

        var mapped = SetupDocumentMapper.Apply(SetupFixtures.Tree(Rich), draft with { DropPreserved = ["devices"] });

        Assert.Contains(mapped.Issues, i => i.Code == SetupIssueCodes.PreservedPathUnknown && i.Field == "dropPreserved[0]");
        Assert.NotNull(((ConfigMapping)mapped.Root).Find("devices"));
    }

    [Fact]
    public void Schema_errors_return_draft_field_and_no_line_numbers()
    {
        // UI-05: a parser issue is reported at the field that wrote the path, without the file's line.
        var draft = SetupFixtures.MaintainerDraft();
        var broken = draft with { Devices = [draft.Devices[0] with { RatedCurrentA = 99 }, .. draft.Devices.Skip(1)] };

        var (_, result, mapped) = SetupFixtures.Apply(null, broken);

        var issue = result.Errors.Single(e => e.Path == "devices[0].rated_current_a");
        var reported = SetupService.FromParser(issue, mapped.Fields, IssueSeverity.Error);
        Assert.Equal(("devices[0].ratedCurrentA", "devices[0].rated_current_a", SetupIssueCodes.DocumentInvalid), (reported.Field, reported.DocumentPath, reported.Code));
        Assert.Equal(ConfigurationIssueCodes.OutOfRange, reported.Args["issue"]);
        Assert.DoesNotContain("line", reported.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_unit_is_reported_at_the_meter_field()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var noUnit = draft with
        {
            Meter = draft.Meter with { Phases = draft.Meter.Phases with { L2 = draft.Meter.Phases.L2! with { Power = draft.Meter.Phases.L2.Power! with { Unit = null } } } },
        };

        var (_, result, mapped) = SetupFixtures.Apply(null, noUnit);

        var fields = result.Errors.Select(e => SetupService.FromParser(e, mapped.Fields, IssueSeverity.Error).Field).ToList();
        Assert.Contains("meter.phases.L2.power.unit", fields);
    }

    private static readonly string G13Snippet = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));

    private static ConfigNode Section(ConfigNode root, string key) => ((ConfigMapping)root).Find(key)!.Value;

    [Fact]
    public void Economics_section_survives_a_wizard_save_unchanged_and_the_file_still_parses()
    {
        // ECO-02: the wizard has no economics step, so a save that dropped or rewrote the section would silently
        // end the user's tariff the first time they touched a device.
        var yaml = Rich.TrimEnd() + "\n\n" + G13Snippet;
        var before = SetupFixtures.Tree(yaml);
        var load = SetupDocumentMapper.Read(before);
        Assert.Contains(load.Preserved, p => p.Path == "economics");

        var draft = load.Draft with { Devices = [.. load.Draft.Devices.Select(d => d.Id == "ev_charger" ? d with { Name = "Garage charger" } : d)] };
        var (text, result, _) = SetupFixtures.Apply(yaml, draft);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal(SetupFixtures.Canon(Section(before, "economics")), SetupFixtures.Canon(Section(SetupFixtures.Tree(text), "economics")));
        Assert.Equal("PLN", result.Configuration!.Economics!.Currency);
        Assert.Equal("Garage charger", result.Configuration.Devices.Single(d => d.Id.Value == "ev_charger").Name);
    }

    [Fact]
    public void A_save_on_a_file_without_economics_does_not_invent_one()
    {
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich));

        var (text, result, _) = SetupFixtures.Apply(Rich, load.Draft);

        Assert.DoesNotContain("economics", text, StringComparison.Ordinal);
        Assert.Null(result.Configuration!.Economics);
    }

    [Fact]
    public void Economics_is_removed_only_when_the_user_confirms_dropping_it()
    {
        var yaml = Rich.TrimEnd() + "\n\n" + G13Snippet;
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml));

        var (text, result, _) = SetupFixtures.Apply(yaml, load.Draft with { DropPreserved = ["economics"] });

        Assert.DoesNotContain("economics", text, StringComparison.Ordinal);
        Assert.Null(result.Configuration!.Economics);
    }

    [Fact]
    public void Device_keys_the_wizard_does_not_know_survive_a_save()
    {
        // Slice 2 adds value_follows, value_factor and sources to a device. The wizard patches the parsed tree and keeps
        // every key it does not own, so they must still be there after a save; checked on the tree because the runtime
        // parser of this version does not read the first two yet.
        var yaml = Rich.Replace(
            "    value_per_kwh: 0.35\n",
            "    value_per_kwh: 0.35\n    value_follows: import_price\n    value_factor: 3.2\n    sources: pv_only\n",
            StringComparison.Ordinal);
        Assert.Contains("value_follows: import_price", yaml, StringComparison.Ordinal);
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml));
        var draft = load.Draft with { Devices = [.. load.Draft.Devices.Select(d => d.Id == "ev_charger" ? d with { Name = "Garage charger" } : d)] };

        var mapped = SetupDocumentMapper.Apply(SetupFixtures.Tree(yaml), draft);

        var devices = (ConfigSequence)Section(mapped.Root, "devices");
        var withKeys = devices.Items.OfType<ConfigMapping>().Where(d => d.Find("value_follows") is not null).ToList();
        Assert.Equal(Rich.Split("    value_per_kwh: 0.35\n").Length - 1, withKeys.Count);
        Assert.All(withKeys, d =>
        {
            Assert.Equal("import_price", ((ConfigScalar)d.Find("value_follows")!.Value).Value);
            Assert.Equal("3.2", ((ConfigScalar)d.Find("value_factor")!.Value).Value);
            Assert.Equal("pv_only", ((ConfigScalar)d.Find("sources")!.Value).Value);
        });
    }

    [Fact]
    public void A_price_signal_and_a_dynamic_economics_section_survive_a_wizard_save_and_still_parse()
    {
        // ECO-10 with ECO-02: the wizard has no price step. A save that dropped the price signal or rewrote the dynamic spec would turn the user's
        // live price into its fallback (or invalidate the file) the first time they touched a device.
        var dynamic = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-dynamic.yaml")).ReplaceLineEndings("\n");
        var signal = dynamic[(dynamic.IndexOf("\nsignals:\n", StringComparison.Ordinal) + "\nsignals:\n".Length)..dynamic.IndexOf("\neconomics:\n", StringComparison.Ordinal)].TrimEnd() + "\n";
        var economics = dynamic[dynamic.IndexOf("\neconomics:\n", StringComparison.Ordinal)..];
        var yaml = Rich.Replace("\nsignals:\n", "\nsignals:\n" + signal, StringComparison.Ordinal).TrimEnd() + "\n" + economics;
        var before = SetupFixtures.Tree(yaml);
        var load = SetupDocumentMapper.Read(before);
        Assert.Contains(load.Preserved, p => p.Path == "economics");
        Assert.Contains(load.Preserved, p => p.Path == "signals.market_price");

        var draft = load.Draft with { Devices = [.. load.Draft.Devices.Select(d => d.Id == "ev_charger" ? d with { Name = "Garage charger" } : d)] };
        var (text, result, _) = SetupFixtures.Apply(yaml, draft);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        var after = SetupFixtures.Tree(text);
        Assert.Equal(SetupFixtures.Canon(Section(before, "economics")), SetupFixtures.Canon(Section(after, "economics")));
        Assert.Equal(
            SetupFixtures.Canon(((ConfigMapping)Section(before, "signals")).Find("market_price")!.Value),
            SetupFixtures.Canon(((ConfigMapping)Section(after, "signals")).Find("market_price")!.Value));
        Assert.IsType<Switchboard.Domain.Economics.DynamicPrice>(result.Configuration!.Economics!.Tariff);
        Assert.Equal("PLN", result.Configuration.Signals.Single(s => s.Name.Value == "market_price").PriceCurrency);
        Assert.DoesNotContain(result.Warnings, w => w.Code is ConfigurationIssueCodes.UnitMismatch or ConfigurationIssueCodes.PriceSignalUnused);
    }

    private static readonly string PlannerSnippet = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "planner-goal.yaml")).ReplaceLineEndings("\n");

    private const string ChargerGoal =
        "    value_per_kwh: 2.03\n    goal:\n      kind: soc\n      hard: true\n      capacity_kwh: 18\n      loss_fraction: 0.10\n      soc: { entity: sensor.car_battery_level, max_age: 30m }\n      target: { value: 90 }\n      deadline: { entity: input_datetime.car_departure, margin: 30m }\n      want: { entity: input_boolean.car_charge_wanted }\n";

    [Fact]
    public void A_device_goal_and_the_planner_section_survive_a_wizard_save_unchanged_and_still_parse()
    {
        // PLAN-01 and PLAN-05: the wizard has no planner step and no goal field. A save that dropped the section, or the goal a user typed under
        // a device (and the signals it creates), would silently end their planner the first time they touched a device name.
        var yaml = Rich.Replace("    value_per_kwh: 2.03\n", ChargerGoal, StringComparison.Ordinal).TrimEnd() + "\n\n" + PlannerSnippet;
        Assert.Contains("goal:", yaml, StringComparison.Ordinal);
        var before = SetupFixtures.Tree(yaml);
        var load = SetupDocumentMapper.Read(before);
        Assert.Contains(load.Preserved, p => p.Path == "planner");
        Assert.True(load.Draft.Devices.Single(d => d.Id == "ev_charger").Editable);

        var draft = load.Draft with { Devices = [.. load.Draft.Devices.Select(d => d.Id == "ev_charger" ? d with { Name = "Garage charger" } : d)] };
        var (text, result, _) = SetupFixtures.Apply(yaml, draft);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        var after = SetupFixtures.Tree(text);
        Assert.Equal(SetupFixtures.Canon(Section(before, "planner")), SetupFixtures.Canon(Section(after, "planner")));
        Assert.Equal(Switchboard.Domain.Planning.PlannerDispatchMode.Off, result.Configuration!.Planner!.Dispatch);
        var charger = result.Configuration.Devices.Single(d => d.Id.Value == "ev_charger");
        Assert.Equal("Garage charger", charger.Name);
        Assert.Equal(Switchboard.Domain.Goals.GoalKind.Soc, charger.Goal!.Kind);
        Assert.Equal(
            SetupFixtures.Canon(((ConfigMapping)((ConfigSequence)Section(before, "devices")).Items.OfType<ConfigMapping>().Single(d => ((ConfigScalar)d.Find("id")!.Value).Value == "ev_charger")).Find("goal")!.Value),
            SetupFixtures.Canon(((ConfigMapping)((ConfigSequence)Section(after, "devices")).Items.OfType<ConfigMapping>().Single(d => ((ConfigScalar)d.Find("id")!.Value).Value == "ev_charger")).Find("goal")!.Value));
        Assert.Equal(
            ["device_ev_charger_goal_deadline", "device_ev_charger_goal_want", "device_ev_charger_soc"],
            result.Configuration.Signals.Where(s => s.Name.Value.StartsWith("device_ev_charger_goal", StringComparison.Ordinal) || s.Name.Value == "device_ev_charger_soc").Select(s => s.Name.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_save_on_a_file_without_a_planner_or_a_goal_does_not_invent_one()
    {
        // Why: a 0.10 file stays a 0.10 file through the wizard: no planner section appears and no device gains a goal.
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich));

        var (text, result, _) = SetupFixtures.Apply(Rich, load.Draft);

        Assert.DoesNotContain("planner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("goal:", text, StringComparison.Ordinal);
        Assert.Null(result.Configuration!.Planner);
        Assert.All(result.Configuration.Devices, d => Assert.Null(d.Goal));
    }

    [Fact]
    public void The_planner_section_is_removed_only_when_the_user_confirms_dropping_it()
    {
        var yaml = Rich.TrimEnd() + "\n\n" + PlannerSnippet;
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml));

        var (text, result, _) = SetupFixtures.Apply(yaml, load.Draft with { DropPreserved = ["planner"] });

        Assert.DoesNotContain("planner:", text, StringComparison.Ordinal);
        Assert.Null(result.Configuration!.Planner);
    }

    private static readonly string EvSnippet = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "ev.yaml")).ReplaceLineEndings("\n");

    [Fact]
    public void The_ev_section_survives_a_wizard_save_unchanged_and_the_file_still_parses()
    {
        // EV-01: the wizard has no EV step. A save that dropped the section would silently end the user's EV configuration the first time they
        // touched a device name; a rewrite would change what they typed. The example file is the section a user starts from.
        var yaml = Rich.TrimEnd() + "\n\n" + EvSnippet;
        var before = SetupFixtures.Tree(yaml);
        var load = SetupDocumentMapper.Read(before);
        Assert.Contains(load.Preserved, p => p.Path == "ev");

        var draft = load.Draft with { Devices = [.. load.Draft.Devices.Select(d => d.Id == "ev_charger" ? d with { Name = "Garage charger" } : d)] };
        var (text, result, _) = SetupFixtures.Apply(yaml, draft);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal(SetupFixtures.Canon(Section(before, "ev")), SetupFixtures.Canon(Section(SetupFixtures.Tree(text), "ev")));
        var ev = result.Configuration!.Ev!;
        Assert.Equal(("ev_charger", 18.0), (ev.Device.Value, ev.CapacityKwh));
        Assert.Contains(result.Configuration.Signals, s => s.Name == ev.Soc && s.Entity.Value == "sensor.car_battery_level");
        Assert.Equal("Garage charger", result.Configuration.Devices.Single(d => d.Id.Value == "ev_charger").Name);
    }

    [Fact]
    public void A_save_on_a_file_without_an_ev_section_does_not_invent_one()
    {
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(Rich));

        var (text, result, _) = SetupFixtures.Apply(Rich, load.Draft);

        Assert.DoesNotContain("\nev:", text, StringComparison.Ordinal);
        Assert.Null(result.Configuration!.Ev);
    }

    [Fact]
    public void The_ev_section_is_removed_only_when_the_user_confirms_dropping_it()
    {
        var yaml = Rich.TrimEnd() + "\n\n" + EvSnippet;
        var load = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml));

        var (text, result, _) = SetupFixtures.Apply(yaml, load.Draft with { DropPreserved = ["ev"] });

        Assert.DoesNotContain("\nev:", text, StringComparison.Ordinal);
        Assert.Null(result.Configuration!.Ev);
    }

    [Fact]
    public void The_new_device_key_is_one_the_wizard_knows_it_does_not_own()
    {
        // Why: KeysFor feeds the wizard's templates; goal must be a key a device of every type may carry, and it stays out of the draft's
        // owned fields (so a save never rewrites it).
        foreach (var type in new[] { Switchboard.Domain.Devices.DeviceType.OnOff, Switchboard.Domain.Devices.DeviceType.Stepped, Switchboard.Domain.Devices.DeviceType.Modulating, Switchboard.Domain.Devices.DeviceType.Storage })
        {
            Assert.Contains("goal", DeviceSectionParser.KeysFor(type));
        }
    }
}
