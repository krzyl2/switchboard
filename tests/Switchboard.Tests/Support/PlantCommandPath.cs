using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Replay;

namespace Switchboard.Tests.Support;

/// <summary>
/// The production command path's send-time half, synchronously, for replay tests: every command the
/// gate submits is resolved through the actuators of the configuration loaded now
/// (<see cref="ActuatorDirectoryHolder"/>), serialized and checked by the allow-list against the
/// declared actuators exactly as on the real link, kept, acknowledged, and handed to the simulated
/// plant (the house's loads follow it). Nothing ever reaches a Home Assistant. A test can make the
/// plant follow a few guard steps late (a relay that reports its new state after the step that
/// commanded it) and make Home Assistant refuse chosen calls (a Modbus write error).
/// </summary>
internal sealed class PlantCommandPath
{
    private readonly SimulatedPlant? _plant;
    private readonly int _reportDelaySteps;
    private readonly Queue<(long Due, ActuatorCall Call)> _late = new();
    private long _steps;
    private int _lastId;

    /// <param name="reportDelaySteps">Guard steps between a command and the plant following it (0: within the step).</param>
    public PlantCommandPath(SwitchboardConfiguration configuration, SimulatedPlant? plant, int reportDelaySteps = 0)
    {
        _plant = plant;
        _reportDelaySteps = reportDelaySteps;
        Directory.Observe(configuration);
        Port.OnSubmit = command =>
        {
            var resolution = Directory.Resolve(command);
            if (resolution.Call is not { } call)
            {
                Refused.Add((command, resolution.Refusal!.Value));
                return;
            }

            var payload = CallServicePayload.Build(++_lastId, call);
            HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload.Span, Directory.IsDeclared);
            Calls.Add((command, call, System.Text.Encoding.UTF8.GetString(payload.Span)));
            if (Fail?.Invoke(call) == true)
            {
                Failed.Add(command);
                return;
            }

            if (_reportDelaySteps > 0)
            {
                _late.Enqueue((_steps + _reportDelaySteps, call));
            }
            else
            {
                _plant?.Apply(call);
            }
        };
        Port.Answer = command => Refused.Any(r => ReferenceEquals(r.Command, command))
            ? (CommandOutcome.NotSent, CommandNotSentReason.NotDeclared)
            : Failed.Any(f => ReferenceEquals(f, command))
                ? (CommandOutcome.Failed, null)
                : (CommandOutcome.Acknowledged, null);
    }

    public ActuatorDirectoryHolder Directory { get; } = new();

    public RecordingDeviceCommandPort Port { get; } = new();

    /// <summary>Every call that left, with the command it came from and its exact bytes.</summary>
    public List<(DeviceCommand Command, ActuatorCall Call, string Json)> Calls { get; } = [];

    public List<(DeviceCommand Command, ActuatorRefusal Refusal)> Refused { get; } = [];

    /// <summary>Calls Home Assistant answers with an error: the plant does not follow them.</summary>
    public Func<ActuatorCall, bool>? Fail { get; set; }

    public List<DeviceCommand> Failed { get; } = [];

    /// <summary>One guard step passed (wired to the runner's Stepped event): delayed calls whose time came reach the plant.</summary>
    public void Stepped()
    {
        _steps++;
        while (_late.TryPeek(out var late) && late.Due <= _steps)
        {
            _plant?.Apply(_late.Dequeue().Call);
        }
    }
}
