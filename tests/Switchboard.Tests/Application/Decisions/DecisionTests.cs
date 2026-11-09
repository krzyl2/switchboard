using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;

namespace Switchboard.Tests.Application.Decisions;

/// <summary>
/// CLAUDE.md: every decision carries a reason code from the closed list of 04 §6 (version 2); a
/// decision without one is a bug. Here it cannot even be constructed.
/// </summary>
public sealed class DecisionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decision_without_reason_cannot_be_constructed()
    {
        Assert.Throws<ArgumentException>(() => Build(reason: default));
        Assert.Throws<ArgumentException>(() => Build(reason: (ReasonCode)99));
        Assert.Throws<ArgumentException>(() => Build(overridden: (ReasonCode)0));
        Assert.Throws<ArgumentException>(() => Build(refusedBy: (ReasonCode)42));
        Assert.Throws<ArgumentException>(() => Build(device: null));
        Assert.Equal(ReasonCode.GuardTierExceeded, Build().Reason);
    }

    [Fact]
    public void Reason_codes_are_exactly_list_version_10()
    {
        // 04 §6, version 10 (2026-11-09): the UI renders a sentence per code, so the list is closed.
        // Versions 3 to 10 only appended, so a log line of an older version keeps its meaning.
        string[] specification =
        [
            "guard_tier_exceeded", "guard_budget_low", "guard_stale_signal",
            "reserve_threshold", "headroom_insufficient", "lockout_min_on",
            "lockout_min_off", "settling_window_open", "awaiting_confirmation",
            "value_rank_below_cutoff", "goal_met", "guard_expression_false",
            "device_unresponsive", "manual_override", "mode_shadow",
            "safe_state", "plan_slot_target", "headroom_available",
            "kill_switch", "foreign_load", "change_limit",
            "balance_projection_low", "balance_surplus", "balance_unknown",
            "mode_active", "command_failed", "command_not_sent",
            "zone_allowed", "source_not_allowed", "value_below_cost",
            "surplus_available", "surplus_low",
            "plan_deferred", "goal_deadline_urgent", "plan_discharge",
            "soc_floor", "soc_ceiling",
            "dispatch_fault",
            "power_band",
            "device_not_ready",
        ];

        Assert.Equal(10, ReasonCodes.ListVersion);
        Assert.Equal(9, ReasonCodes.PowerBandListVersion);
        Assert.Equal(8, ReasonCodes.DispatchFaultListVersion);
        Assert.Equal(7, ReasonCodes.PlannerListVersion);
        Assert.Equal(6, ReasonCodes.EconomicsListVersion);
        Assert.Equal((5, 4, 3, 2), (ReasonCodes.PreviousListVersion, ReasonCodes.EarlierListVersion, ReasonCodes.OlderListVersion, ReasonCodes.OldestListVersion));
        Assert.Equal(specification, ReasonCodes.All.Select(ReasonCodes.Name));
        Assert.False(ReasonCodes.IsDefined(default));
        Assert.True(ReasonCodes.InVersion(ReasonCode.KillSwitch, ReasonCodes.OldestListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.ForeignLoad, ReasonCodes.OldestListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.ChangeLimit, ReasonCodes.OldestListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.ChangeLimit, ReasonCodes.OlderListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.BalanceProjectionLow, ReasonCodes.OlderListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.BalanceProjectionLow, ReasonCodes.EarlierListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.ModeActive, ReasonCodes.EarlierListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.CommandFailed, ReasonCodes.OlderListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.CommandNotSent, ReasonCodes.EarlierListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.CommandNotSent, ReasonCodes.PreviousListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.CommandNotSent, ReasonCodes.ListVersion));

        // Version 6 appended the economic codes: no older version has them, and the last of version 5 is where version 5 ended.
        foreach (var code in new[] { ReasonCode.ZoneAllowed, ReasonCode.SourceNotAllowed, ReasonCode.ValueBelowCost, ReasonCode.SurplusAvailable, ReasonCode.SurplusLow })
        {
            Assert.True(ReasonCodes.InVersion(code, ReasonCodes.ListVersion));
            Assert.All(
                new[] { ReasonCodes.PreviousListVersion, ReasonCodes.EarlierListVersion, ReasonCodes.OlderListVersion, ReasonCodes.OldestListVersion },
                version => Assert.False(ReasonCodes.InVersion(code, version)));
        }

        // Version 7 appended the planner codes: version 6 and every older version lack them, and the economic codes of version 6 are still there.
        foreach (var code in new[] { ReasonCode.PlanDeferred, ReasonCode.GoalDeadlineUrgent, ReasonCode.PlanDischarge, ReasonCode.SocFloor, ReasonCode.SocCeiling })
        {
            Assert.True(ReasonCodes.InVersion(code, ReasonCodes.ListVersion));
            Assert.All(
                new[] { ReasonCodes.EconomicsListVersion, ReasonCodes.PreviousListVersion, ReasonCodes.EarlierListVersion, ReasonCodes.OlderListVersion, ReasonCodes.OldestListVersion },
                version => Assert.False(ReasonCodes.InVersion(code, version)));
        }

        // Version 8 appended dispatch_fault: no older version has it, and version 7 still has every planner code.
        Assert.True(ReasonCodes.InVersion(ReasonCode.DispatchFault, ReasonCodes.ListVersion));
        Assert.All(
            new[] { ReasonCodes.PlannerListVersion, ReasonCodes.EconomicsListVersion, ReasonCodes.PreviousListVersion, ReasonCodes.EarlierListVersion, ReasonCodes.OlderListVersion, ReasonCodes.OldestListVersion },
            version => Assert.False(ReasonCodes.InVersion(ReasonCode.DispatchFault, version)));
        Assert.True(ReasonCodes.InVersion(ReasonCode.SocCeiling, ReasonCodes.PlannerListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.SurplusLow, ReasonCodes.PlannerListVersion));

        // Version 9 appended power_band: no older version has it, and version 8 still has dispatch_fault.
        Assert.True(ReasonCodes.InVersion(ReasonCode.PowerBand, ReasonCodes.ListVersion));
        Assert.All(
            new[] { ReasonCodes.DispatchFaultListVersion, ReasonCodes.PlannerListVersion, ReasonCodes.EconomicsListVersion, ReasonCodes.PreviousListVersion, ReasonCodes.EarlierListVersion, ReasonCodes.OlderListVersion, ReasonCodes.OldestListVersion },
            version => Assert.False(ReasonCodes.InVersion(ReasonCode.PowerBand, version)));
        Assert.True(ReasonCodes.InVersion(ReasonCode.DispatchFault, ReasonCodes.DispatchFaultListVersion));

        // Version 10 appended device_not_ready: no older version has it, and version 9 still has power_band.
        Assert.True(ReasonCodes.InVersion(ReasonCode.DeviceNotReady, ReasonCodes.ListVersion));
        Assert.All(
            new[] { ReasonCodes.PowerBandListVersion, ReasonCodes.DispatchFaultListVersion, ReasonCodes.PlannerListVersion, ReasonCodes.EconomicsListVersion, ReasonCodes.PreviousListVersion, ReasonCodes.EarlierListVersion, ReasonCodes.OlderListVersion, ReasonCodes.OldestListVersion },
            version => Assert.False(ReasonCodes.InVersion(ReasonCode.DeviceNotReady, version)));
        Assert.True(ReasonCodes.InVersion(ReasonCode.PowerBand, ReasonCodes.PowerBandListVersion));

        Assert.True(ReasonCodes.InVersion(ReasonCode.SurplusLow, ReasonCodes.EconomicsListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.SurplusLow, ReasonCodes.PreviousListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.PlanSlotTarget, ReasonCodes.OldestListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.GoalMet, ReasonCodes.OldestListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.CommandNotSent, ReasonCodes.PreviousListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.CommandNotSent, 11));
        Assert.False(ReasonCodes.InVersion((ReasonCode)(int.MaxValue), ReasonCodes.ListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.BalanceUnknown, ReasonCodes.OldestListVersion));
        Assert.True(ReasonCodes.InVersion(ReasonCode.BalanceSurplus, ReasonCodes.ListVersion));
    }

    [Fact]
    public void Decision_ids_are_unique_across_clock_steps()
    {
        // A timestamp would repeat after a backward clock step; boot id plus sequence never does.
        var ids = new DecisionIdGenerator("a1b2c3");

        var issued = Enumerable.Range(0, 1000).Select(_ => ids.Next()).ToList();

        Assert.Equal(issued.Count, issued.Distinct().Count());
        Assert.All(issued, id => Assert.StartsWith("a1b2c3-", id, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new DecisionIdGenerator("not a boot id"));
    }

    private static Decision Build(
        ReasonCode reason = ReasonCode.GuardTierExceeded,
        ReasonCode? overridden = null,
        ReasonCode? refusedBy = ReasonCode.ModeShadow,
        string? device = "heater_l1") =>
        Decision.Restore(
            "boot-000000001",
            At,
            DecisionScope.Device,
            device is null ? null : DeviceName.Parse(device),
            null,
            null,
            DecisionAction.SwitchOff,
            reason,
            OverriddenRule.Of(overridden),
            OperatingMode.Shadow,
            false,
            RefusalReason.Of(refusedBy),
            DecisionInputs.None,
            null,
            null,
            null);
}
