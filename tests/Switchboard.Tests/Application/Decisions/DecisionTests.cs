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
    public void Reason_codes_are_exactly_list_version_6()
    {
        // 04 §6, version 6 (2026-10-26): the UI renders a sentence per code, so the list is closed.
        // Versions 3 to 6 only appended, so a log line of an older version keeps its meaning.
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
        ];

        Assert.Equal(6, ReasonCodes.ListVersion);
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

        Assert.True(ReasonCodes.InVersion(ReasonCode.CommandNotSent, ReasonCodes.PreviousListVersion));
        Assert.False(ReasonCodes.InVersion(ReasonCode.CommandNotSent, 7));
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
