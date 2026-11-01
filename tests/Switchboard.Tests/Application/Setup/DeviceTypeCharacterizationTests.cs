using System.Text.Json;
using System.Text.RegularExpressions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;
using Switchboard.Domain.Devices;
using Switchboard.Tests.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// The wizard's reading of a device type, recorded on the string-typed setup layer before it was
/// parsed into <see cref="DeviceType"/>: the keys a device of each type is written with, what an
/// unknown or wrongly spelled type does, and which checks apply to which type. A type a draft
/// spells in a way the parser does not know is written as typed and refused by the parser, never
/// treated as a known one; a stepped device is kept as written and cannot be edited.
/// </summary>
public sealed class DeviceTypeCharacterizationTests
{
    private const string Common = "id,name,type,actuator,state_entity,phase";
    private const string Tail = "value_per_kwh,state_reports,reaction_time,min_on,min_off";

    /// <summary>Every field a device draft can carry, with entities of the domains of the given type.</summary>
    private static DeviceDraft Full(string? type, bool setpoint) => new()
    {
        Id = "d1",
        Name = "Device one",
        Type = type,
        Actuator = setpoint ? "number.d1_amp" : "switch.d1",
        StateEntity = setpoint ? "sensor.d1_amp" : "binary_sensor.d1",
        StateReports = "on_change",
        Phase = "L1",
        RatedCurrentA = 7,
        RatedPowerW = 1700,
        Min = 6,
        Max = 16,
        Step = 1,
        ReactionTime = "10s",
        MinOn = "30s",
        MinOff = "30s",
        ValuePerKwh = 1,
        RotationGroup = "grp",
        StopActuator = "select.d1_frc",
        StopOption = "1",
        ResumeOption = "0",
        Start = "headroom",
        Draw = new DrawDraft { Current = "sensor.d1_current", Power = "sensor.d1_power", MaxAge = "60s" },
    };

    private static (string Keys, ConfigurationStatus Status, string Errors, string Issues) Written(string? type, bool setpoint)
    {
        var draft = SetupFixtures.MaintainerDraft();
        var (_, result, mapped) = SetupFixtures.Apply(null, draft with { Devices = [Full(type, setpoint)] });
        var device = (ConfigMapping)((ConfigSequence)((ConfigMapping)mapped.Root).Get("devices")!).Items[0];
        return (
            string.Join(",", device.Entries.Select(e => e.Key)),
            result.Status,
            string.Join("; ", result.Errors.Select(e => Regex.Replace(e.ToString()!, @"^(\S+?):\d+:\d+ (\w+) .*$", "$1 $2", RegexOptions.Singleline))),
            string.Join("; ", mapped.Issues.Select(i => $"{i.Code}@{i.Field}")));
    }

    [Fact]
    public void An_onoff_device_keeps_its_rating_and_rotation_group_and_drops_the_setpoint_keys()
    {
        var (keys, status, errors, issues) = Written("onoff", setpoint: false);

        Assert.Equal($"{Common},rated_current_a,rated_power_w,{Tail},rotation_group,start,draw", keys);
        Assert.Equal((ConfigurationStatus.Invalid, "devices[0].rotation_group rotation_group_mismatch", ""), (status, errors, issues));
    }

    [Fact]
    public void A_modulating_device_keeps_its_range_and_stop_actuator_and_drops_the_rating()
    {
        var (keys, status, errors, issues) = Written("modulating", setpoint: true);

        Assert.Equal($"{Common},min,max,step,{Tail},stop_actuator,stop_option,resume_option,start,draw", keys);
        Assert.Equal((ConfigurationStatus.Valid, "", ""), (status, errors, issues));
    }

    [Fact]
    public void A_storage_device_keeps_its_range_but_never_a_stop_actuator()
    {
        var (keys, status, errors, issues) = Written("storage", setpoint: true);

        Assert.Equal($"{Common},min,max,step,{Tail},start,draw", keys);
        Assert.Equal((ConfigurationStatus.Invalid, "devices[0].min modulating_range_invalid", ""), (status, errors, issues));
    }

    [Fact]
    public void A_stepped_device_the_wizard_cannot_represent_keeps_neither_rating_nor_range_and_is_refused_by_the_parser()
    {
        var (keys, status, errors, issues) = Written("stepped", setpoint: false);

        Assert.Equal($"{Common},{Tail},start,draw", keys);
        Assert.Equal(
            (ConfigurationStatus.Invalid, "devices[0].actuator unknown_key; devices[0].state_entity unknown_key; devices[0].phase unknown_key; devices[0].stages missing_key", ""),
            (status, errors, issues));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("Onoff")]
    public void A_type_the_parser_does_not_spell_is_written_as_typed_with_no_type_keys_and_refused(string type)
    {
        var (keys, status, errors, issues) = Written(type, setpoint: false);

        Assert.Equal($"{Common},{Tail},start,draw", keys);
        Assert.Equal((ConfigurationStatus.Invalid, "devices[0].type invalid_value", ""), (status, errors, issues));
    }

    [Fact]
    public void A_device_without_a_type_is_written_without_one_and_refused_as_missing()
    {
        var (keys, status, errors, issues) = Written(null, setpoint: false);

        Assert.Equal($"id,name,actuator,state_entity,phase,{Tail},start,draw", keys);
        Assert.Equal((ConfigurationStatus.Invalid, "devices[0].type missing_key", ""), (status, errors, issues));
    }

    [Fact]
    public void An_existing_device_naming_no_type_is_not_a_type_change()
    {
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(TestConfigurations.Example)).Draft;
        var untyped = draft with { Devices = [draft.Devices[0] with { Type = null }, .. draft.Devices.Skip(1)] };

        var (text, result, mapped) = SetupFixtures.Apply(TestConfigurations.Example, untyped);

        Assert.Empty(mapped.Issues);
        Assert.Equal(DeviceType.OnOff, result.Configuration!.Devices[0].Type);
        Assert.Contains("type: onoff", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Modulating")]
    [InlineData("MODULATING ")]
    [InlineData("bogus")]
    public void An_existing_device_naming_a_different_spelling_of_its_type_is_a_type_change(string spelled)
    {
        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(TestConfigurations.Example)).Draft;
        var respelled = draft with { Devices = [.. draft.Devices.Take(3), draft.Devices[3] with { Type = spelled }] };

        var (_, result, mapped) = SetupFixtures.Apply(TestConfigurations.Example, respelled);

        var issue = Assert.Single(mapped.Issues);
        Assert.Equal((SetupIssueCodes.ExistingDeviceImmutable, "devices[3].type", "modulating"), (issue.Code, issue.Field, issue.Args["type"]));
        Assert.Equal(DeviceType.Modulating, result.Configuration!.Devices[3].Type);
    }

    [Fact]
    public void A_stepped_device_in_the_file_is_kept_as_written_and_not_editable()
    {
        var yaml = DispatchSyntheticReplayRun.Configuration.Replace("\ncircuit_groups:", "\n  - id: heat_pump\n    type: stepped\n    stages:\n      - { actuator: switch.hp_stage_1, phase: L2, rated_current_a: 5 }\n\ncircuit_groups:", StringComparison.Ordinal);

        var draft = SetupDocumentMapper.Read(SetupFixtures.Tree(yaml)).Draft;

        Assert.Equal(("stepped", false), (draft.Devices[4].Type, draft.Devices[4].Editable));
        Assert.All(draft.Devices.Take(4), d => Assert.True(d.Editable));
        var (text, _, _) = SetupFixtures.Apply(yaml, draft with { Devices = [.. draft.Devices.Take(4), draft.Devices[4] with { Actuator = "switch.other" }] });
        Assert.Contains("switch.hp_stage_1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("switch.other", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_draft_json_keeps_the_types_spelling_and_the_same_properties()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(new DeviceDraft { Type = "Weird" }, options);

        Assert.Equal("Weird", JsonSerializer.Deserialize<DeviceDraft>(json, options)!.Type);
        Assert.Equal(
            ["id", "existing", "editable", "template", "name", "type", "actuator", "stateEntity", "stateReports", "phase", "ratedCurrentA", "ratedPowerW", "min", "max", "step", "reactionTime", "minOn", "minOff", "valuePerKwh", "circuitGroup", "rotationGroup", "draw", "stopActuator", "stopOption", "resumeOption", "start"],
            JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
    }

    private const string CommonKeys = "id, name, type, value_per_kwh, shed_priority, reaction_time, no_shed, min_on, min_off, state_max_age, state_reports, draw, circuit_group, start, value_follows, value_factor, sources, goal";

    [Theory]
    [InlineData(DeviceType.OnOff, "onoff", "actuator, state_entity, phase, rated_current_a, rated_power_w, rotation_group", "switch, input_boolean", "switch, input_boolean, binary_sensor")]
    [InlineData(DeviceType.Stepped, "stepped", "stages", "switch, input_boolean", "switch, input_boolean, binary_sensor")]
    [InlineData(DeviceType.Modulating, "modulating", "actuator, state_entity, phase, min, max, step, unit, stop_actuator, stop_option, resume_option", "number, input_number", "number, input_number, sensor")]
    [InlineData(DeviceType.Storage, "storage", "actuator, state_entity, phase, min, max, step, unit", "number, input_number", "number, input_number, sensor")]
    public void Each_type_has_one_spelling_one_key_set_and_its_entity_domains(DeviceType type, string name, string typeKeys, string actuatorDomains, string stateDomains)
    {
        Assert.Equal(name, DeviceSectionParser.TypeName(type));
        Assert.Equal(type, DeviceSectionParser.TypeOf(name));
        Assert.Equal($"{CommonKeys}, {typeKeys}", string.Join(", ", DeviceSectionParser.KeysFor(type)));
        Assert.Equal(actuatorDomains, string.Join(", ", DeviceSectionParser.ActuatorDomainsFor(type)));
        Assert.Equal(stateDomains, string.Join(", ", DeviceSectionParser.StateDomainsFor(type)));
    }

    [Theory]
    [InlineData("onoff", "actuator: switch.a|phase: L1|rated_current_a: 7|bogus_key: 1")]
    [InlineData("modulating", "actuator: number.a|phase: L1|min: 6|max: 16|step: 1|bogus_key: 1")]
    [InlineData("storage", "actuator: number.a|phase: L1|min: -10|max: 10|step: 1|bogus_key: 1")]
    [InlineData("stepped", "stages: [{ actuator: switch.a, phase: L1, rated_current_a: 5 }]|bogus_key: 1")]
    public void An_unknown_device_key_is_refused_naming_exactly_the_keys_of_the_type(string type, string body)
    {
        var lines = body.Replace("|", "\n    ", StringComparison.Ordinal);
        var yaml = TestConfigurations.ThreePhase(extra: $"devices:\n  - id: d1\n    type: {type}\n    value_per_kwh: 1\n    {lines}\n");

        var error = Assert.Single(TestConfigurations.Parse(yaml).Errors, e => e.ToString()!.Contains("bogus_key", StringComparison.Ordinal));

        var expected = DeviceSectionParser.KeysFor(DeviceSectionParser.TypeOf(type)!.Value);
        Assert.Contains($"Allowed here: {string.Join(", ", expected)}.", error.ToString(), StringComparison.Ordinal);
    }
}
