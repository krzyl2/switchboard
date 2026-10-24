using Switchboard.Application.Configuration;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.Runtime;
using Switchboard.Tests.Support;
using YamlDotNet.Serialization;

namespace Switchboard.Tests.Packaging;

/// <summary>
/// Where the configuration document lives (DECISIONS: the add-on config folder, editable by hand and
/// part of backups) and what ships with it: the embedded control defaults and a valid example that
/// uses primary measurements only.
/// </summary>
[Trait("Category", "Packaging")]
public sealed class ConfigurationPackagingTests
{
    // Template and derived sensors at the reference installation: inverter − meter differences and
    // averages that dip periodically (07-PITFALLS). None may ever be a control input.
    private static readonly string[] DerivedSensors =
    [
        "sensor.usage",
        "sensor.zuzycie_srednie_5m",
        "sensor.moc_grzalek_cwu",
        "sensor.grzalki_srednie_5m",
    ];

    [Fact]
    public void Configuration_folder_mapped_writable_before_data()
    {
        var config = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(File.ReadAllText(RepoPaths.Combine("switchboard", "config.yaml")));

        var map = Assert.IsType<List<object>>(config["map"], exactMatch: false)
            .Select(entry => Assert.IsType<Dictionary<object, object>>(entry, exactMatch: false))
            .ToList();

        Assert.Equal(["addon_config", "data"], map.Select(m => m["type"].ToString()));
        Assert.Equal("false", map[0]["read_only"].ToString());
    }

    [Fact]
    public void Default_configuration_path_is_addon_config()
    {
        // The Supervisor mounts the add-on config folder at /config inside the container.
        Assert.Equal("/config/switchboard.yaml", RuntimeEnvironment.Read(_ => null).ConfigPath);
        Assert.Equal("/elsewhere.yaml", RuntimeEnvironment.Read(key => key == "SWITCHBOARD_CONFIG_PATH" ? "/elsewhere.yaml" : null).ConfigPath);
    }

    [Fact]
    public void Shipped_defaults_embedded_and_valid()
    {
        Assert.Contains(EmbeddedControlDefaults.ResourceName, typeof(EmbeddedControlDefaults).Assembly.GetManifestResourceNames());
        Assert.IsType<ConfigMapping>(EmbeddedControlDefaults.Load());
    }

    [Fact]
    public void Example_configuration_loads_without_issues()
    {
        var result = TestConfigurations.Parse(TestConfigurations.Example);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Example_uses_no_derived_sensor()
    {
        var entities = TestConfigurations.Valid(TestConfigurations.Example).Configuration!.Signals.Select(s => s.Entity.Value).ToList();

        Assert.Empty(entities.Intersect(DerivedSensors));
    }

    [Fact]
    public void Example_devices_valid_and_heaters_shed_before_the_car()
    {
        // The maintainer's loads: three 1.7 kW elements, one per phase, and the go-e charger on L1.
        // The house prefers charging the car, so on L1 the heater goes first; the actuators are the
        // declared relays and the charger's current number, never a derived sensor. Each element is
        // declared on the meter phase its relay moves in the recording (switch.grzalka_2 on L1 with
        // the charger, switch.grzalka_1 on L2), so an L1 overload sheds the element that draws on L1.
        var configuration = TestConfigurations.Valid(TestConfigurations.Example).Configuration!;

        Assert.Equal(["heater_l1", "heater_l2", "heater_l3", "ev_charger"], configuration.Devices.Select(d => d.Id.Value));
        Assert.Equal(
            ["switch.grzalka_2", "switch.grzalka_1", "switch.grzalka_3", "number.goe_422752_amp"],
            configuration.Devices.SelectMany(d => d.Stages).Select(s => s.Actuator.Value));
        Assert.Empty(configuration.Devices.SelectMany(d => d.Stages).Select(s => s.StateEntity.Value).Intersect(DerivedSensors));
        var l1 = Switchboard.Domain.Guard.ShedOrder.For(
            Switchboard.Domain.Signals.Phase.L1,
            [.. configuration.Devices.Select(d => new Switchboard.Domain.Guard.ShedCandidate(
                d.ToSpec(),
                [.. d.Stages.Select(_ => (Switchboard.Domain.Devices.AppliedState?)null)],
                false,
                System.Collections.Immutable.ImmutableDictionary<int, Switchboard.Domain.Guard.StageLimit>.Empty))]);
        // The charger has no stop actuator: it is reduced to its minimum and never spends a second
        // (switch off) step that could not happen.
        Assert.Equal(["heater_l1", "ev_charger"], l1.Select(s => s.Device.Value));
    }
}
