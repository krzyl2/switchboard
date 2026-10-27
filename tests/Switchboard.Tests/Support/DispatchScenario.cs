using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Support;

/// <summary>Device sets for dispatch scenarios (neutral entity names; 3 × 25 A, reserve 2.5 kW: threshold 14.13 A).</summary>
internal static class DispatchDevices
{
    /// <summary>Three 1.5 kW heater elements (6.6 A, 0.35 per kWh), one per phase.</summary>
    public const string Heaters = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: heater_l2
            type: onoff
            actuator: switch.heater_2
            phase: L2
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: heater_l3
            type: onoff
            actuator: switch.heater_3
            phase: L3
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;

    /// <summary>The three heaters as one rotation group (DISP-10).</summary>
    public static string RotatedHeaters => Heaters.ReplaceLineEndings("\n").Replace("value_per_kwh: 0.35", "value_per_kwh: 0.35\n    rotation_group: cwu", StringComparison.Ordinal);

    /// <summary>The L1 heater and a 6–20 A charger on L1 (2.03 per kWh) with its own draw current.</summary>
    public const string HeaterAndCharger = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
            draw: { current: sensor.charger_draw }
        """;

    /// <summary>The L1 heater and a 3 A pool pump on L1 worth more per kWh.</summary>
    public const string HeaterAndPool = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: pool
            type: onoff
            actuator: switch.pool
            phase: L1
            rated_current_a: 3
            rated_power_w: 690
            value_per_kwh: 1.0
        """;

    /// <summary>A three-stage boiler on L1, 3 A per stage.</summary>
    public const string Boiler = """
          - id: boiler
            type: stepped
            value_per_kwh: 0.3
            stages:
              - { actuator: switch.boiler_1, phase: L1, rated_current_a: 3, rated_power_w: 690 }
              - { actuator: switch.boiler_2, phase: L1, rated_current_a: 3, rated_power_w: 690 }
              - { actuator: switch.boiler_3, phase: L1, rated_current_a: 3, rated_power_w: 690 }
        """;

    /// <summary>A battery on L1: −10…16 A, positive charges.</summary>
    public const string Battery = """
          - id: battery
            type: storage
            actuator: number.battery_current
            phase: L1
            min: -10
            max: 16
            step: 1
            value_per_kwh: 1.5
        """;
}

/// <summary>Scenarios with dispatch running in every step, as the runner composes it.</summary>
internal static class DispatchScenario
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Active semantics: option active, control allowed, configuration valid, state confirmed (commands go to the recording port).</summary>
    public static ModeStatus Active => ModeState.Resolve(OperatingMode.Active, controlAllowed: true, configurationValid: true, stateConfirmed: true);

    /// <param name="off">Devices reported off (relays off, setpoints at their floor); the rest start on.</param>
    public static GuardScenario Create(string devices, string[] off, string control = "", ModeStatus? mode = null, IDeviceCommandPort? port = null, bool settledLockouts = true, DateTimeOffset? start = null, string extra = "", Switchboard.Application.Planning.IPlanView? plan = null)
    {
        var s = new GuardScenario(TestDevices.Document(devices, control, extra: extra), start: start, mode: mode, settledLockouts: false, dispatch: true, port: port, plan: plan);
        foreach (var name in off)
        {
            var device = s.Setup.Device(DeviceName.Parse(name))!;
            foreach (var stage in device.Stages)
            {
                s.SetState(device.Id, stage.Index, device.IsSetpoint ? new SetpointApplied(device.SetpointFloor) : new RelayApplied(false));
            }
        }

        if (settledLockouts)
        {
            s.Lockouts.Observe(s.Setup, [.. s.Inputs().DeviceStates], s.Now - TimeSpan.FromHours(1));
        }

        return s;
    }

    /// <summary>
    /// The meter polled every 10 s until <paramref name="end"/> with these currents (a null phase
    /// stays silent), with ticks in between; <paramref name="refresh"/> runs before each poll (a
    /// device draw reported with it).
    /// </summary>
    public static void PollUntil(this GuardScenario s, DateTimeOffset end, double? l1, double? l2, double? l3, Action? refresh = null)
    {
        while (s.Now + PollInterval <= end)
        {
            s.RunUntil(s.Now + PollInterval);
            refresh?.Invoke();
            foreach (var (phase, amps) in new[] { (Phase.L1, l1), (Phase.L2, l2), (Phase.L3, l3) })
            {
                if (amps is { } value)
                {
                    s.Sample(phase, value, evaluate: false);
                }
            }

            s.Evaluate();
        }

        s.RunUntil(end);
    }

    /// <summary>
    /// The scenario's devices report every commanded level at once, as a relay or charger whose
    /// entity follows its commands does (an on_change state then confirms each acknowledged command).
    /// </summary>
    public static void Follow(this GuardScenario s, RecordingDeviceCommandPort port)
    {
        port.OnSubmit = command =>
        {
            switch (command.Kind)
            {
                case DeviceCommandKind.Relay:
                    s.SetState(command.Device, command.Stage, new RelayApplied(command.On));
                    break;
                case DeviceCommandKind.Setpoint:
                    s.SetState(command.Device, command.Stage, new SetpointApplied(command.Setpoint!.Value));
                    break;
                default:
                    s.SetState(command.Device, DeviceStateSample.StopStage, new RelayApplied(command.On));
                    break;
            }
        };
    }

    /// <summary>A charger's setpoint and draw as the signal layer would report them now.</summary>
    public static void Charger(this GuardScenario s, double setpoint, double draw)
    {
        s.SetState("ev_charger", new SetpointApplied(new Amperes(setpoint)));
        s.SetDraw("ev_charger", Phase.L1, draw);
    }
}
