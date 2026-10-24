using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// SIG-02: one internal convention (A, W, V, kWh; import positive) whatever the source writes, and
/// a precise refusal instead of a guess when the source writes something unexpected.
/// </summary>
public sealed class SignalNormalizerTests
{
    [Theory]
    [InlineData("A", "12.5", 12.5)]
    [InlineData("mA", "12500", 12.5)]
    [InlineData("A", "-3", -3)]
    public void Milliamperes_become_amperes(string unit, string state, double expected)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Current, unit, SignConvention.PositiveImport), state, unit);

        Assert.Equal(new CurrentValue(new Amperes(expected)), result.Value);
    }

    [Theory]
    [InlineData("W", "1500", 1500)]
    [InlineData("kW", "1.5", 1500)]
    [InlineData("kW", "1e-3", 1)]
    public void Kilowatts_become_watts(string unit, string state, double expected)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Power, unit, SignConvention.PositiveImport), state, unit);

        Assert.Equal(new PowerValue(new Watts(expected)), result.Value);
    }

    [Theory]
    [InlineData("Wh", "2500", 2.5)]
    [InlineData("kWh", "2.5", 2.5)]
    [InlineData("MWh", "0.0025", 2.5)]
    public void Energy_counters_become_kWh(string unit, string state, double expected)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.EnergyCounter, unit, null), state, unit);

        var energy = Assert.IsType<EnergyValue>(result.Value);
        Assert.Equal(expected, energy.Energy.Value, 9);
    }

    [Theory]
    [InlineData("-1089.2", 1089.2)]
    [InlineData("731.5", -731.5)]
    public void Positive_export_meter_reports_import_as_negative_and_is_flipped(string state, double expectedImport)
    {
        // SolarEdge M1 per-phase power: negative while importing. Internally import is positive.
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Power, "W", SignConvention.PositiveExport), state, "W");

        Assert.Equal(new PowerValue(new Watts(expectedImport)), result.Value);
    }

    [Theory]
    [InlineData("kW")]
    [InlineData(null)]
    [InlineData("w")]
    public void Unit_attribute_contradicting_declaration_rejects_because_it_is_a_1000x_error(string? reported)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Power, "W", SignConvention.PositiveImport), "1500", reported);

        Assert.Equal(NormalizationOutcome.Rejected, result.Outcome);
        Assert.Equal(SignalDetail.UnitMismatch, result.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("on")]
    public void Non_numeric_state_is_rejected_not_read_as_zero(string state)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Current, "A", SignConvention.PositiveImport), state, "A");

        Assert.Equal(NormalizationOutcome.Rejected, result.Outcome);
        Assert.Equal(SignalDetail.NotNumeric, result.Detail);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("unavailable", SignalDetail.StateUnavailable)]
    [InlineData("unknown", SignalDetail.StateUnknown)]
    public void Unavailable_state_makes_signal_unavailable(string state, SignalDetail detail)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Current, "A", SignConvention.Unsigned), state, "A");

        Assert.Equal(NormalizationOutcome.Unavailable, result.Outcome);
        Assert.Equal(detail, result.Detail);
    }

    [Fact]
    public void Negative_value_on_unsigned_signal_is_rejected()
    {
        // An "unsigned" source that sends a sign was misdeclared; guessing which sign it meant is unsafe.
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Current, "A", SignConvention.Unsigned), "-4.2", "A");

        Assert.Equal(SignalDetail.SignViolation, result.Detail);
    }

    [Fact]
    public void Unsigned_value_stays_a_magnitude_until_its_direction_is_known()
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Current, "mA", SignConvention.Unsigned), "5437", "mA");

        Assert.Equal(new CurrentValue(new Amperes(5.437)), result.Value);
    }

    [Fact]
    public void State_signal_keeps_its_text()
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.State, null, null), "on", null);

        Assert.Equal(new StateValue("on"), result.Value);
    }

    private static SignalDefinition Definition(SignalKind kind, string? unit, SignConvention? sign) =>
        new(SignalName.Parse("test"), EntityId.Parse("sensor.test"), kind, unit, sign, null, false, TimeSpan.FromSeconds(90), null, SignalRole.None, null, false);
}
