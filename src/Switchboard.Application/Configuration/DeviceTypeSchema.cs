using System.Collections.Immutable;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Configuration;

/// <summary>
/// What the document says about one device type: its spelling, the keys a device of it may carry,
/// the domains of its actuator and state entity, and whether it is a relay or a setpoint device.
/// One table, so the parser, the key check, the wizard's templates and the setup layer all read the
/// same answer instead of each restating it per type. Reading a device's stages and building its
/// spec stay where they are (they need the parser's own context and the model's subtypes).
/// </summary>
internal sealed class DeviceTypeSchema
{
    private static readonly ImmutableArray<string> CommonKeys = ["id", "name", "type", "value_per_kwh", "shed_priority", "reaction_time", "no_shed", "min_on", "min_off", "state_max_age", "state_reports", "draw", "circuit_group", "start", "value_follows", "value_factor", "sources", GoalSectionParser.Key];

    private static readonly ImmutableArray<string> RelayActuatorDomains = ["switch", "input_boolean"];
    private static readonly ImmutableArray<string> RelayStateDomains = ["switch", "input_boolean", "binary_sensor"];
    private static readonly ImmutableArray<string> SetpointActuatorDomains = ["number", "input_number"];
    private static readonly ImmutableArray<string> SetpointStateDomains = ["number", "input_number", "sensor"];

    /// <summary>The keys of a stage of a stepped device, and of the single stage of the others.</summary>
    public static readonly ImmutableArray<string> StageKeys = ["actuator", "state_entity", "phase", "rated_current_a", "rated_power_w"];

    public static readonly DeviceTypeSchema OnOff = new(DeviceType.OnOff, "onoff", [.. StageKeys, "rotation_group"], relay: true, wizardEditable: true);

    public static readonly DeviceTypeSchema Stepped = new(DeviceType.Stepped, "stepped", ["stages"], relay: true, wizardEditable: false);

    public static readonly DeviceTypeSchema Modulating = new(
        DeviceType.Modulating,
        "modulating",
        ["actuator", "state_entity", "phase", "min", "max", "step", "unit", "stop_actuator", "stop_option", "resume_option"],
        relay: false,
        wizardEditable: true);

    public static readonly DeviceTypeSchema Storage = new(DeviceType.Storage, "storage", ["actuator", "state_entity", "phase", "min", "max", "step", "unit"], relay: false, wizardEditable: true);

    /// <summary>Every type, in the order the document lists them.</summary>
    public static readonly ImmutableArray<DeviceTypeSchema> All = [OnOff, Stepped, Modulating, Storage];

    private DeviceTypeSchema(DeviceType type, string name, ImmutableArray<string> typeKeys, bool relay, bool wizardEditable)
    {
        Type = type;
        Name = name;
        Keys = [.. CommonKeys, .. typeKeys];
        IsRelay = relay;
        ActuatorDomains = relay ? RelayActuatorDomains : SetpointActuatorDomains;
        StateDomains = relay ? RelayStateDomains : SetpointStateDomains;
        WizardEditable = wizardEditable;
    }

    public DeviceType Type { get; }

    /// <summary>The spelling of the type in the document.</summary>
    public string Name { get; }

    /// <summary>Every key a device of this type may carry: the common ones, then the type's own.</summary>
    public ImmutableArray<string> Keys { get; }

    /// <summary>On/off and stepped devices switch relays; modulating and storage devices take a setpoint.</summary>
    public bool IsRelay { get; }

    public bool IsSetpoint => !IsRelay;

    /// <summary>Only a modulating device may declare a stop actuator (a storage device is brought to zero charge by protection).</summary>
    public bool HasStopActuator => Type == DeviceType.Modulating;

    /// <summary>The domains an actuator of this type may have (SAFE-05: only these can ever be declared).</summary>
    public ImmutableArray<string> ActuatorDomains { get; }

    /// <summary>The domains a state entity of this type may have.</summary>
    public ImmutableArray<string> StateDomains { get; }

    /// <summary>Whether the setup wizard can represent a device of this type; the others are kept as written and can only be removed.</summary>
    public bool WizardEditable { get; }

    public static DeviceTypeSchema Of(DeviceType type) => All.Single(s => s.Type == type);

    /// <summary>The schema a document spelling names (exactly, case-sensitive), if any.</summary>
    public static DeviceTypeSchema? TryParse(string? name) => name is null ? null : All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}
