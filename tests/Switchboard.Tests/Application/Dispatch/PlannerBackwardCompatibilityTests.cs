using System.Text;
using Switchboard.Application.Configuration;
using Switchboard.Tests.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// PLAN-01 and PLAN-05 acceptance, slice 1: a 0.10 file, with or without a goal and a planner section, decides byte for byte as before. The
/// digests of <see cref="EconomicsBackwardCompatibilityTests"/> pin the plain file to the 0.9 engine; here the same synthetic morning is run
/// with a goal on the charger, whose inputs (a state of charge, a deadline, a want gate) never arrive, and with <c>planner: dispatch: full</c>,
/// and its digest must equal the plain one. In this slice a goal is shown and a planner mode is echoed, nothing more: a goal input that is
/// missing, or a mode that says "full", must not move a decision, hold a device, or enter a safe state.
/// </summary>
public sealed class PlannerBackwardCompatibilityTests(DispatchSyntheticReplayRun run) : IClassFixture<DispatchSyntheticReplayRun>
{
    private const string ChargerLine = "    value_per_kwh: 2.03\n";

    private const string EntityGoal =
        "    goal:\n      kind: soc\n      hard: true\n      capacity_kwh: 18\n      loss_fraction: 0.10\n      soc: { entity: sensor.car_battery_level, max_age: 30m }\n      target: { value: 90 }\n      deadline: { entity: input_datetime.car_departure, margin: 30m }\n      want: { entity: input_boolean.car_charge_wanted }\n";

    private const string EnergyGoal = "    goal:\n      kind: energy\n      target_kwh: 12\n      hard: true\n      deadline: { at: \"06:30\" }\n";

    private static string Digest(DispatchSyntheticReplayRun run, string yaml)
    {
        var harness = new GuardReplayHarness(run.Data, yaml, dispatch: true);
        harness.RunUntil(run.Data.End);
        var text = new StringBuilder();
        foreach (var step in harness.Steps)
        {
            EconomicsBackwardCompatibilityTests.Add(text, step.Decisions, step.Dispatch);
        }

        return EconomicsBackwardCompatibilityTests.Sha(text);
    }

    private static string WithCharger(string extra)
    {
        var yaml = DispatchSyntheticReplayRun.HeadroomConfiguration;
        Assert.Contains(ChargerLine, yaml, StringComparison.Ordinal);
        return yaml.Replace(ChargerLine, ChargerLine + extra, StringComparison.Ordinal);
    }

    [Fact]
    public void A_goal_whose_inputs_never_arrive_and_a_planner_set_to_full_decide_the_synthetic_morning_exactly_as_the_plain_file_does()
    {
        // Why: the slice's promise. Both files parse as valid with their new keys, and the morning's every decision, wish, device state and
        // balance view is the same as without them. If a goal or a planner mode reached the guard or dispatch, or a goal signal that never
        // reports held anything back, this digest would move.
        var plain = Digest(run, DispatchSyntheticReplayRun.HeadroomConfiguration);

        var entityGoal = WithCharger(EntityGoal) + "\nplanner:\n  dispatch: full\n";
        var energyGoal = WithCharger(EnergyGoal) + "\nplanner:\n  dispatch: narrow\n";
        foreach (var yaml in new[] { entityGoal, energyGoal })
        {
            var parsed = TestConfigurations.Valid(yaml).Configuration!;
            Assert.NotNull(parsed.Planner);
            Assert.NotNull(parsed.Devices.Single(d => d.Id.Value == "ev_charger").Goal);
        }

        Assert.Equal(plain, Digest(run, entityGoal));
        Assert.Equal(plain, Digest(run, energyGoal));
    }

    [Fact]
    public void The_plain_morning_still_matches_the_pinned_09_digest_so_the_comparison_above_is_against_the_real_baseline()
    {
        // Why: equality with a drifted baseline would prove nothing. The plain file's digest is the one pinned to the 0.9 engine.
        EconomicsBackwardCompatibilityTests.Same(
            "4831B8B697EC61AF0A10E5D86F940B77EACDD3E1FDACCDB148BCD1DD1C344824",
            Digest(run, DispatchSyntheticReplayRun.HeadroomConfiguration));
    }
}
