using Switchboard.Application.Configuration;
using Switchboard.Infrastructure.Configuration;

namespace Switchboard.Tests.Support;

/// <summary>Configuration documents for tests, read through the real YAML reader, defaults and validator.</summary>
internal static class TestConfigurations
{
    public const string Path = "/config/switchboard.yaml";

    public static readonly DateTimeOffset LoadedAt = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<ConfigNode> DefaultsNode = new(EmbeddedControlDefaults.Load);

    public static ConfigNode Defaults => DefaultsNode.Value;

    /// <summary>The committed example for the maintainer's installation.</summary>
    public static string Example => File.ReadAllText(RepoPaths.Combine("deploy", "examples", "switchboard.yaml"));

    /// <summary>The example plus the EV charger's L1 current and power as role-less signals (diagnostics only).</summary>
    public static string ExampleWithCharger => Example.ReplaceLineEndings("\n").Replace(
        "\nsignals:\n",
        """

        signals:
          charger_current:
            entity: sensor.goe_422752_nrg_4
            kind: current
            unit: A
            sign: positive_import
            max_age: 30s
          charger_power:
            entity: sensor.goe_422752_nrg_7
            kind: power
            unit: W
            sign: positive_import
            max_age: 30s

        """.ReplaceLineEndings("\n"),
        StringComparison.Ordinal);

    public static ConfigurationResult Parse(string yaml)
    {
        var read = YamlDocumentReader.Read(yaml, ConfigOrigin.Document);
        return read.Errors.IsEmpty
            ? SwitchboardDocumentParser.Parse(read.Root, Defaults, Path, LoadedAt)
            : ConfigurationResult.Failed(ConfigurationStatus.Invalid, read.Errors, Path, LoadedAt);
    }

    public static ConfigurationResult Valid(string yaml)
    {
        var result = Parse(yaml);
        Assert.True(
            result.Status == ConfigurationStatus.Valid,
            "Expected a valid document, got: " + string.Join("; ", result.Errors));
        return result;
    }

    /// <summary>
    /// A three-phase meter like the reference installation, with short neutral entity names: unsigned
    /// phase currents signed by per-phase power (positive_export), voltages, and an inverter with
    /// per-phase currents. <paramref name="extra"/> is appended under <c>signals:</c> (indent 2),
    /// <paramref name="control"/> becomes the <c>control:</c> block (indent 2).
    /// </summary>
    public static string ThreePhase(string extra = "", string control = "", string currentMaxAge = "90s", bool inverter = true, bool inverterCurrents = true, string characteristic = "B")
    {
        var signals = new System.Text.StringBuilder();
        foreach (var (phase, letter) in new[] { ("L1", "a"), ("L2", "b"), ("L3", "c") })
        {
            var n = phase.ToLowerInvariant();
            signals.Append($"""
                  grid_power_{n}:
                    entity: sensor.meter_power_{letter}
                    kind: power
                    unit: W
                    sign: positive_export
                    max_age: 90s
                    role: phase_power
                    phase: {phase}
                  grid_current_{n}:
                    entity: sensor.meter_current_{letter}
                    kind: current
                    unit: A
                    sign: unsigned
                    direction_from: grid_power_{n}
                    max_age: {currentMaxAge}
                    role: phase_current
                    phase: {phase}
                  grid_voltage_{n}:
                    entity: sensor.meter_voltage_{letter}
                    kind: voltage
                    unit: V
                    max_age: 90s
                    role: phase_voltage
                    phase: {phase}

                """);
            if (inverter && inverterCurrents)
            {
                signals.Append($"""
                      pv_current_{n}:
                        entity: sensor.inverter_current_{letter}
                        kind: current
                        unit: A
                        sign: unsigned
                        direction_from: pv_power
                        max_age: 90s
                        role: inverter_phase_current
                        phase: {phase}

                    """);
            }
        }

        if (inverter)
        {
            signals.Append("""
                  pv_power:
                    entity: sensor.inverter_power
                    kind: power
                    unit: W
                    sign: positive_import
                    max_age: 90s
                    role: inverter_power

                """);
        }

        return $"""
            schema_version: 1
            connection:
              topology: three_phase
              breaker_rating_a: 25
              characteristic: {characteristic}
              voltage_v: 230
              reserve_w: {"{"} L1: 2500, L2: 2500, L3: 2500 {"}"}
            signals:
            {signals}{extra}
            {(control.Length == 0 ? string.Empty : "control:\n" + control)}
            """.ReplaceLineEndings("\n");
    }
}
