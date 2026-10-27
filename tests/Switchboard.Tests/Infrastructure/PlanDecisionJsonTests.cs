using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The <c>plan</c> block of a dispatch decision (PLAN-15, reason-code list version 7) in the decision log: the only record of which plan entry a decision was made
/// beside. Why it matters: the shadow week is judged from these lines (did the plan's Defer hold the car back, what did Urgent open), so a block that is lost,
/// invented, or read from a line that could not have written it would misreport what the planner did.
/// </summary>
public sealed class PlanDecisionJsonTests
{
    private static readonly DeviceName Dev = DeviceName.Parse("dev");

    /// <summary>The decision of the first start of a device that only an urgent plan opens, in full mode.</summary>
    private static Decision UrgentStart()
    {
        var view = new ScriptedPlanView(PlannerDispatchMode.Full);
        var s = Create(Device("dev", "L1", 6.6, 1500, 0.35, "pv_only"), Workday(11), ["dev"], plan: view);
        view.Entries[Dev] = ScriptedPlanView.Entry(s.Now, PlanWant.Urgent, "zone:morning_peak", energyKwh: 1.25, maxPowerW: 1500);
        Export(s, -500);
        var feed = Feed(s, -100);
        feed();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 1, 1, feed);
        return Starts(s, "dev").First();
    }

    [Fact]
    public void The_plan_block_round_trips_through_the_log_line_and_the_line_says_list_version_7()
    {
        // Why: the Log page and the API after a restart read the block back from the file; a round trip that changed it would show a different plan than the one used.
        var decision = UrgentStart();
        var written = decision.Inputs.Dispatch!.Plan!;

        var line = DecisionJson.Serialize(decision);
        var read = DecisionJson.TryParse(line)!;

        Assert.Equal(written, read.Inputs.Dispatch!.Plan);
        Assert.Equal(line, DecisionJson.Serialize(read));
        Assert.Contains("\"codes\":7", line, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"goal_deadline_urgent\"", line, StringComparison.Ordinal);
        Assert.Contains("\"want\":\"urgent\"", line, StringComparison.Ordinal);
        Assert.Contains("\"class\":\"zone:morning_peak\"", line, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"full\"", line, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"fresh\"", line, StringComparison.Ordinal);
        Assert.Equal((1.25, 1500, "plan-1"), (read.Inputs.Dispatch.Plan!.EnergyKwh, read.Inputs.Dispatch.Plan.MaxPowerW, read.Inputs.Dispatch.Plan.PlanId));
    }

    [Fact]
    public void A_decision_made_with_the_mode_off_writes_no_plan_key_and_reads_as_none()
    {
        // Why: off is v2 byte for byte: the line carries no trace of a planner, and a line of an older version (which has no key) keeps reading unchanged.
        var view = new ScriptedPlanView(PlannerDispatchMode.Off);
        var s = Create(Device("dev", "L1", 6.6, 1500, 2.03, "any"), Workday(11), ["dev"], plan: view);
        view.Entries[Dev] = ScriptedPlanView.Entry(s.Now, PlanWant.Urgent, "zone:morning_peak");
        Export(s, -500);
        var feed = Feed(s, -100);
        feed();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 1, 1, feed);
        var start = Starts(s, "dev").First();

        var line = DecisionJson.Serialize(start);

        Assert.Null(start.Inputs.Dispatch!.Plan);
        Assert.DoesNotContain("\"plan\"", line, StringComparison.Ordinal);
        Assert.Contains("\"codes\":7", line, StringComparison.Ordinal);
        Assert.Null(DecisionJson.TryParse(line)!.Inputs.Dispatch!.Plan);
        Assert.Equal(line, DecisionJson.Serialize(DecisionJson.TryParse(line)!));
    }

    [Fact]
    public void A_plan_block_in_a_line_of_an_older_list_version_is_not_a_decision()
    {
        // Why: no add-on of version 6 or earlier could have written a plan block; a line that carries one under an older list was edited, not written by us.
        var line = DecisionJson.Serialize(UrgentStart());
        Assert.NotNull(DecisionJson.TryParse(line));

        foreach (var older in new[] { 6, 5, 4, 3, 2 })
        {
            Assert.Null(DecisionJson.TryParse(line.Replace("\"codes\":7", $"\"codes\":{older}", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData("\"want\":\"urgent\"", "\"want\":\"someday\"")]
    [InlineData("\"mode\":\"full\"", "\"mode\":\"off\"")]
    [InlineData("\"status\":\"fresh\"", "\"status\":\"stale\"")]
    [InlineData("\"energy_kwh\":1.25", "\"energy_kwh\":-1")]
    [InlineData("\"max_power_w\":1500", "\"max_power_w\":\"1500\"")]
    [InlineData("\"class\":\"zone:morning_peak\"", "\"class\":\"grid\"")]
    [InlineData("\"plan_id\":\"plan-1\"", "\"plan_id\":7")]
    [InlineData("\"slot_start\":", "\"slot_begin\":")]
    public void A_malformed_plan_block_makes_the_line_no_decision(string good, string bad)
    {
        // Why: as for balance and economics, a block of a known shape that is wrong must not be read as a decision with a block quietly dropped: the line is skipped.
        var line = DecisionJson.Serialize(UrgentStart());
        Assert.Contains(good, line, StringComparison.Ordinal);

        Assert.Null(DecisionJson.TryParse(line.Replace(good, bad, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_new_reason_codes_are_written_and_read_in_a_line_of_version_7()
    {
        // Why: the Log page filters by reason; each code of version 7 has to survive the file.
        var decision = UrgentStart();
        foreach (var code in new[] { ReasonCode.PlanDeferred, ReasonCode.GoalDeadlineUrgent, ReasonCode.PlanDischarge, ReasonCode.SocFloor, ReasonCode.SocCeiling, ReasonCode.PlanSlotTarget, ReasonCode.GoalMet })
        {
            var line = DecisionJson.Serialize(decision).Replace("\"reason\":\"goal_deadline_urgent\"", $"\"reason\":\"{ReasonCodes.Name(code)}\"", StringComparison.Ordinal);

            Assert.Equal(code, DecisionJson.TryParse(line)!.Reason);
        }
    }
}
