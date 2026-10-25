using Switchboard.Application.Configuration;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// devices[].name is a display label (the Polish names on the dashboard), optional, and never part
/// of a decision: it does not change a device's identity, so renaming a device never resets its
/// ledger. Untrusted like every key: trimmed, 1 to 40 characters, no control characters.
/// </summary>
public sealed class DeviceNameTests
{
    private const string Heater = """
          - id: heater
            {0}type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;

    [Fact]
    public void A_name_is_trimmed_and_kept_for_display()
    {
        var device = Single(Device("name: '  Grzałka CWU L1 '\n    "));

        Assert.Equal("Grzałka CWU L1", device.Name);
    }

    [Fact]
    public void Without_a_name_the_device_has_none()
    {
        Assert.Null(Single(Device(string.Empty)).Name);
    }

    [Theory]
    [InlineData("name: ''\n    ")]
    [InlineData("name: '   '\n    ")]
    [InlineData("name: \"Line\\nbreak\"\n    ")]
    [InlineData("name: \"Tab\\tbed\"\n    ")]
    [InlineData("name: '12345678901234567890123456789012345678901'\n    ")]
    public void A_name_outside_the_rules_is_invalid_value_at_its_key(string line)
    {
        var error = Assert.Single(TestConfigurations.Parse(Device(line)).Errors);

        Assert.Equal(("invalid_value", "devices[0].name"), (error.Code, error.Path));
    }

    [Fact]
    public void A_name_does_not_change_the_device_identity()
    {
        // The specification fingerprint decides whether a device's ledger is reset on reload.
        var named = Single(Device("name: Kocioł\n    ")).ToSpec();
        var plain = Single(Device(string.Empty)).ToSpec();

        Assert.Equal(Switchboard.Domain.Dispatch.SpecIdentity.Fingerprint(plain), Switchboard.Domain.Dispatch.SpecIdentity.Fingerprint(named));
    }

    private static string Device(string nameLine) => TestDevices.Document(string.Format(System.Globalization.CultureInfo.InvariantCulture, Heater, nameLine));

    private static DeviceDefinition Single(string yaml) => Assert.Single(TestConfigurations.Valid(yaml).Configuration!.Devices);
}
