using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;
using Switchboard.Domain.Devices;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// The wizard's rules ship in setup-defaults.yaml, never as code literals, and a template may only
/// write keys a device of switchboard.yaml accepts: a template key the parser refuses would make
/// every device created from that card invalid. What a template does not write (reaction time,
/// lockouts) is inherited from control-defaults, so a later default still applies to it (UI-02).
/// </summary>
public sealed class SetupDefaultsTests
{
    private static readonly SetupDefaults Defaults = EmbeddedSetupDefaults.Load();

    [Fact]
    public void The_embedded_rules_bind()
    {
        Assert.Equal(4, Defaults.Templates.Length);
        Assert.Equal(["ev_charger", "water_heater_element", "heat_pump", "generic_onoff"], Defaults.Templates.Select(t => t.Id));
        Assert.Equal(TimeSpan.FromSeconds(60), Defaults.SelfTest.Isolation);
        Assert.Equal(TimeSpan.FromMinutes(15), Defaults.SelfTest.WatchTimeout);
        Assert.Equal(230, Defaults.PrefillVoltage);
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
    public void Every_derived_platform_is_listed(string platform)
    {
        // 07-PITFALLS: a sensor computed from other sensors is never a control input; powercalc
        // estimates a load's power, it does not measure it.
        Assert.Contains(platform, Defaults.DerivedPlatforms);
    }

    [Fact]
    public void Templates_reference_only_parser_keys()
    {
        foreach (var template in Defaults.Templates)
        {
            Assert.All(template.Prefill, value => Assert.True(DeviceTemplates.IsDeviceKey(template.Type, value.Key), $"{template.Id}: '{value.Key}' is not a key of a {template.Type} device."));
        }
    }

    [Fact]
    public void A_device_from_each_template_parses_with_the_runtime_parser()
    {
        // The prefill written as a device next to the user's own fields is a valid document.
        foreach (var template in Defaults.Templates)
        {
            var prefill = string.Join("\n", template.Prefill.Select(v => $"    {v.Key}: {v.Text}"));
            var fields = template.Type == DeviceType.OnOff
                ? "    actuator: switch.test_relay\n    phase: L1\n    rated_current_a: 7\n    rated_power_w: 1600"
                : "    actuator: number.test_current\n    phase: L1";
            var yaml = TestConfigurations.ThreePhase(extra: $"devices:\n  - id: from_{template.Id}\n    type: {DeviceSectionParser.TypeName(template.Type)}\n{fields}\n{prefill}\n    value_per_kwh: 1\n");
            var result = TestConfigurations.Parse(yaml);
            Assert.True(result.Status == ConfigurationStatus.Valid, $"{template.Id}: {string.Join("; ", result.Errors)}");
        }
    }

    [Fact]
    public void Template_inherits_lockouts_without_writing_them()
    {
        var control = SwitchboardDocumentParser.BindControl(null, TestConfigurations.Defaults)!;
        var templates = DeviceTemplates.Describe(Defaults, control).ToDictionary(t => t.Id);

        // The heater element writes only its type; its lockouts are the relay defaults.
        Assert.Equal(["type"], templates["water_heater_element"].Prefill.Keys);
        Assert.Equal((control.Dispatch.RelayMinOn, control.Dispatch.RelayMinOff), (templates["water_heater_element"].MinOn, templates["water_heater_element"].MinOff));
        Assert.Equal(control.Devices.ReactionTime[DeviceType.Modulating], templates["ev_charger"].ReactionTime);
        Assert.Equal((control.Devices.ModulatingMinOn, control.Devices.ModulatingMinOff), (templates["ev_charger"].MinOn, templates["ev_charger"].MinOff));
        Assert.Equal(["number", "input_number"], templates["ev_charger"].ActuatorDomains);
        Assert.Equal(["switch", "input_boolean"], templates["generic_onoff"].ActuatorDomains);
        Assert.True(templates["ev_charger"].DrawRecommended);
        Assert.Equal(new Dictionary<string, object> { ["type"] = "modulating", ["min"] = 6.0, ["max"] = 16.0, ["step"] = 1.0 }, templates["ev_charger"].Prefill);
        Assert.Equal("10m", templates["heat_pump"].Prefill["minOn"]);
    }
}
