using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// What the device types refuse to be built as (UPGRADE.md "Niezmienniki pilnowane przez wołających"):
/// a setpoint range whose step divides the level count, a device whose type does not belong to its
/// kind, a sample that is not fresh yet carries a state. A caller used to guard each of these; now
/// the value cannot exist.
/// </summary>
public sealed class DeviceModelInvariantTests
{
    private static readonly DeviceName Name = DeviceName.Parse("device");

    private static readonly ImmutableArray<StageSpec> Stage = [new StageSpec(1, [Phase.L1], new Amperes(16))];

    [Theory]
    [InlineData(6, 16, 0)]
    [InlineData(6, 16, -1)]
    [InlineData(6, 16, double.NaN)]
    [InlineData(6, 16, double.PositiveInfinity)]
    [InlineData(6, double.PositiveInfinity, 1)]
    [InlineData(double.NaN, 16, 1)]
    [InlineData(16, 6, 1)]
    public void A_setpoint_range_needs_a_positive_finite_step_and_a_maximum_not_below_the_minimum(double min, double max, double step)
    {
        // Levels count (max − floor) / step: a zero step divides by zero (DeviceLevels.All), a negative
        // one counts backwards, a NaN or an infinity turns the count into garbage.
        Assert.ThrowsAny<ArgumentException>(() => new ModulatingRange(new Amperes(min), new Amperes(max), new Amperes(step)));
    }

    [Theory]
    [InlineData(6, 16, 1)]
    [InlineData(-10, 16, 2)]
    [InlineData(6, 6, 0.5)]
    public void A_valid_setpoint_range_keeps_its_values(double min, double max, double step)
    {
        var range = new ModulatingRange(new Amperes(min), new Amperes(max), new Amperes(step));

        Assert.Equal((min, max, step), (range.Min.Value, range.Max.Value, range.Step.Value));
        Assert.Equal(range, new ModulatingRange(new Amperes(min), new Amperes(max), new Amperes(step)));
    }

    [Fact]
    public void A_range_that_cannot_be_built_never_reaches_the_level_arithmetic()
    {
        // The step the old arithmetic divided by is the one the type now refuses.
        var range = new ModulatingRange(new Amperes(6), new Amperes(6), new Amperes(1));
        var spec = new SetpointSpec(Name, DeviceType.Modulating, Stage, range, 1, null, TimeSpan.Zero, false, TimeSpan.Zero, TimeSpan.Zero) { Start = StartPolicy.RestoreOnly };

        Assert.Equal(["6 A"], DeviceLevels.All(spec).Select(l => l.ToString()));
    }

    [Fact]
    public void A_relay_spec_is_onoff_or_stepped_and_a_setpoint_spec_modulating_or_storage()
    {
        var range = new ModulatingRange(new Amperes(6), new Amperes(16), new Amperes(1));

        foreach (var type in Enum.GetValues<DeviceType>())
        {
            var relay = Accepts(() => new RelaySpec(Name, type, Stage, 1, null, TimeSpan.Zero, false, TimeSpan.Zero, TimeSpan.Zero) { Start = StartPolicy.RestoreOnly });
            var setpoint = Accepts(() => new SetpointSpec(Name, type, Stage, range, 1, null, TimeSpan.Zero, false, TimeSpan.Zero, TimeSpan.Zero) { Start = StartPolicy.RestoreOnly });

            // Every configured type belongs to exactly one kind: a new type forces the decision here.
            Assert.True(relay ^ setpoint, $"{type} must belong to exactly one device kind.");
            Assert.Equal(type is DeviceType.OnOff or DeviceType.Stepped, relay);
        }
    }

    [Fact]
    public void The_device_and_level_kinds_are_exactly_two_each()
    {
        // C# cannot check a switch over an abstract record for exhaustiveness, so the code that
        // matches on the kind ends in a throw. A third kind must fail here first: revisit every
        // `_ => throw new UnreachableException()` that matches on DeviceSpec or DeviceLevel.
        Assert.Equal([typeof(RelaySpec), typeof(SetpointSpec)], Subtypes(typeof(DeviceSpec)));
        Assert.Equal([typeof(RelayLevel), typeof(SetpointLevel)], Subtypes(typeof(DeviceLevel)));
        Assert.All(Subtypes(typeof(DeviceSpec)).Concat(Subtypes(typeof(DeviceLevel))), t => Assert.True(t.IsSealed));
    }

    [Fact]
    public void A_sample_that_is_not_fresh_cannot_carry_an_applied_state()
    {
        // A stale "off" must never exclude a device from protection: the type refuses to hold one.
        Assert.Throws<ArgumentException>(() => new DeviceStateSample(Name, 1, false, DeviceStateInvalidReason.Stale, new RelayApplied(false), null));
        Assert.Throws<ArgumentException>(() => new DeviceStateSample(Name, 1, false, null, new SetpointApplied(Amperes.Zero), null));
    }

    [Fact]
    public void A_fresh_state_and_an_unknown_one_are_built_by_name()
    {
        var at = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        var known = DeviceStateSample.Known(Name, 2, new RelayApplied(true), at);
        var unknown = DeviceStateSample.Unknown(Name, 2, DeviceStateInvalidReason.Awaiting, at);

        Assert.Equal((true, null, new RelayApplied(true), at), (known.Fresh, known.InvalidReason, known.Applied, known.ReportedAt));
        Assert.Equal((false, DeviceStateInvalidReason.Awaiting, null, at), (unknown.Fresh, unknown.InvalidReason, unknown.Applied, unknown.ReportedAt));
        Assert.Equal(new DeviceStateSample(Name, 2, true, null, new RelayApplied(true), at), known);
        Assert.Null(new DeviceStateSample(Name, 2, true, null, null, null).Applied);
    }

    private static bool Accepts(Func<DeviceSpec> build)
    {
        try
        {
            build();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Type[] Subtypes(Type baseType) => [.. baseType.Assembly.GetTypes().Where(t => t.IsSubclassOf(baseType)).OrderBy(t => t.Name)];
}
