using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The goal as api/plan shows it, byte for byte, for an energy goal and a SoC goal, hard and soft, with and without a deadline. Why: the
/// request's kind-dependent fields (target, progress, since) are reshaped into a target per kind; the JSON the SPA reads must not move.
/// </summary>
public sealed class PlanGoalShapePinTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Json(GoalRequest request)
    {
        var configuration = TestConfigurations.Parse(TestDevices.Document(
            "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    goal:\n      kind: energy\n      target_kwh: 12\n"));
        var response = PlanEndpoint.Build(configuration, new GoalBoard(Now, [Goal.Of(request, Now)], [], []));
        return JsonSerializer.Serialize(Assert.Single(response.Devices).Goal, Web);
    }

    [Fact]
    public void An_energy_goal_shows_its_target_progress_and_since_and_no_soc()
    {
        Assert.Equal("{\"kind\":\"energy\",\"hard\":true,\"source\":\"entity\",\"targetKwh\":12,\"targetSoc\":null,\"currentSoc\":null,\"deadline\":\"2026-10-27T03:00:00+00:00\",\"marginS\":1800,\"remainingKwh\":8.8,\"deliveredKwh\":4,\"status\":\"on_track\",\"inactiveReason\":null,\"inactiveInput\":null,\"onMet\":\"stop\",\"escalate\":true,\"sources\":\"pv_only\",\"since\":\"2026-10-26T16:00:00+00:00\",\"plannedKwh\":null,\"shortfallKwh\":null,\"latestGridStart\":null,\"urgent\":null,\"device\":null,\"cause\":null}", Json(Energy(hard: true, deadline: Now.AddHours(10))));
    }

    [Fact]
    public void A_soft_energy_goal_without_a_deadline_has_a_null_deadline()
    {
        Assert.Equal("{\"kind\":\"energy\",\"hard\":false,\"source\":\"entity\",\"targetKwh\":12,\"targetSoc\":null,\"currentSoc\":null,\"deadline\":null,\"marginS\":1800,\"remainingKwh\":8.8,\"deliveredKwh\":4,\"status\":\"on_track\",\"inactiveReason\":null,\"inactiveInput\":null,\"onMet\":\"stop\",\"escalate\":false,\"sources\":\"pv_only\",\"since\":\"2026-10-26T16:00:00+00:00\",\"plannedKwh\":null,\"shortfallKwh\":null,\"latestGridStart\":null,\"urgent\":null,\"device\":null,\"cause\":null}", Json(Energy(hard: false, deadline: null)));
    }

    [Fact]
    public void A_soc_goal_shows_its_target_and_current_percent_and_no_progress_or_since()
    {
        Assert.Equal("{\"kind\":\"soc\",\"hard\":true,\"source\":\"module\",\"targetKwh\":null,\"targetSoc\":90,\"currentSoc\":40,\"deadline\":\"2026-10-27T03:00:00+00:00\",\"marginS\":0,\"remainingKwh\":6.5,\"deliveredKwh\":null,\"status\":\"on_track\",\"inactiveReason\":null,\"inactiveInput\":null,\"onMet\":\"opportunistic\",\"escalate\":true,\"sources\":null,\"since\":null,\"plannedKwh\":null,\"shortfallKwh\":null,\"latestGridStart\":null,\"urgent\":null,\"device\":null,\"cause\":null}", Json(Soc(hard: true, deadline: Now.AddHours(10))));
    }

    [Fact]
    public void A_soft_soc_goal_without_a_deadline_has_a_null_deadline()
    {
        Assert.Equal("{\"kind\":\"soc\",\"hard\":false,\"source\":\"module\",\"targetKwh\":null,\"targetSoc\":90,\"currentSoc\":40,\"deadline\":null,\"marginS\":0,\"remainingKwh\":6.5,\"deliveredKwh\":null,\"status\":\"on_track\",\"inactiveReason\":null,\"inactiveInput\":null,\"onMet\":\"opportunistic\",\"escalate\":false,\"sources\":null,\"since\":null,\"plannedKwh\":null,\"shortfallKwh\":null,\"latestGridStart\":null,\"urgent\":null,\"device\":null,\"cause\":null}", Json(Soc(hard: false, deadline: null)));
    }

    private static GoalRequest Energy(bool hard, DateTimeOffset? deadline) =>
        new(Car, new EnergyTarget(12, 4, Now.AddHours(-1)), 8.8, deadline, TimeSpan.FromMinutes(30), hard, SourceSet.PvOnly, hard, GoalOnMet.Stop, GoalOrigin.Entity, 5);

    private static GoalRequest Soc(bool hard, DateTimeOffset? deadline) =>
        new(Car, new SocTarget(90, 40), 6.5, deadline, TimeSpan.Zero, hard, null, hard, GoalOnMet.Opportunistic, GoalOrigin.Module, 7);
}
