using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Application.Decisions;

/// <summary>
/// The decision log filters by kind (protection, dispatch, override, verification, foreign load,
/// system). A decision must land in exactly one kind and the one a user expects: the action decides
/// before the origin, because foreign load events and verification records carry origin dispatch.
/// </summary>
public sealed class DecisionKindsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<DecisionAction, DecisionScope, DecisionOrigin?, DecisionKind> Cases() => new()
    {
        { DecisionAction.ModeResolved, DecisionScope.System, null, DecisionKind.System },
        { DecisionAction.KillSwitchEngaged, DecisionScope.System, null, DecisionKind.System },
        { DecisionAction.KillSwitchDisengaged, DecisionScope.System, null, DecisionKind.System },
        { DecisionAction.SafeStateEntered, DecisionScope.System, null, DecisionKind.System },
        { DecisionAction.SafeStateExited, DecisionScope.System, null, DecisionKind.System },
        { DecisionAction.SafeStateEntered, DecisionScope.Phase, null, DecisionKind.Protection },
        { DecisionAction.MarkedUnresponsive, DecisionScope.Device, DecisionOrigin.Dispatch, DecisionKind.Verification },
        { DecisionAction.UnresponsiveCleared, DecisionScope.Device, DecisionOrigin.Dispatch, DecisionKind.Verification },
        { DecisionAction.SettlingTimedOut, DecisionScope.Device, DecisionOrigin.Guard, DecisionKind.Verification },
        { DecisionAction.ForeignLoadDetected, DecisionScope.Phase, DecisionOrigin.Dispatch, DecisionKind.ForeignLoad },
        { DecisionAction.OverrideSet, DecisionScope.Device, DecisionOrigin.Override, DecisionKind.Override },
        { DecisionAction.SwitchOn, DecisionScope.Device, DecisionOrigin.Override, DecisionKind.Override },
        { DecisionAction.SwitchOn, DecisionScope.Device, DecisionOrigin.Dispatch, DecisionKind.Dispatch },
        { DecisionAction.SetSetpoint, DecisionScope.Device, DecisionOrigin.Dispatch, DecisionKind.Dispatch },
        { DecisionAction.Deferred, DecisionScope.Device, null, DecisionKind.Dispatch },
        { DecisionAction.SwitchOff, DecisionScope.Device, DecisionOrigin.Guard, DecisionKind.Protection },
        { DecisionAction.SwitchOff, DecisionScope.Device, null, DecisionKind.Protection },
        { DecisionAction.Release, DecisionScope.Device, DecisionOrigin.Guard, DecisionKind.Protection },
        { DecisionAction.RequestReduction, DecisionScope.Phase, null, DecisionKind.Protection },
        { DecisionAction.CannotReduceExporting, DecisionScope.Phase, null, DecisionKind.Protection },
        { DecisionAction.StopIncreases, DecisionScope.Group, null, DecisionKind.Protection },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Action_and_scope_decide_before_origin(DecisionAction action, DecisionScope scope, DecisionOrigin? origin, DecisionKind expected)
    {
        Assert.Equal(expected, DecisionKinds.Of(Make(action, scope, origin)));
    }

    [Fact]
    public void Every_action_and_origin_maps_to_one_kind()
    {
        // Total over the closed lists: a new action without a mapping would fall silently into
        // protection, so every combination must yield a defined kind.
        foreach (var action in Enum.GetValues<DecisionAction>())
        {
            foreach (var origin in Enum.GetValues<DecisionOrigin>().Select(o => (DecisionOrigin?)o).Append(null))
            {
                Assert.True(Enum.IsDefined(DecisionKinds.Of(Make(action, DecisionScope.Device, origin))), $"{action} {origin}");
            }
        }
    }

    private static Decision Make(DecisionAction action, DecisionScope scope, DecisionOrigin? origin) => new(
        "k-1",
        At,
        scope,
        scope == DecisionScope.Device ? DeviceName.Parse("heater_l1") : null,
        null,
        scope == DecisionScope.Phase ? Phase.L1 : null,
        action,
        ReasonCode.ReserveThreshold,
        null,
        OperatingMode.Shadow,
        false,
        null,
        DecisionInputs.None,
        origin,
        scope == DecisionScope.Group ? Switchboard.Domain.Connection.GroupName.Parse("carport") : null);
}
