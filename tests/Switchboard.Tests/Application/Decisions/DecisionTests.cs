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
    public void Reason_codes_are_exactly_list_version_3()
    {
        // 04 §6, version 3 (2026-10-25): the UI renders a sentence per code, so the list is closed.
        // Version 3 only appended, so a version-2 log line keeps its meaning.
        string[] specification =
        [
            "guard_tier_exceeded", "guard_budget_low", "guard_stale_signal",
            "reserve_threshold", "headroom_insufficient", "lockout_min_on",
            "lockout_min_off", "settling_window_open", "awaiting_confirmation",
            "value_rank_below_cutoff", "goal_met", "guard_expression_false",
            "device_unresponsive", "manual_override", "mode_shadow",
            "safe_state", "plan_slot_target", "headroom_available",
            "kill_switch", "foreign_load", "change_limit",
        ];

        Assert.Equal(3, ReasonCodes.ListVersion);
        Assert.Equal(specification, ReasonCodes.All.Select(ReasonCodes.Name));
        Assert.False(ReasonCodes.IsDefined(default));
        Assert.True(ReasonCodes.InVersion(ReasonCode.KillSwitch, ReasonCodes.PreviousListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.ForeignLoad, ReasonCodes.PreviousListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.ChangeLimit, ReasonCodes.PreviousListVersion));
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
        new(
            "boot-000000001",
            At,
            DecisionScope.Device,
            device is null ? null : DeviceName.Parse(device),
            null,
            null,
            DecisionAction.SwitchOff,
            reason,
            overridden,
            OperatingMode.Shadow,
            false,
            refusedBy,
            DecisionInputs.None);
}
