using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Reads <c>devices:</c> (schema version 1, the subset protection needs; dispatch only adds keys).
/// A device names the one entity that may ever be commanded for it (its actuator, SAFE-05) and
/// where its applied state is read; each stage's state becomes a signal with role device_state, so
/// freshness and units go through the signal layer like any measurement (SIG-05). Untrusted input:
/// every key is checked and every problem is reported with its position.
/// </summary>
internal static class DeviceSectionParser
{
    /// <summary>Prefix of the state signals created here; refused for signals declared under <c>signals:</c>.</summary>
    public const string SignalPrefix = "device_";

    private const string Key = "devices";
    private const string ThreePhase = "three_phase";
    private const string StorageType = "storage";
    private const string DefaultUnit = "A";

    // Schema bound, not a control parameter: a house with more is a typo or a generated file.
    private const int MaxDevices = 64;

    private static readonly string[] CommonKeys = ["id", "type", "value_per_kwh", "shed_priority", "reaction_time", "no_shed", "min_on", "min_off", "state_max_age"];
    private static readonly string[] StageKeys = ["actuator", "state_entity", "phase", "rated_current_a", "rated_power_w"];
    private static readonly string[] ModulatingKeys = ["actuator", "state_entity", "phase", "min", "max", "step", "unit"];
    private static readonly string[] SteppedKeys = ["stages"];

    // Dispatch (Stage 3) keys of 03-ARCHITECTURE: accepted with a warning so a document written for
    // a later version still loads. A power range would duplicate the setpoint range, so it is refused.
    private static readonly string[] LaterKeys = ["circuit_group", "sources", "goal", "guards"];
    private static readonly string[] PowerRangeKeys = ["power_min", "power_max"];

    private static readonly ImmutableDictionary<string, DeviceType> Types = new Dictionary<string, DeviceType>
    {
        ["onoff"] = DeviceType.OnOff,
        ["stepped"] = DeviceType.Stepped,
        ["modulating"] = DeviceType.Modulating,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableArray<string> RelayActuatorDomains = ["switch", "input_boolean"];
    private static readonly ImmutableArray<string> ModulatingActuatorDomains = ["number", "input_number"];
    private static readonly ImmutableArray<string> RelayStateDomains = ["switch", "input_boolean", "binary_sensor"];
    private static readonly ImmutableArray<string> ModulatingStateDomains = ["number", "input_number", "sensor"];

    private static readonly ImmutableDictionary<string, Phase> PhaseNames = new Dictionary<string, Phase>
    {
        ["L1"] = Phase.L1,
        ["L2"] = Phase.L2,
        ["L3"] = Phase.L3,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <param name="signalEntities">Entities already mapped under <c>signals:</c>; a state entity may not be one of them.</param>
    public static (ImmutableArray<DeviceDefinition> Devices, ImmutableArray<SignalDefinition> Signals) Read(
        ConfigMapping root,
        ConnectionProfile? connection,
        ControlParameters? control,
        IReadOnlyDictionary<EntityId, SignalName> signalEntities,
        ConfigIssues issues)
    {
        if (root.Find(Key) is not { } entry || ConfigRead.IsNull(entry.Value))
        {
            return ([], []);
        }

        if (entry.Value is not ConfigSequence sequence)
        {
            issues.Error(ConfigurationIssueCodes.WrongType, Key, entry.Value, "Expected a list of devices.");
            return ([], []);
        }

        if (sequence.Items.Length > MaxDevices)
        {
            issues.Error(ConfigurationIssueCodes.OutOfRange, Key, sequence, $"At most {MaxDevices} devices may be declared.");
        }

        var context = new Context(connection, control, issues);
        var drafts = new List<DeviceDraft>();
        for (var i = 0; i < sequence.Items.Length; i++)
        {
            if (ReadDevice(sequence.Items[i], i, context) is { } draft)
            {
                drafts.Add(draft);
            }
        }

        CheckUniqueness(drafts, signalEntities, issues);
        if (control is null || drafts.Any(d => !d.Valid))
        {
            return ([], []);
        }

        var devices = ImmutableArray.CreateBuilder<DeviceDefinition>(drafts.Count);
        var signals = ImmutableArray.CreateBuilder<SignalDefinition>();
        foreach (var draft in drafts)
        {
            var definition = draft.Build();
            devices.Add(definition);
            foreach (var stage in definition.Stages)
            {
                signals.Add(StateSignal(definition, stage, control));
            }
        }

        return (devices.ToImmutable(), signals.ToImmutable());
    }

    /// <summary>The logical name of a stage's state signal: <c>device_&lt;id&gt;</c>, or <c>device_&lt;id&gt;_stage&lt;n&gt;</c> for a stepped device.</summary>
    public static SignalName StateSignalName(DeviceName device, int stage, int stageCount) =>
        SignalName.Parse(stageCount == 1
            ? $"{SignalPrefix}{device.Value}"
            : string.Create(CultureInfo.InvariantCulture, $"{SignalPrefix}{device.Value}_stage{stage}"));

    private static SignalDefinition StateSignal(DeviceDefinition device, StageDefinition stage, ControlParameters control) =>
        device.Type == DeviceType.Modulating
            ? new SignalDefinition(
                stage.StateSignal,
                stage.StateEntity,
                SignalKind.Current,
                device.RangeUnit,
                SignConvention.PositiveImport,
                null,
                false,
                device.StateMaxAge,
                control.Signals.DefaultSigma.GetValueOrDefault(SignalKind.Current),
                SignalRole.DeviceState,
                null,
                false,
                device.Path)
            : new SignalDefinition(
                stage.StateSignal,
                stage.StateEntity,
                SignalKind.State,
                null,
                null,
                null,
                false,
                device.StateMaxAge,
                null,
                SignalRole.DeviceState,
                null,
                false,
                device.Path);

    private static DeviceDraft? ReadDevice(ConfigNode node, int index, Context context)
    {
        var issues = context.Issues;
        var path = $"{Key}[{index.ToString(CultureInfo.InvariantCulture)}]";
        if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            if (ConfigRead.IsNull(node))
            {
                issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(path, "id"), node, "'id' is required.");
            }

            return null;
        }

        var errorsBefore = issues.Errors.Count;
        DeviceName? id = null;
        var idNode = ConfigRead.Required(mapping, "id", path, issues);
        if (idNode is not null && ConfigRead.String(idNode, ConfigRead.Join(path, "id"), issues) is { } idText)
        {
            if (!DeviceName.TryParse(idText, out id))
            {
                issues.Error(
                    ConfigurationIssueCodes.InvalidDeviceId,
                    ConfigRead.Join(path, "id"),
                    idNode,
                    $"'{idText}' is not a valid device id: lower-case letters, digits and '_', starting with a letter, at most 40 characters.");
            }
        }

        DeviceType? type = null;
        var typePath = ConfigRead.Join(path, "type");
        if (ConfigRead.Required(mapping, "type", path, issues) is { } typeNode && ConfigRead.String(typeNode, typePath, issues) is { } typeText)
        {
            if (typeText == StorageType)
            {
                issues.Warning(
                    ConfigurationIssueCodes.ReservedKeyIgnored,
                    typePath,
                    typeNode,
                    "Device type 'storage' is reserved for a later version; this device is ignored by this one.");
                return null;
            }

            type = ConfigRead.Choice(typeNode, typePath, issues, Types);
        }

        if (type is not { } deviceType)
        {
            return null;
        }

        CheckDeviceKeys(mapping, path, deviceType, issues);
        var control = context.Control;
        var valuePerKwh = ConfigRead.Required(mapping, "value_per_kwh", path, issues) is { } valueNode
            ? ConfigRead.Number(valueNode, ConfigRead.Join(path, "value_per_kwh"), issues, Range.AtLeast(0))
            : null;
        var shedPriority = ConfigRead.Optional(mapping, "shed_priority") is { } priorityNode
            ? ConfigRead.Number(priorityNode, ConfigRead.Join(path, "shed_priority"), issues, Range.AtLeast(0))
            : null;
        var reactionTime = OptionalDuration(mapping, "reaction_time", path, issues, positive: false)
            ?? control?.Devices.ReactionTime.GetValueOrDefault(deviceType);
        var noShed = ConfigRead.Optional(mapping, "no_shed") is { } noShedNode
            ? ConfigRead.Boolean(noShedNode, ConfigRead.Join(path, "no_shed"), issues)
            : false;
        var minOn = OptionalDuration(mapping, "min_on", path, issues, positive: false)
            ?? (deviceType == DeviceType.Modulating ? control?.Devices.ModulatingMinOn : control?.Dispatch.RelayMinOn);
        var minOff = OptionalDuration(mapping, "min_off", path, issues, positive: false)
            ?? (deviceType == DeviceType.Modulating ? control?.Devices.ModulatingMinOff : control?.Dispatch.RelayMinOff);
        var stateMaxAge = ReadStateMaxAge(mapping, path, control, issues);

        var stages = new List<StageDraft>();
        ModulatingRange? range = null;
        string? unit = null;
        switch (deviceType)
        {
            case DeviceType.OnOff:
                stages.Add(ReadStage(mapping, path, 1, deviceType, context));
                break;
            case DeviceType.Modulating:
                (range, unit) = ReadRange(mapping, path, context);
                stages.Add(ReadStage(mapping, path, 1, deviceType, context, range?.Max));
                break;
            case DeviceType.Stepped:
                stages.AddRange(ReadStages(mapping, path, context));
                break;
        }

        var valid = issues.Errors.Count == errorsBefore
            && id is not null && valuePerKwh is not null && reactionTime is not null && noShed is not null
            && minOn is not null && minOff is not null && stateMaxAge is not null
            && stages.Count > 0 && stages.All(s => s.Valid) && (deviceType != DeviceType.Modulating || range is not null);
        return new DeviceDraft(
            index,
            id,
            idNode,
            deviceType,
            stages,
            range,
            unit,
            valuePerKwh ?? 0,
            shedPriority,
            reactionTime ?? TimeSpan.Zero,
            noShed ?? false,
            minOn ?? TimeSpan.Zero,
            minOff ?? TimeSpan.Zero,
            stateMaxAge ?? TimeSpan.Zero,
            valid);
    }

    /// <summary>Each type has an exact key set; dispatch keys warn, a power range is refused, anything else is unknown.</summary>
    private static void CheckDeviceKeys(ConfigMapping mapping, string path, DeviceType type, ConfigIssues issues)
    {
        var allowed = type switch
        {
            DeviceType.OnOff => CommonKeys.Concat(StageKeys),
            DeviceType.Modulating => CommonKeys.Concat(ModulatingKeys),
            _ => CommonKeys.Concat(SteppedKeys),
        };
        var keys = allowed.ToArray();
        foreach (var entry in mapping.Entries)
        {
            var keyPath = ConfigRead.Join(path, entry.Key);
            if (keys.Contains(entry.Key))
            {
                continue;
            }

            if (LaterKeys.Contains(entry.Key))
            {
                issues.Warning(ConfigurationIssueCodes.ReservedKeyIgnored, keyPath, entry.Value, $"'{entry.Key}' is reserved for a later version and is ignored by this one.");
            }
            else if (PowerRangeKeys.Contains(entry.Key))
            {
                issues.ErrorAtKey(
                    ConfigurationIssueCodes.KeyNotAllowed,
                    keyPath,
                    entry,
                    type == DeviceType.Modulating
                        ? $"'{entry.Key}' is not used: a modulating device's range is min, max and step in its actuator's unit."
                        : $"'{entry.Key}' applies to modulating devices only, and is not used: their range is min, max and step.");
            }
            else
            {
                issues.ErrorAtKey(ConfigurationIssueCodes.UnknownKey, keyPath, entry, $"Unknown key '{entry.Key}'. Allowed here: {string.Join(", ", keys)}.");
            }
        }
    }

    private static TimeSpan? ReadStateMaxAge(ConfigMapping mapping, string path, ControlParameters? control, ConfigIssues issues)
    {
        if (ConfigRead.Optional(mapping, "state_max_age") is not { } node)
        {
            return control?.Devices.StateMaxAge;
        }

        var keyPath = ConfigRead.Join(path, "state_max_age");
        var maxAge = ConfigRead.PositiveDuration(node, keyPath, issues);
        if (maxAge > control?.Signals.MaxAgeLimit)
        {
            issues.Error(
                ConfigurationIssueCodes.OutOfRange,
                keyPath,
                node,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"state_max_age may be at most {control!.Signals.MaxAgeLimit.TotalSeconds:0.###} s (control.signals.max_age_limit): a frozen state counts as fresh for its whole max_age."));
            return null;
        }

        return maxAge;
    }

    private static IEnumerable<StageDraft> ReadStages(ConfigMapping mapping, string path, Context context)
    {
        var issues = context.Issues;
        var stagesPath = ConfigRead.Join(path, "stages");
        if (ConfigRead.Required(mapping, "stages", path, issues) is not { } node)
        {
            yield break;
        }

        if (node is not ConfigSequence sequence)
        {
            issues.Error(ConfigurationIssueCodes.WrongType, stagesPath, node, "Expected a list of stages.");
            yield break;
        }

        if (sequence.Items.Length < 2)
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, stagesPath, node, "A stepped device has at least two stages; declare a single relay as type onoff.");
            yield break;
        }

        for (var i = 0; i < sequence.Items.Length; i++)
        {
            var stagePath = $"{stagesPath}[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (ConfigRead.Mapping(sequence.Items[i], stagePath, issues) is not { } stage)
            {
                if (ConfigRead.IsNull(sequence.Items[i]))
                {
                    issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(stagePath, "actuator"), sequence.Items[i], "'actuator' is required.");
                }

                yield return StageDraft.Invalid;
                continue;
            }

            ConfigRead.CheckKeys(stage, stagePath, StageKeys, issues);
            yield return ReadStage(stage, stagePath, i + 1, DeviceType.Stepped, context);
        }
    }

    /// <param name="modulatingMax">A modulating device's maximum setpoint in A: its rated current.</param>
    private static StageDraft ReadStage(ConfigMapping mapping, string path, int index, DeviceType type, Context context, Amperes? modulatingMax = null)
    {
        var issues = context.Issues;
        var errorsBefore = issues.Errors.Count;
        var relay = type != DeviceType.Modulating;

        var actuatorNode = ConfigRead.Required(mapping, "actuator", path, issues);
        var actuator = ReadEntity(actuatorNode, ConfigRead.Join(path, "actuator"), relay ? RelayActuatorDomains : ModulatingActuatorDomains, ConfigurationIssueCodes.ActuatorDomainNotAllowed, "an actuator", issues);

        // Every actuator domain can report its own state, so the default needs no domain check.
        var stateNode = ConfigRead.Optional(mapping, "state_entity");
        var state = stateNode is null
            ? actuator
            : ReadEntity(stateNode, ConfigRead.Join(path, "state_entity"), relay ? RelayStateDomains : ModulatingStateDomains, ConfigurationIssueCodes.StateEntityDomainNotAllowed, "a state entity", issues);

        var phases = ReadPhases(mapping, path, context);

        Amperes? rated = modulatingMax;
        Watts? power = null;
        if (relay)
        {
            var rating = context.Connection?.BreakerRating.Value ?? double.PositiveInfinity;
            if (ConfigRead.Required(mapping, "rated_current_a", path, issues) is { } currentNode
                && ConfigRead.Number(currentNode, ConfigRead.Join(path, "rated_current_a"), issues, new Range(0, rating, MinExclusive: true)) is { } current)
            {
                rated = new Amperes(current);
            }

            if (ConfigRead.Required(mapping, "rated_power_w", path, issues) is { } powerNode
                && ConfigRead.Number(powerNode, ConfigRead.Join(path, "rated_power_w"), issues, Range.Above(0)) is { } watts)
            {
                power = new Watts(watts);
            }
        }

        var valid = issues.Errors.Count == errorsBefore && actuator is not null && state is not null && phases is not null && rated is not null;
        return new StageDraft(index, actuator, actuatorNode, state, stateNode ?? actuatorNode, phases ?? [], rated ?? Amperes.Zero, power, valid);
    }

    private static ImmutableArray<Phase>? ReadPhases(ConfigMapping mapping, string path, Context context)
    {
        var issues = context.Issues;
        var phasePath = ConfigRead.Join(path, "phase");
        if (ConfigRead.Required(mapping, "phase", path, issues) is not { } node || ConfigRead.String(node, phasePath, issues) is not { } text)
        {
            return null;
        }

        var topology = context.Connection?.Topology;
        if (text == ThreePhase)
        {
            if (topology == Topology.SinglePhase)
            {
                issues.Error(ConfigurationIssueCodes.PhaseNotInTopology, phasePath, node, "A single_phase connection has no three_phase devices.");
                return null;
            }

            return [Phase.L1, Phase.L2, Phase.L3];
        }

        if (!PhaseNames.TryGetValue(text, out var phase))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, phasePath, node, $"'{text}' is not one of: L1, L2, L3, {ThreePhase}.");
            return null;
        }

        if (topology is { } t && !TopologyPhases.For(t).Contains(phase))
        {
            issues.Error(ConfigurationIssueCodes.PhaseNotInTopology, phasePath, node, $"{phase} is not a phase of this connection.");
            return null;
        }

        return [phase];
    }

    /// <summary>
    /// The setpoint range in the actuator's unit (A or mA), converted to amperes by the signal
    /// layer's factor. It must be reachable in whole steps and stay within the breaker rating.
    /// </summary>
    private static (ModulatingRange? Range, string? Unit) ReadRange(ConfigMapping mapping, string path, Context context)
    {
        var issues = context.Issues;
        var unit = DefaultUnit;
        if (ConfigRead.Optional(mapping, "unit") is { } unitNode)
        {
            var unitPath = ConfigRead.Join(path, "unit");
            if (ConfigRead.String(unitNode, unitPath, issues) is not { } text)
            {
                return (null, null);
            }

            if (!SignalUnits.AllowedFor(SignalKind.Current).Contains(text))
            {
                issues.Error(
                    ConfigurationIssueCodes.UnitNotAllowedForKind,
                    unitPath,
                    unitNode,
                    $"'{text}' is not a current unit; a modulating device sets a current in one of: {string.Join(", ", SignalUnits.AllowedFor(SignalKind.Current))}.");
                return (null, null);
            }

            unit = text;
        }

        var minNode = ConfigRead.Required(mapping, "min", path, issues);
        var maxNode = ConfigRead.Required(mapping, "max", path, issues);
        var stepNode = ConfigRead.Required(mapping, "step", path, issues);
        var min = minNode is null ? null : ConfigRead.Number(minNode, ConfigRead.Join(path, "min"), issues);
        var max = maxNode is null ? null : ConfigRead.Number(maxNode, ConfigRead.Join(path, "max"), issues);
        var step = stepNode is null ? null : ConfigRead.Number(stepNode, ConfigRead.Join(path, "step"), issues);
        if (min is not { } lo || max is not { } hi || step is not { } by)
        {
            return (null, unit);
        }

        var factor = SignalUnits.Factor(unit);
        var rating = context.Connection?.BreakerRating.Value ?? double.PositiveInfinity;
        var (problem, at, key) = (lo, hi, by) switch
        {
            _ when lo <= 0 => ("min must be greater than 0 (a stop actuator is not part of this version).", minNode, "min"),
            _ when hi <= lo => ("max must be greater than min.", maxNode, "max"),
            _ when hi * factor > rating => ($"max must not exceed the breaker rating ({rating.ToString(CultureInfo.InvariantCulture)} A).", maxNode, "max"),
            _ when by <= 0 => ("step must be greater than 0.", stepNode, "step"),
            _ when !WholeSteps(hi - lo, by) => ("(max − min) must be a whole number of steps, or max is never reachable.", stepNode, "step"),
            _ => ((string?)null, (ConfigNode?)null, (string?)null),
        };
        if (problem is not null)
        {
            issues.Error(ConfigurationIssueCodes.ModulatingRangeInvalid, ConfigRead.Join(path, key!), at, problem);
            return (null, unit);
        }

        return (new ModulatingRange(new Amperes(lo * factor), new Amperes(hi * factor), new Amperes(by * factor)), unit);
    }

    // Decimal steps (0.1 A) are not exact in binary floating point; a relative tolerance accepts them.
    private static bool WholeSteps(double span, double step)
    {
        var steps = span / step;
        return Math.Abs(steps - Math.Round(steps)) <= 1e-9 * Math.Max(1, steps);
    }

    private static EntityId? ReadEntity(ConfigNode? node, string path, ImmutableArray<string> domains, string domainCode, string what, ConfigIssues issues)
    {
        if (node is null || ConfigRead.String(node, path, issues) is not { } text)
        {
            return null;
        }

        if (!EntityId.TryParse(text, out var entity))
        {
            issues.Error(ConfigurationIssueCodes.InvalidEntityId, path, node, $"'{text}' is not a Home Assistant entity ID (domain.object_id).");
            return null;
        }

        if (!domains.Contains(Domain(entity.Value)))
        {
            issues.Error(domainCode, path, node, $"{entity} cannot be {what} of this device type; use one of: {string.Join(", ", domains.Select(d => d + "."))}.");
            return null;
        }

        return entity;
    }

    private static string Domain(EntityId entity) => entity.Value[..entity.Value.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>
    /// Ids are unique; an actuator is declared once across all devices and stages; a state entity is
    /// read by one stage only and is not also mapped under <c>signals:</c> (one entity feeds one signal).
    /// </summary>
    private static void CheckUniqueness(List<DeviceDraft> drafts, IReadOnlyDictionary<EntityId, SignalName> signalEntities, ConfigIssues issues)
    {
        var ids = new Dictionary<DeviceName, int>();
        var actuators = new Dictionary<EntityId, string>();
        var states = new Dictionary<EntityId, string>();
        foreach (var draft in drafts)
        {
            if (draft.Id is { } id && !ids.TryAdd(id, draft.Index))
            {
                issues.Error(ConfigurationIssueCodes.DuplicateDeviceId, $"{draft.Path}.id", draft.IdNode, $"Device id '{id}' is already declared by devices[{ids[id]}].");
                draft.Valid = false;
            }

            foreach (var stage in draft.Stages)
            {
                var owner = draft.Stages.Count == 1 ? draft.Path : $"{draft.Path}.stages[{stage.Index - 1}]";
                if (stage.Actuator is { } actuator && !actuators.TryAdd(actuator, owner))
                {
                    issues.Error(ConfigurationIssueCodes.DuplicateActuator, $"{owner}.actuator", stage.ActuatorNode, $"{actuator} is already the actuator of {actuators[actuator]}; an entity is commanded for one device stage only.");
                    draft.Valid = false;
                }

                if (stage.State is not { } state)
                {
                    continue;
                }

                if (signalEntities.TryGetValue(state, out var signal))
                {
                    issues.Error(ConfigurationIssueCodes.DuplicateEntity, $"{owner}.state_entity", stage.StateNode, $"{state} is already mapped by signal '{signal}'; one entity feeds one signal.");
                    draft.Valid = false;
                }
                else if (!states.TryAdd(state, owner))
                {
                    issues.Error(ConfigurationIssueCodes.DuplicateEntity, $"{owner}.state_entity", stage.StateNode, $"{state} already reports the state of {states[state]}; one entity feeds one signal.");
                    draft.Valid = false;
                }
            }
        }
    }

    private sealed record Context(ConnectionProfile? Connection, ControlParameters? Control, ConfigIssues Issues);

    private static TimeSpan? OptionalDuration(ConfigMapping mapping, string key, string path, ConfigIssues issues, bool positive)
    {
        if (ConfigRead.Optional(mapping, key) is not { } node)
        {
            return null;
        }

        return positive
            ? ConfigRead.PositiveDuration(node, ConfigRead.Join(path, key), issues)
            : ConfigRead.NonNegativeDuration(node, ConfigRead.Join(path, key), issues);
    }

    private sealed record StageDraft(
        int Index,
        EntityId? Actuator,
        ConfigNode? ActuatorNode,
        EntityId? State,
        ConfigNode? StateNode,
        ImmutableArray<Phase> Phases,
        Amperes RatedCurrent,
        Watts? RatedPower,
        bool Valid)
    {
        public static StageDraft Invalid { get; } = new(0, null, null, null, null, [], Amperes.Zero, null, false);
    }

    private sealed class DeviceDraft(
        int index,
        DeviceName? id,
        ConfigNode? idNode,
        DeviceType type,
        List<StageDraft> stages,
        ModulatingRange? range,
        string? unit,
        double valuePerKwh,
        double? shedPriority,
        TimeSpan reactionTime,
        bool noShed,
        TimeSpan minOn,
        TimeSpan minOff,
        TimeSpan stateMaxAge,
        bool valid)
    {
        public int Index => index;

        public string Path => $"{Key}[{index.ToString(CultureInfo.InvariantCulture)}]";

        public DeviceName? Id => id;

        public ConfigNode? IdNode => idNode;

        public List<StageDraft> Stages => stages;

        public bool Valid { get; set; } = valid;

        public DeviceDefinition Build()
        {
            var name = id!.Value;
            return new DeviceDefinition(
                index,
                name,
                type,
                [.. stages.Select(s => new StageDefinition(
                    s.Index,
                    s.Actuator!.Value,
                    s.State!.Value,
                    s.Phases,
                    s.RatedCurrent,
                    s.RatedPower,
                    StateSignalName(name, s.Index, stages.Count)))],
                range,
                type == DeviceType.Modulating ? unit : null,
                valuePerKwh,
                shedPriority,
                reactionTime,
                noShed,
                minOn,
                minOff,
                stateMaxAge);
        }
    }
}
