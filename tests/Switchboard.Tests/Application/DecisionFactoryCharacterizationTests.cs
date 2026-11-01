using System.Globalization;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application;

/// <summary>
/// Every field of every decision the factory builds without a step context, pinned before the
/// 16-argument constructor became factories (UPGRADE.md "Decision"). A swap of two reason-code
/// arguments, or of origin and group, changes one of these lines. The factory methods that need a
/// step context (<c>Hold</c>, <c>Device</c>) are pinned by the golden traces of the gate.
/// </summary>
public sealed class DecisionFactoryCharacterizationTests
{
    private const string Stamp = "boot1-000000001|2026-11-01T12:00:00.0000000+00:00";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly GroupName Carport = GroupName.Parse("carport");

    private static readonly RelaySpec Heater = new(
        DeviceName.Parse("heater"),
        DeviceType.OnOff,
        [new StageSpec(1, [Phase.L2], new Amperes(6.6))],
        0.35,
        null,
        TimeSpan.FromSeconds(1),
        false,
        TimeSpan.FromMinutes(6),
        TimeSpan.FromSeconds(90))
    {
        Start = StartPolicy.RestoreOnly,
        CircuitGroup = Carport,
    };

    private static readonly RelaySpec Stepped = new(
        DeviceName.Parse("stepped"),
        DeviceType.Stepped,
        [new StageSpec(1, [Phase.L1], new Amperes(6.6)), new StageSpec(2, [Phase.L3], new Amperes(4.4))],
        0.35,
        null,
        TimeSpan.FromSeconds(1),
        false,
        TimeSpan.FromMinutes(6),
        TimeSpan.FromSeconds(90))
    {
        Start = StartPolicy.RestoreOnly,
    };

    private static DecisionFactory Factory() => new(new DecisionIdGenerator("boot1"), new LockoutClocks(), commandPath: true);

    private static ModeStatus Mode(OperatingMode effective, params ModeCondition[] missing) =>
        new(OperatingMode.Active, true, !missing.Contains(ModeCondition.ConfigurationInvalid), true, effective, [.. missing], ModeNote.None);

    [Fact]
    public void Mode_decision_is_a_system_event_with_the_resolved_mode_inputs()
    {
        var d = Factory().Mode(Mode(OperatingMode.Shadow, ModeCondition.ConfigurationInvalid), null, Now);

        Assert.Equal($"{Stamp}|System||||ModeResolved|SafeState||Shadow|False||||", Line(d));
        Assert.NotNull(d.Inputs.Mode);
        Assert.Equal(OperatingMode.Active, d.Inputs.Mode!.Ceiling);
    }

    [Fact]
    public void Dispatch_fault_is_a_system_stop_increases_of_dispatch_origin_in_the_recorded_mode()
    {
        var d = Factory().DispatchFault(Mode(OperatingMode.Active), Now);

        Assert.Equal($"{Stamp}|System||||StopIncreases|DispatchFault||Active|False||Dispatch||", Line(d));
        Assert.Equal(DecisionInputs.None, d.Inputs);
    }

    [Fact]
    public void Guard_event_keeps_scope_phase_group_and_inputs_with_no_origin()
    {
        var inputs = DecisionInputs.None with { SafeStateCause = SafeStateCause.Shutdown };
        var d = Factory().FromGuard(new GuardEvent(DecisionScope.Group, Phase.L2, DecisionAction.RequestReduction, ReasonCode.SafeState, inputs) { Group = Carport }, OperatingMode.Shadow, Now);

        Assert.Equal($"{Stamp}|Group|||L2|RequestReduction|SafeState||Shadow|False|||carport|", Line(d));
        Assert.Equal(inputs, d.Inputs);
    }

    [Fact]
    public void Dispatch_event_has_dispatch_origin_and_no_group()
    {
        var d = Factory().FromDispatch(new DispatchEvent(DecisionScope.Phase, Phase.L3, DecisionAction.StopIncreases, ReasonCode.DispatchFault, DecisionInputs.None), OperatingMode.Active, Now);

        Assert.Equal($"{Stamp}|Phase|||L3|StopIncreases|DispatchFault||Active|False||Dispatch||", Line(d));
    }

    [Fact]
    public void Self_test_off_names_the_stage_only_of_a_stepped_device_and_the_first_phase()
    {
        var single = Factory().SelfTestOff(Heater, 1, DeviceLevel.Relay(false), Now);
        var stepped = Factory().SelfTestOff(Stepped, 2, DeviceLevel.Relay(false), Now);

        Assert.Equal($"{Stamp}|Device|heater||L2|SwitchOff|ManualOverride||Active|False||SelfTest|carport|{Off}", Line(single));
        Assert.Equal($"{Stamp}|Device|stepped|2|L1|SwitchOff|ManualOverride||Active|False||SelfTest||{Off}", Line(stepped));
    }

    [Fact]
    public void Removed_off_is_a_dispatch_switch_off_in_safe_state_of_the_first_phase_and_group()
    {
        var d = Factory().RemovedOff(Heater, DeviceName.Parse("gone"), OperatingMode.Shadow, Now, DeviceLevel.Relay(false));

        Assert.Equal($"{Stamp}|Device|gone||L2|SwitchOff|SafeState||Shadow|False||Dispatch|carport|{Off}", Line(d));
    }

    [Fact]
    public void Removed_release_is_a_guard_release_with_no_phase_and_no_group()
    {
        var d = Factory().RemovedRelease(DeviceName.Parse("gone"), OperatingMode.Active, Now, null);

        Assert.Equal($"{Stamp}|Device|gone|||Release|SafeState||Active|False||Guard||", Line(d));
    }

    [Theory]
    [InlineData(GuardTarget.Off, "SwitchOff")]
    [InlineData(GuardTarget.AtMinimum, "ReduceToMinimum")]
    [InlineData(GuardTarget.Released, "Release")]
    public void Intent_record_maps_the_target_and_keeps_overridden_and_refusal_apart(GuardTarget target, string action)
    {
        var intent = new GuardIntent(DeviceName.Parse("heater"), 2, target, ReasonCode.SafeState, Phase.L1, IntentUrgency.Protective, ReasonCode.LockoutMinOn, DecisionInputs.None) { Group = Carport };

        var d = Factory().Intent(intent, ReasonCode.LockoutMinOn, ReasonCode.ModeShadow, OperatingMode.Shadow, Now, DeviceLevel.Relay(false));

        Assert.Equal($"{Stamp}|Device|heater|2|L1|{action}|SafeState|LockoutMinOn|Shadow|False|ModeShadow|Guard|carport|{Off}", Line(d));
    }

    [Fact]
    public void With_command_changes_only_the_outcome()
    {
        var d = Factory().Intent(
            new GuardIntent(DeviceName.Parse("heater"), null, GuardTarget.Off, ReasonCode.SafeState, null, IntentUrgency.Protective, null, DecisionInputs.None), ReasonCode.LockoutMinOn, ReasonCode.ModeShadow, OperatingMode.Active, Now, null);

        var sent = d.WithCommand(true, null);
        var refused = d.WithCommand(false, ReasonCode.LockoutMinOn);

        Assert.Equal($"{Stamp}|Device|heater|||SwitchOff|SafeState|LockoutMinOn|Active|True||Guard||", Line(sent));
        Assert.Equal($"{Stamp}|Device|heater|||SwitchOff|SafeState|LockoutMinOn|Active|False|LockoutMinOn|Guard||", Line(refused));
    }

    [Fact]
    public void Json_round_trip_keeps_every_field_of_a_fully_populated_decision()
    {
        var d = Factory().Intent(
            new GuardIntent(DeviceName.Parse("heater"), 2, GuardTarget.AtMinimum, ReasonCode.SafeState, Phase.L1, IntentUrgency.Protective, ReasonCode.LockoutMinOn, DecisionInputs.None) { Group = Carport },
            ReasonCode.LockoutMinOn,
            ReasonCode.ModeShadow,
            OperatingMode.Shadow,
            Now,
            DeviceLevel.At(new Amperes(8)));

        var line = DecisionJson.Serialize(d);
        var back = DecisionJson.TryParse(line);

        Assert.NotNull(back);
        Assert.Equal(Line(d), Line(back!));
        Assert.Equal(line, DecisionJson.Serialize(back!));
    }

    [Fact]
    public void The_constructor_that_enforces_the_closed_reason_list_is_the_only_one_and_is_private()
    {
        // A public way around the factories would be a way around the reason check's single point.
        Assert.Empty(typeof(Decision).GetConstructors());
        Assert.Single(typeof(Decision).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), c => c.IsPrivate && c.GetParameters().Length == 16);
    }

    private static string Off => DeviceLevel.Relay(false).ToString()!;

    private static string Line(Decision d) =>
        string.Join(
            '|',
            d.Id,
            d.At.ToString("O", CultureInfo.InvariantCulture),
            d.Scope,
            d.Device?.ToString(),
            d.Stage?.ToString(CultureInfo.InvariantCulture),
            d.Phase,
            d.Action,
            d.Reason,
            d.Overridden,
            d.Mode,
            d.Commanded,
            d.RefusedBy,
            d.Origin,
            d.Group?.ToString(),
            d.Target?.ToString());
}
