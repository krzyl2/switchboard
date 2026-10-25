using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// The committed tier walk (make-synthetic-overload.py) with the real command path (Stage C): the
/// example's three heater elements in effective active mode (the charger left out: the walk has no
/// charger draw, so dispatch would count its setpoint and stop the heaters for the reserve before
/// the walk begins), every command resolved through the declared actuators and the allow-list as on
/// the live link, and the elements following them (the simulated plant, synthetic-plant.json, each
/// on its real phase). The walk's quiet 8 A includes the element that is on, so a shed element takes
/// its 7 A out of the meter. Nothing reaches a Home Assistant.
/// </summary>
public sealed class ControlSyntheticReplayRun
{
    public ControlSyntheticReplayRun()
    {
        var data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", GuardSyntheticReplayRun.File));
        Configuration = TestConfigurations.Valid(Yaml).Configuration!;
        var plant = new SimulatedPlant(PlantDefinition.Load(RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-plant.json")), data, () => Configuration);
        Path = new PlantCommandPath(Configuration, plant);
        Harness = new GuardReplayHarness(data, Yaml, mode: TestModes.State(OperatingMode.Active, controlAllowed: true), port: Path.Port);
        plant.Attach(Harness.Replay.Source);
        Harness.RunUntil(data.End);
    }

    /// <summary>The example without the charger; the heaters report on change, as the maintainer's Modbus relays do.</summary>
    public static string Yaml
    {
        get
        {
            const string Last = "      max_age: 60s\n";
            var example = TestConfigurations.Example.ReplaceLineEndings("\n");
            var start = example.IndexOf("  - id: ev_charger", StringComparison.Ordinal);
            var end = example.IndexOf(Last, start, StringComparison.Ordinal) + Last.Length;
            return example[..start] + example[end..];
        }
    }

    internal SwitchboardConfiguration Configuration { get; }

    internal PlantCommandPath Path { get; }

    internal GuardReplayHarness Harness { get; }

    internal IEnumerable<Decision> Sheds(string device, DateTimeOffset from, DateTimeOffset to) =>
        Harness.Decisions.Where(d => d.Device?.Value == device && d.Origin == DecisionOrigin.Guard && d.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum && d.At >= from && d.At <= to);

    /// <summary>L1's measured current at the first report after <paramref name="after"/>.</summary>
    internal double L1After(DateTimeOffset after) =>
        Harness.Observations.First(o => o.TakenAt > after && o.Phases.Any(p => p.Phase == Phase.L1 && p.Current is not null)).Phases.Single(p => p.Phase == Phase.L1).Current!.Magnitude.Value;
}

public sealed class ControlSyntheticReplayTests(ControlSyntheticReplayRun run) : IClassFixture<ControlSyntheticReplayRun>
{
    [Fact]
    public void The_run_is_in_effective_active_mode_and_starts_in_safe_state_with_every_element_off()
    {
        // SAFE-01 at start: before the first reading every controlled device is off, and with the
        // command path that is real (the elements are restored once data is valid: restore_only).
        var mode = run.Harness.Decisions.First(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal((OperatingMode.Active, true), (mode.Mode, mode.Inputs.Mode!.ControlAllowed));
        var start = run.Harness.Decisions.Where(d => d.At == mode.At && d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff).ToList();
        Assert.Equal(["heater_l1", "heater_l2", "heater_l3"], start.Select(d => d.Device!.Value.Value).Order(StringComparer.Ordinal));
        Assert.All(start, d => Assert.True(d.Commanded));
        Assert.Contains(run.Harness.Decisions, d => d.Device?.Value == "heater_l1" && d.Action == DecisionAction.SwitchOn && d.Commanded && d.At < GuardSyntheticReplayRun.Minute(30));
    }

    [Fact]
    public void Nothing_is_ever_refused_and_every_change_that_needed_a_command_was_acknowledged()
    {
        var changes = run.Harness.Decisions.Where(d => d.Device is not null && d.Action is DecisionAction.SwitchOff or DecisionAction.SwitchOn).ToList();

        Assert.NotEmpty(changes);
        Assert.All(changes, d => Assert.Null(d.RefusedBy));
        Assert.Equal(run.Path.Calls.Count, run.Path.Port.Submitted.Count);
    }

    [Fact]
    public void Only_declared_actuators_are_ever_called_and_only_in_the_allow_list_shape()
    {
        var declared = run.Configuration.Devices.SelectMany(d => d.Stages.Select(s => s.Actuator)).ToHashSet();

        Assert.All(run.Path.Calls, c => Assert.Contains(c.Call.Entity, declared));
        Assert.Empty(run.Path.Refused);
        Assert.All(run.Path.Calls, c => HomeAssistantCommandAllowList.EnsureCallServiceAllowed(System.Text.Encoding.UTF8.GetBytes(c.Json), run.Path.Directory.IsDeclared));
    }

    [Theory]
    [InlineData(37, 0, 54, 0, 38, 0)]
    [InlineData(65, 59, 71, 0, 67, 0)]
    [InlineData(82, 59, 88, 0, 83, 30)]
    public void Each_band_sheds_the_element_at_the_same_moment_as_in_shadow_and_the_phase_really_falls(int fromMinute, int fromSecond, int toMinute, int toSecond, int atMinute, int atSecond)
    {
        // BRK-03 with the real path: the reaction speed per tier is the one the shadow walk records
        // (GuardSyntheticReplayTests), the shed is commanded, and the next reading of L1 is the
        // element's 7 A lower.
        var shed = Assert.Single(run.Sheds("heater_l1", GuardSyntheticReplayRun.Minute(fromMinute, fromSecond), GuardSyntheticReplayRun.Minute(toMinute, toSecond)));

        Assert.Equal((GuardSyntheticReplayRun.Minute(atMinute, atSecond), DecisionAction.SwitchOff, true), (shed.At, shed.Action, shed.Commanded));
        var before = run.L1After(shed.At - TimeSpan.FromSeconds(11));
        var after = run.L1After(shed.At + TimeSpan.FromSeconds(1));
        Assert.Equal(before - 7, after, 1);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(114)]
    public void The_fast_bands_shed_the_element_at_the_entering_poll(int minute)
    {
        var shed = Assert.Single(run.Sheds("heater_l1", GuardSyntheticReplayRun.Minute(minute - 1, 55), GuardSyntheticReplayRun.Minute(minute + 1)));

        Assert.Equal((GuardSyntheticReplayRun.Poll(minute), true), (shed.At, shed.Commanded));
    }
}
