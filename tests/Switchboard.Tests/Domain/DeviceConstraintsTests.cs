using System.Collections.Immutable;
using System.Reflection;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// Which constraints a device loads, and whether a stage is on at a level, are answered once, in
/// Domain, for the guard, the effector gate and dispatch (UPGRADE.md "Duplikaty"). Were the answers
/// to drift apart, the guard would hold a device on a phase dispatch does not see, or dispatch would
/// start a device on a group phase the guard does not count. The cases were first run against the
/// three former copies (GuardEngine.KeysOf, EffectorGate.Constraints, DispatchEngine.Constraints)
/// and they agreed on every one.
/// </summary>
public sealed class DeviceConstraintsTests
{
    private static readonly GroupName Carport = GroupName.Parse("carport");

    private static readonly TierTable Tiers = new([
        new TierBand(0.90, null, TierReaction.MayAdd),
        new TierBand(1.00, null, TierReaction.Hold),
        new TierBand(1.13, TimeSpan.FromMinutes(15), TierReaction.ReduceOrdinaryCycle),
        new TierBand(1.25, TimeSpan.FromMinutes(3), TierReaction.ReduceNextCycle),
        new TierBand(1.45, TimeSpan.FromSeconds(30), TierReaction.ShedOneImmediately),
        new TierBand(null, TimeSpan.Zero, TierReaction.ShedAllOnPhase),
    ]);

    private static readonly BreakerProfile Breaker = new(new Amperes(25), BreakerCharacteristic.B, 1.13, Tiers);

    private static readonly ImmutableArray<Phase> All = [Phase.L1, Phase.L2, Phase.L3];

    private static readonly DeviceSpec Heater = Device("heater", DeviceType.OnOff, [Stage(1, Phase.L1)]);

    private static readonly DeviceSpec Boiler = Device("boiler", DeviceType.OnOff, [Stage(1, Phase.L1, Phase.L2, Phase.L3)]);

    // Stages on their own phases, declared out of phase order, two on the same phase.
    private static readonly DeviceSpec Stepped = Device("stepped", DeviceType.Stepped, [Stage(1, Phase.L3), Stage(2, Phase.L1), Stage(3, Phase.L3)]);

    private static readonly DeviceSpec Charger = Device("charger", DeviceType.Modulating, [Stage(1, Phase.L1, Phase.L2, Phase.L3)]) with
    {
        Range = new ModulatingRange(new Amperes(6), new Amperes(16), new Amperes(1)),
    };

    private static readonly DeviceSpec Battery = Device("battery", DeviceType.Storage, [Stage(1, Phase.L2)]) with
    {
        Range = new ModulatingRange(Amperes.Zero, new Amperes(10), new Amperes(1)),
    };

    public static TheoryData<string> Cases => [.. Matrix().Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_device_loads_its_main_phases_then_the_group_phases_its_group_covers(string name)
    {
        // Order is part of the answer: the guard names the first invalid key as a hold's cause and
        // dispatch records headroom per key in this order, so replay digests depend on it.
        var (groups, device, expected) = Matrix()[name];

        Assert.Equal(expected, Setup(groups, device).ConstraintsOf(device));
    }

    [Fact]
    public void A_stepped_device_loads_every_phase_any_stage_uses_whichever_stages_are_on()
    {
        // Constraints are a property of the declaration, not of the level: a stage switched on
        // later loads its phase at once, so the guard must already count and release that phase.
        var grouped = Stepped with { CircuitGroup = Carport };
        var setup = Setup([Group(All)], grouped);

        Assert.Equal([Key(Phase.L1), Key(Phase.L3), In(Phase.L1), In(Phase.L3)], setup.ConstraintsOf(grouped));
        Assert.DoesNotContain(Key(Phase.L2), setup.ConstraintsOf(grouped));
    }

    [Fact]
    public void Guard_gate_and_dispatch_keep_no_copy_of_their_own()
    {
        // A private helper that again maps a device to constraint keys, or reads a stage's on flag,
        // is the duplicate this single answer replaced.
        var offenders = new[] { typeof(GuardEngine), typeof(EffectorGate), typeof(DispatchEngine) }
            .SelectMany(Nested)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(DeviceSpec))
                && (typeof(IEnumerable<ConstraintKey>).IsAssignableFrom(m.ReturnType) || m.Name == nameof(DeviceLevels.StageOn)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_relay_stage_is_on_only_while_its_flag_is_set()
    {
        Assert.True(DeviceLevels.StageOn(Stepped, DeviceLevel.Relay(true, false, false), 1));
        Assert.False(DeviceLevels.StageOn(Stepped, DeviceLevel.Relay(true, false, false), 2));
        Assert.True(DeviceLevels.StageOn(Stepped, DeviceLevel.Relay(false, true, true), 3));

        // A level that does not carry the flag (none at all, a shorter one, a setpoint's) has the
        // stage off: lockouts and records never start on a stage nobody switched on.
        Assert.False(DeviceLevels.StageOn(Stepped, new DeviceLevel(default, null, true), 1));
        Assert.False(DeviceLevels.StageOn(Stepped, DeviceLevel.Relay(true), 2));
        Assert.False(DeviceLevels.StageOn(Stepped, DeviceLevel.At(new Amperes(10)), 1));
    }

    [Fact]
    public void A_setpoint_stage_is_on_only_while_enabled_above_zero()
    {
        // A stopped charger keeps its setpoint number but draws nothing, and zero charge is off:
        // min_on and min_off run from the moment it really starts or stops.
        Assert.True(DeviceLevels.StageOn(Charger, DeviceLevel.At(new Amperes(10)), 1));
        Assert.False(DeviceLevels.StageOn(Charger, DeviceLevel.At(new Amperes(10), enabled: false), 1));
        Assert.False(DeviceLevels.StageOn(Battery, DeviceLevel.At(Amperes.Zero), 1));
        Assert.False(DeviceLevels.StageOn(Charger, new DeviceLevel([], null, true), 1));
        Assert.False(DeviceLevels.StageOn(Charger, DeviceLevel.Relay(true), 1));
    }

    [Fact]
    public void A_relay_ceiling_counts_exactly_the_stages_that_are_on()
    {
        // Protection's load model reads the same flag the gate's lockouts and records read: a stage
        // the gate takes for off never adds current, one it takes for on always does.
        var level = DeviceLevel.Relay(false, true, true);

        Assert.Equal(new Amperes(6), DeviceLevels.Ceiling(Stepped, level, Phase.L1));
        Assert.Equal(new Amperes(6), DeviceLevels.Ceiling(Stepped, level, Phase.L3));
        Assert.Equal(Amperes.Zero, DeviceLevels.Ceiling(Stepped, DeviceLevel.Relay(true), Phase.L1));
    }

    private static Dictionary<string, (ImmutableArray<CircuitGroupSpec> Groups, DeviceSpec Device, ConstraintKey[] Expected)> Matrix()
    {
        static DeviceSpec Grouped(DeviceSpec device) => device with { CircuitGroup = Carport };
        return new()
        {
            ["single_phase"] = ([], Heater, [Key(Phase.L1)]),
            ["three_phase"] = ([], Boiler, [Key(Phase.L1), Key(Phase.L2), Key(Phase.L3)]),
            ["stepped_per_stage_phases_in_phase_order"] = ([], Stepped, [Key(Phase.L1), Key(Phase.L3)]),
            ["stepped_single_and_three_phase_stages"] = ([], Device("mixed", DeviceType.Stepped, [Stage(1, Phase.L2), Stage(2, Phase.L1, Phase.L2, Phase.L3)]), [Key(Phase.L1), Key(Phase.L2), Key(Phase.L3)]),
            ["setpoint_three_phase"] = ([], Charger, [Key(Phase.L1), Key(Phase.L2), Key(Phase.L3)]),
            ["storage"] = ([], Battery, [Key(Phase.L2)]),
            ["single_phase_in_group"] = ([Group(All)], Grouped(Heater), [Key(Phase.L1), In(Phase.L1)]),
            ["three_phase_in_one_phase_group"] = ([Group([Phase.L1])], Grouped(Boiler), [Key(Phase.L1), Key(Phase.L2), Key(Phase.L3), In(Phase.L1)]),
            ["stepped_in_group"] = ([Group(All)], Grouped(Stepped), [Key(Phase.L1), Key(Phase.L3), In(Phase.L1), In(Phase.L3)]),
            ["setpoint_in_group"] = ([Group(All)], Grouped(Charger), [Key(Phase.L1), Key(Phase.L2), Key(Phase.L3), In(Phase.L1), In(Phase.L2), In(Phase.L3)]),
            ["group_on_other_phases"] = ([Group([Phase.L2])], Grouped(Heater), [Key(Phase.L1)]),
            ["group_not_declared"] = ([], Grouped(Heater), [Key(Phase.L1)]),
            ["no_group_beside_a_group"] = ([Group(All)], Heater, [Key(Phase.L1)]),
        };
    }

    private static IEnumerable<Type> Nested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(Nested));

    private static ConstraintKey Key(Phase phase) => ConstraintKey.Main(phase);

    private static ConstraintKey In(Phase phase) => new(Carport, phase);

    private static StageSpec Stage(int index, params Phase[] phases) => new(index, [.. phases], new Amperes(6));

    private static DeviceSpec Device(string name, DeviceType type, ImmutableArray<StageSpec> stages) =>
        new(DeviceName.Parse(name), type, stages, null, 1, null, TimeSpan.FromSeconds(10), false, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60))
        {
            Start = StartPolicy.RestoreOnly,
        };

    private static CircuitGroupSpec Group(ImmutableArray<Phase> phases) =>
        new(Carport, Breaker, phases, phases.ToImmutableDictionary(p => p, _ => Amperes.Zero), phases.ToImmutableDictionary(p => p, _ => TimeSpan.FromSeconds(30)), true, []);

    private static ProtectionSetup Setup(ImmutableArray<CircuitGroupSpec> groups, DeviceSpec device) =>
        new(
            Breaker,
            All,
            All.ToImmutableDictionary(p => p, _ => Amperes.Zero),
            All.ToImmutableDictionary(p => p, _ => TimeSpan.FromSeconds(30)),
            new BudgetParameters(TimeSpan.FromMinutes(10), 0.5, 0.2, 0.05),
            new GuardTiming(TimeSpan.FromSeconds(30), 2, 2, 1),
            [device])
        {
            Groups = groups,
        };
}
