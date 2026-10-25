using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// Stage C over the 72 h recorded at the maintainer's installation (gitignored; fails loudly when
/// absent): the committed example in effective active mode, every command through the declared
/// actuators and the allow-list as on the live link, the heater elements following them (the
/// simulated plant on their real phases). Protection must still miss no violation with the loads it
/// switches itself, nothing may be refused, and only declared actuators are ever called. Nothing
/// reaches a Home Assistant.
/// </summary>
[Trait("Category", "LiveReplay")]
public sealed class ControlLiveReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void Live_72h_with_the_command_path_misses_no_violation_and_calls_only_declared_actuators()
    {
        var data = ReplayFixtures.LiveData;
        var configuration = TestConfigurations.Valid(TestConfigurations.Example).Configuration!;
        var plant = new SimulatedPlant(PlantDefinition.Load(RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-plant.json")), data, () => configuration);
        var path = new PlantCommandPath(configuration, plant);
        var harness = new GuardReplayHarness(data, TestConfigurations.Example, mode: TestModes.State(OperatingMode.Active, controlAllowed: true), port: path.Port);
        plant.Attach(harness.Replay.Source);
        harness.RunUntil(data.End);

        var protection = ProtectionSetupFactory.Build(configuration);
        var violations = new ViolationAnalyzer(protection, harness.Observations, harness.Decisions, harness.Steps);
        var missed = violations.MissedViolations();
        var declared = configuration.Devices.SelectMany(d => d.Stages.Select(s => s.Actuator)).ToHashSet();

        output.WriteLine($"Replayed {data.Start:O} to {data.End:O}: {harness.Steps.Count} steps, {harness.Decisions.Count} decisions, {path.Calls.Count} calls.");
        foreach (var group in path.Calls.GroupBy(c => $"{c.Call.Entity} {c.Call.Service}").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"call: {group.Key}: {group.Count()}");
        }

        foreach (var group in harness.Decisions.Where(d => d.Scope == DecisionScope.Device).GroupBy(d => $"{d.Device} {d.Origin} {d.Action} {ReasonCodes.Name(d.Reason)} commanded={d.Commanded}").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"device decision: {group.Key}: {group.Count()}");
        }

        foreach (var line in missed)
        {
            output.WriteLine(line);
        }

        Assert.True(harness.Steps.Count > 10_000, $"Only {harness.Steps.Count} steps; the check would pass vacuously.");
        Assert.NotEmpty(path.Calls);
        Assert.Empty(missed);
        Assert.Empty(path.Refused);
        Assert.All(path.Calls, c => Assert.Contains(c.Call.Entity, declared));
        Assert.All(path.Calls, c => HomeAssistantCommandAllowList.EnsureCallServiceAllowed(System.Text.Encoding.UTF8.GetBytes(c.Json), path.Directory.IsDeclared));
        Assert.All(harness.Decisions.Where(d => d.Origin == DecisionOrigin.Guard && d.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum), d => Assert.Null(d.RefusedBy));
    }
}
