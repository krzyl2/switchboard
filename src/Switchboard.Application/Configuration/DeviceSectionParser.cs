using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Reads <c>devices:</c> (schema version 1; dispatch only added keys). A device names the entities
/// that may ever be commanded for it (its actuator, and a modulating device's stop actuator,
/// SAFE-05) and where its applied state is read; each stage's state becomes a signal with role
/// device_state, and a declared draw becomes device_current / device_power signals, so freshness
/// and units go through the signal layer like any measurement (SIG-05). Untrusted input: every key
/// is checked and every problem is reported with its position.
/// </summary>
internal static class DeviceSectionParser
{
    /// <summary>Prefix of the signals created here; refused for signals declared under <c>signals:</c>.</summary>
    public const string SignalPrefix = "device_";

    private const string Key = "devices";
    private const string ThreePhase = "three_phase";
    /// <summary>The unit of a draw current without current_unit.</summary>
    public const string DefaultUnit = "A";

    /// <summary>The unit of a draw power without power_unit.</summary>
    public const string DefaultPowerUnit = "W";
    private const string SensorDomain = "sensor";

    // Schema bound, not a control parameter: a house with more is a typo or a generated file.
    private const int MaxDevices = 64;

    // Display bound for a device's name: it labels a row on a phone screen, not a description.
    private const int MaxNameLength = 40;


    private static readonly string[] CommonKeys = ["id", "name", "type", "value_per_kwh", "shed_priority", "reaction_time", "no_shed", "min_on", "min_off", "state_max_age", "state_reports", "draw", "circuit_group", "start", "value_follows", "value_factor", "sources"];
    private static readonly string[] StageKeys = ["actuator", "state_entity", "phase", "rated_current_a", "rated_power_w"];
    private static readonly string[] OnOffKeys = [.. StageKeys, "rotation_group"];
    private static readonly string[] ModulatingKeys = ["actuator", "state_entity", "phase", "min", "max", "step", "unit", "stop_actuator", "stop_option", "resume_option"];
    private static readonly string[] StopOptionKeys = ["stop_option", "resume_option"];
    private static readonly string[] StorageKeys = ["actuator", "state_entity", "phase", "min", "max", "step", "unit"];
    private static readonly string[] SteppedKeys = ["stages"];
    private static readonly string[] DrawKeys = ["current", "power", "max_age", "current_unit", "power_unit"];

    // Keys of 03-ARCHITECTURE for later versions: accepted with a warning so a document written for
    // a later version still loads. A power range would duplicate the setpoint range, so it is refused.
    private static readonly string[] LaterKeys = ["goal", "guards"];
    private static readonly string[] PowerRangeKeys = ["power_min", "power_max"];

    private static readonly ImmutableDictionary<string, DeviceType> Types = new Dictionary<string, DeviceType>
    {
        ["onoff"] = DeviceType.OnOff,
        ["stepped"] = DeviceType.Stepped,
        ["modulating"] = DeviceType.Modulating,
        ["storage"] = DeviceType.Storage,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ValueFollows> ValueFollowsNames = new Dictionary<string, ValueFollows>
    {
        ["static"] = ValueFollows.Static,
        ["import_price"] = ValueFollows.ImportPrice,
        ["export_price"] = ValueFollows.ExportPrice,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, StateFreshness> StateReports = new Dictionary<string, StateFreshness>
    {
        ["age"] = StateFreshness.Age,
        ["on_change"] = StateFreshness.OnChange,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableArray<string> RelayActuatorDomains = ["switch", "input_boolean"];
    private static readonly ImmutableArray<string> SelectActuatorDomains = ["select", "input_select"];
    private static readonly ImmutableArray<string> ModulatingActuatorDomains = ["number", "input_number"];
    private static readonly ImmutableArray<string> RelayStateDomains = ["switch", "input_boolean", "binary_sensor"];
    private static readonly ImmutableArray<string> ModulatingStateDomains = ["number", "input_number", "sensor"];

    private static readonly ImmutableDictionary<string, Phase> PhaseNames = new Dictionary<string, Phase>
    {
        ["L1"] = Phase.L1,
        ["L2"] = Phase.L2,
        ["L3"] = Phase.L3,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <param name="signalEntities">Entities already mapped under <c>signals:</c> and by circuit groups; a device entity may not be one of them.</param>
    /// <param name="groups">The valid circuit groups, for membership and phase checks.</param>
    /// <param name="groupIds">Every group id declared (valid or not), so a device naming a broken group is not reported twice.</param>
    public static (ImmutableArray<DeviceDefinition> Devices, ImmutableArray<SignalDefinition> Signals) Read(
        ConfigMapping root,
        ConnectionProfile? connection,
        ControlParameters? control,
        IReadOnlyDictionary<EntityId, string> signalEntities,
        IReadOnlyDictionary<GroupName, CircuitGroupDefinition> groups,
        IReadOnlySet<GroupName> groupIds,
        EconomicsDefinition? economics,
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

        var context = new Context(connection, control, issues, groups, groupIds, economics);
        var drafts = new List<DeviceDraft>();
        for (var i = 0; i < sequence.Items.Length; i++)
        {
            if (ReadDevice(sequence.Items[i], i, context) is { } draft)
            {
                drafts.Add(draft);
            }
        }

        CheckUniqueness(drafts, signalEntities, issues);
        CheckRotationGroups(drafts, issues);
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
                signals.Add(StateSignal(definition, stage.StateSignal, stage.StateEntity, control));
            }

            if (definition.StopActuator is { } stop)
            {
                signals.Add(StateSignal(definition, definition.StopSignal!.Value, stop, control, relay: true));
            }

            signals.AddRange(draft.DrawSignals(definition, control));
        }

        // Derived names can collide when one device's id extends another's (a_stage1 and a stepped a).
        var seen = new Dictionary<SignalName, string>();
        foreach (var signal in signals)
        {
            if (!seen.TryAdd(signal.Name, signal.Path))
            {
                var owner = drafts.Single(d => d.Path == signal.Path);
                issues.Error(ConfigurationIssueCodes.InvalidDeviceId, $"{owner.Path}.id", owner.IdNode, $"The signal name '{signal.Name}' derived from this id is already derived by {seen[signal.Name]}; choose another id.");
                return ([], []);
            }
        }

        return (devices.ToImmutable(), signals.ToImmutable());
    }

    /// <summary>The logical name of a stage's state signal: <c>device_&lt;id&gt;</c>, or <c>device_&lt;id&gt;_stage&lt;n&gt;</c> for a stepped device.</summary>
    public static SignalName StateSignalName(DeviceName device, int stage, int stageCount) =>
        SignalName.Parse(stageCount == 1
            ? $"{SignalPrefix}{device.Value}"
            : string.Create(CultureInfo.InvariantCulture, $"{SignalPrefix}{device.Value}_stage{stage}"));

    /// <summary>The draw signal of a device phase: <c>device_&lt;id&gt;_current</c> (or <c>_power</c>), with <c>_l&lt;n&gt;</c> for a device on several phases.</summary>
    public static SignalName DrawSignalName(DeviceName device, string kind, Phase phase, bool perPhase) =>
        SignalName.Parse(perPhase ? $"{SignalPrefix}{device.Value}_{kind}_{phase.ToString().ToLowerInvariant()}" : $"{SignalPrefix}{device.Value}_{kind}");

    /// <summary>The keys a device of <paramref name="type"/> may carry; the wizard's templates are checked against them.</summary>
    public static ImmutableArray<string> KeysFor(DeviceType type) => [.. CommonKeys, .. type switch
    {
        DeviceType.OnOff => OnOffKeys,
        DeviceType.Modulating => ModulatingKeys,
        DeviceType.Storage => StorageKeys,
        _ => SteppedKeys,
    }];

    /// <summary>The domains an actuator of <paramref name="type"/> may have (SAFE-05: only these can ever be declared).</summary>
    public static ImmutableArray<string> ActuatorDomainsFor(DeviceType type) =>
        type is DeviceType.OnOff or DeviceType.Stepped ? RelayActuatorDomains : ModulatingActuatorDomains;

    /// <summary>
    /// The domains a modulating device's stop actuator may have: a switch (off stops it, on resumes
    /// it) or a select (its declared stop_option stops it, its resume_option resumes it).
    /// </summary>
    public static ImmutableArray<string> StopActuatorDomains() => [.. RelayActuatorDomains, .. SelectActuatorDomains];

    /// <summary>The stop actuator domains that take options (select_option) instead of on and off.</summary>
    public static ImmutableArray<string> SelectStopDomains => SelectActuatorDomains;

    /// <summary>The domains a state entity of <paramref name="type"/> may have.</summary>
    public static ImmutableArray<string> StateDomainsFor(DeviceType type) =>
        type is DeviceType.OnOff or DeviceType.Stepped ? RelayStateDomains : ModulatingStateDomains;

    /// <summary>The spelling of a device type in the document.</summary>
    public static string TypeName(DeviceType type) => Types.First(t => t.Value == type).Key;

    /// <summary>The device type a document spelling names, if any.</summary>
    public static DeviceType? TypeOf(string? name) => name is not null && Types.TryGetValue(name, out var type) ? type : null;

    /// <summary>Every id declared under <c>circuit_groups:</c> that is a valid group name.</summary>
    public static IReadOnlySet<GroupName> DeclaredGroupIds(ConfigMapping root)
    {
        var ids = new HashSet<GroupName>();
        if (root.Find("circuit_groups")?.Value is ConfigSequence sequence)
        {
            foreach (var item in sequence.Items)
            {
                if (item is ConfigMapping group && ConfigRead.Optional(group, "id") is ConfigScalar { IsNull: false } id && GroupName.TryParse(id.Value, out var name))
                {
                    ids.Add(name.Value);
                }
            }
        }

        return ids;
    }

    /// <summary>A direct device sensor: the sensor domain only, so an actuator or a helper can never stand in for a measurement.</summary>
    public static EntityId? ReadSensor(ConfigNode node, string path, ConfigIssues issues)
    {
        if (ConfigRead.String(node, path, issues) is not { } text)
        {
            return null;
        }

        if (!EntityId.TryParse(text, out var entity))
        {
            issues.Error(ConfigurationIssueCodes.InvalidEntityId, path, node, $"'{text}' is not a Home Assistant entity ID (domain.object_id).");
            return null;
        }

        if (Domain(entity.Value) != SensorDomain)
        {
            issues.Error(ConfigurationIssueCodes.SensorDomainRequired, path, node, $"{entity} is not a sensor; a draw or sub-meter is read from a direct device sensor (sensor.).");
            return null;
        }

        return entity;
    }

    private static SignalDefinition StateSignal(DeviceDefinition device, SignalName name, EntityId entity, ControlParameters control, bool relay = false) =>
        device.IsSetpointDevice() && !relay
            ? new SignalDefinition(
                name,
                entity,
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
            {
                Freshness = device.StateReports,
            }
            : new SignalDefinition(
                name,
                entity,
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
                device.Path)
            {
                Freshness = device.StateReports,
            };

    private static bool IsSetpointDevice(this DeviceDefinition device) => device.Type is DeviceType.Modulating or DeviceType.Storage;

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
        if (ConfigRead.Required(mapping, "type", path, issues) is { } typeNode && ConfigRead.String(typeNode, typePath, issues) is not null)
        {
            type = ConfigRead.Choice(typeNode, typePath, issues, Types);
        }

        if (type is not { } deviceType)
        {
            return null;
        }

        CheckDeviceKeys(mapping, path, deviceType, issues);
        var control = context.Control;
        var setpoint = deviceType is DeviceType.Modulating or DeviceType.Storage;
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
            ?? (setpoint ? control?.Devices.ModulatingMinOn : control?.Dispatch.RelayMinOn);
        var minOff = OptionalDuration(mapping, "min_off", path, issues, positive: false)
            ?? (setpoint ? control?.Devices.ModulatingMinOff : control?.Dispatch.RelayMinOff);
        // Null for an unknown policy (reported) or without control parameters: the draft is then
        // invalid and never built, so no default is ever made up here.
        var start = ConfigRead.Optional(mapping, "start") is { } startNode
            ? ConfigRead.Choice(startNode, ConfigRead.Join(path, "start"), issues, ControlParametersBinder.StartPolicies)
            : control?.Devices.DefaultStart;
        var stateReports = ReadStateReports(mapping, path, issues);
        var stateMaxAge = ReadStateMaxAge(mapping, path, control, stateReports, issues);
        var name = ReadName(mapping, path, issues);
        var valueFollows = ReadValueFollows(mapping, path, context.Economics, issues);
        var valueFactor = ConfigRead.Optional(mapping, "value_factor") is { } factorNode
            ? ConfigRead.Number(factorNode, ConfigRead.Join(path, "value_factor"), issues, Range.Above(0))
            : ValueSpec.NeutralFactor;
        var (sourcesDeclared, sources) = ReadSources(mapping, path, context.Economics, issues);

        var stages = new List<StageDraft>();
        ModulatingRange? range = null;
        string? unit = null;
        switch (deviceType)
        {
            case DeviceType.OnOff:
                stages.Add(ReadStage(mapping, path, 1, deviceType, context));
                break;
            case DeviceType.Modulating or DeviceType.Storage:
                (range, unit) = ReadRange(mapping, path, deviceType, context);
                stages.Add(ReadStage(mapping, path, 1, deviceType, context, range?.Max));
                break;
            case DeviceType.Stepped:
                stages.AddRange(ReadStages(mapping, path, context));
                break;
        }

        var phases = stages.Where(s => s.Valid).SelectMany(s => s.Phases).Distinct().Order().ToImmutableArray();
        var stop = deviceType == DeviceType.Modulating ? ReadStopActuator(mapping, path, issues) : (null, null, null);
        var draw = ReadDraw(mapping, path, phases, context);
        var rotation = ReadGroupName(mapping, "rotation_group", path, issues);
        var circuit = ReadCircuitGroup(mapping, path, phases, context);

        var valid = issues.Errors.Count == errorsBefore
            && id is not null && valuePerKwh is not null && reactionTime is not null && noShed is not null
            && minOn is not null && minOff is not null && stateMaxAge is not null && stateReports is not null && start is not null
            && stages.Count > 0 && stages.All(s => s.Valid) && (!setpoint || range is not null);
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
            valid)
        {
            StateReports = stateReports ?? StateFreshness.Age,
            Stop = stop.Entity,
            StopNode = stop.Node,
            StopOptions = stop.Options,
            Draw = draw,
            RotationGroup = rotation.Name,
            RotationNode = rotation.Node,
            CircuitGroup = circuit,
            ShedPriorityDeclared = shedPriority is not null,
            Name = name,
            Start = start,
            ValueFollows = valueFollows,
            ValueFactor = valueFactor ?? ValueSpec.NeutralFactor,
            Sources = sources,
            SourcesDeclared = sourcesDeclared,
        };
    }

    /// <summary>
    /// <c>value_follows</c> (static, import_price, export_price). Naming a price that no spec provides is a warning and the
    /// device stays static: a configuration problem only ever narrows, it never invents a price.
    /// </summary>
    private static ValueFollows ReadValueFollows(ConfigMapping mapping, string path, EconomicsDefinition? economics, ConfigIssues issues)
    {
        if (ConfigRead.Optional(mapping, "value_follows") is not { } node
            || ConfigRead.Choice(node, ConfigRead.Join(path, "value_follows"), issues, ValueFollowsNames) is not { } follows)
        {
            return ValueFollows.Static;
        }

        var exists = follows switch
        {
            ValueFollows.ImportPrice => economics?.Tariff is not null,
            ValueFollows.ExportPrice => economics?.Settlement?.ExportPrice is not null,
            _ => true,
        };
        if (exists)
        {
            return follows;
        }

        issues.Warning(
            ConfigurationIssueCodes.ValueFollowsWithoutPrice,
            ConfigRead.Join(path, "value_follows"),
            node,
            follows == ValueFollows.ImportPrice
                ? "value_follows: import_price needs economics.tariff; the device keeps its static value_per_kwh."
                : "value_follows: export_price needs economics.settlement.export_price; the device keeps its static value_per_kwh.");
        return ValueFollows.Static;
    }

    /// <summary>
    /// <c>sources</c> (pv_only, pv_and_zone:&lt;id&gt;[,&lt;id&gt;], any); omitted means unspecified (null, 0.9 behaviour). A named zone
    /// that economics.tariff does not have is a warning and is dropped, so the device is narrowed, never widened.
    /// </summary>
    private static (SourceSet? Declared, SourceSet? Effective) ReadSources(ConfigMapping mapping, string path, EconomicsDefinition? economics, ConfigIssues issues)
    {
        if (ConfigRead.Optional(mapping, "sources") is not { } node)
        {
            return (null, null);
        }

        var keyPath = ConfigRead.Join(path, "sources");
        if (ConfigRead.String(node, keyPath, issues) is not { } text)
        {
            return (null, null);
        }

        if (!SourceSet.TryParse(text, out var declared, out var problem))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, keyPath, node, problem!);
            return (null, null);
        }

        var known = PriceSpecs.ZoneIds(economics?.Tariff).ToHashSet(StringComparer.Ordinal);
        var effective = declared!.Narrow(known, out var dropped);
        foreach (var zone in dropped)
        {
            issues.Warning(
                ConfigurationIssueCodes.SourcesUnusable,
                keyPath,
                node,
                economics?.Tariff is null
                    ? $"sources names zone '{zone}' but there is no economics.tariff; the zone is dropped and the device is narrowed to '{effective.ToText()}'."
                    : $"sources names zone '{zone}', which economics.tariff does not have; the zone is dropped and the device is narrowed to '{effective.ToText()}'.");
        }

        return (declared, effective);
    }

    /// <summary>
    /// The optional display name: trimmed, 1 to 40 characters, no control characters. It is shown in
    /// the UI only and never takes part in a decision; the id stays what every record names.
    /// </summary>
    private static string? ReadName(ConfigMapping mapping, string path, ConfigIssues issues)
    {
        if (ConfigRead.Optional(mapping, "name") is not { } node)
        {
            return null;
        }

        var keyPath = ConfigRead.Join(path, "name");
        if (ConfigRead.String(node, keyPath, issues) is not { } text)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                keyPath,
                node,
                string.Create(CultureInfo.InvariantCulture, $"A device name is 1 to {MaxNameLength} characters of text without control characters (line breaks, tabs)."));
            return null;
        }

        return trimmed;
    }

    /// <summary>Each type has an exact key set; later keys warn, a power range is refused, anything else is unknown.</summary>
    private static void CheckDeviceKeys(ConfigMapping mapping, string path, DeviceType type, ConfigIssues issues)
    {
        var allowed = type switch
        {
            DeviceType.OnOff => CommonKeys.Concat(OnOffKeys),
            DeviceType.Modulating => CommonKeys.Concat(ModulatingKeys),
            DeviceType.Storage => CommonKeys.Concat(StorageKeys),
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
                    type is DeviceType.Modulating or DeviceType.Storage
                        ? $"'{entry.Key}' is not used: a setpoint device's range is min, max and step in its actuator's unit."
                        : $"'{entry.Key}' applies to modulating devices only, and is not used: their range is min, max and step.");
            }
            else if (entry.Key is "rotation_group" or "stop_actuator" or "stop_option" or "resume_option")
            {
                issues.ErrorAtKey(
                    ConfigurationIssueCodes.KeyNotAllowed,
                    keyPath,
                    entry,
                    entry.Key == "rotation_group"
                        ? "rotation_group applies to onoff devices only (identical elements, one per phase)."
                        : $"{entry.Key} applies to modulating devices only.");
            }
            else
            {
                issues.ErrorAtKey(ConfigurationIssueCodes.UnknownKey, keyPath, entry, $"Unknown key '{entry.Key}'. Allowed here: {string.Join(", ", keys)}.");
            }
        }
    }

    private static StateFreshness? ReadStateReports(ConfigMapping mapping, string path, ConfigIssues issues) =>
        ConfigRead.Optional(mapping, "state_reports") is { } node
            ? ConfigRead.Choice(node, ConfigRead.Join(path, "state_reports"), issues, StateReports)
            : StateFreshness.Age;

    private static TimeSpan? ReadStateMaxAge(ConfigMapping mapping, string path, ControlParameters? control, StateFreshness? reports, ConfigIssues issues)
    {
        if (ConfigRead.Optional(mapping, "state_max_age") is not { } node)
        {
            return control?.Devices.StateMaxAge;
        }

        var keyPath = ConfigRead.Join(path, "state_max_age");
        if (reports == StateFreshness.OnChange)
        {
            // An on-change state has no age limit by declaration; an age next to it would be ignored silently.
            issues.Error(ConfigurationIssueCodes.KeyNotAllowed, keyPath, node, "state_max_age does not apply to state_reports: on_change (freshness then comes from availability); remove one of them.");
            return null;
        }

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

    /// <param name="setpointMax">A setpoint device's maximum in A: its rated current.</param>
    private static StageDraft ReadStage(ConfigMapping mapping, string path, int index, DeviceType type, Context context, Amperes? setpointMax = null)
    {
        var issues = context.Issues;
        var errorsBefore = issues.Errors.Count;
        var relay = type is DeviceType.OnOff or DeviceType.Stepped;

        var actuatorNode = ConfigRead.Required(mapping, "actuator", path, issues);
        var actuator = ReadEntity(actuatorNode, ConfigRead.Join(path, "actuator"), relay ? RelayActuatorDomains : ModulatingActuatorDomains, ConfigurationIssueCodes.ActuatorDomainNotAllowed, "an actuator", issues);

        // Every actuator domain can report its own state, so the default needs no domain check.
        var stateNode = ConfigRead.Optional(mapping, "state_entity");
        var state = stateNode is null
            ? actuator
            : ReadEntity(stateNode, ConfigRead.Join(path, "state_entity"), relay ? RelayStateDomains : ModulatingStateDomains, ConfigurationIssueCodes.StateEntityDomainNotAllowed, "a state entity", issues);

        var phases = ReadPhases(mapping, path, context);

        Amperes? rated = setpointMax;
        Watts? power = null;
        double? ratedCurrent = null;
        if (relay)
        {
            var rating = context.Connection?.BreakerRating.Value ?? double.PositiveInfinity;
            if (ConfigRead.Required(mapping, "rated_current_a", path, issues) is { } currentNode
                && ConfigRead.Number(currentNode, ConfigRead.Join(path, "rated_current_a"), issues, new Range(0, rating, MinExclusive: true)) is { } current)
            {
                rated = new Amperes(current);
                ratedCurrent = current;
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
    /// layer's factor. It must be reachable in whole steps and stay within the breaker rating. A
    /// modulating range starts above zero (a stop actuator switches it off); a storage range
    /// includes zero on its step grid (min ≤ 0 &lt; max), so zero charge is always reachable.
    /// </summary>
    private static (ModulatingRange? Range, string? Unit) ReadRange(ConfigMapping mapping, string path, DeviceType type, Context context)
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
                    $"'{text}' is not a current unit; a setpoint device sets a current in one of: {string.Join(", ", SignalUnits.AllowedFor(SignalKind.Current))}.");
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
        var storage = type == DeviceType.Storage;
        var (problem, at, key) = (lo, hi, by) switch
        {
            _ when !storage && lo <= 0 => ("min must be greater than 0 (a modulating device stops through its stop_actuator).", minNode, "min"),
            _ when storage && (lo > 0 || hi <= 0) => (lo > 0 ? "A storage range must include 0 (min ≤ 0): zero charge is where protection brings it." : "max must be greater than 0: charging is the only load a storage device adds.", lo > 0 ? minNode : maxNode, lo > 0 ? "min" : "max"),
            _ when hi <= lo => ("max must be greater than min.", maxNode, "max"),
            _ when hi * factor > rating => ($"max must not exceed the breaker rating ({rating.ToString(CultureInfo.InvariantCulture)} A).", maxNode, "max"),
            _ when by <= 0 => ("step must be greater than 0.", stepNode, "step"),
            _ when !WholeSteps(hi - lo, by) => ("(max − min) must be a whole number of steps, or max is never reachable.", stepNode, "step"),
            _ when storage && !WholeSteps(-lo, by) => ("0 must be a whole number of steps above min, or zero charge is never reachable.", stepNode, "step"),
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

    /// <summary>
    /// A modulating device's stop actuator: a switch (turned off to stop, on to resume) or a select
    /// with its two declared options, stop_option and resume_option, both required there and refused
    /// anywhere else. An option is read as written, so <c>1</c> and <c>"1"</c> are the same option.
    /// </summary>
    private static (EntityId? Entity, ConfigNode? Node, StopOptions? Options) ReadStopActuator(ConfigMapping mapping, string path, ConfigIssues issues)
    {
        var present = StopOptionKeys.Select(mapping.Find).OfType<ConfigEntry>().ToList();
        if (ConfigRead.Optional(mapping, "stop_actuator") is not { } node)
        {
            foreach (var entry in present)
            {
                issues.ErrorAtKey(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(path, entry.Key), entry, $"{entry.Key} belongs to a select stop_actuator; this device declares none.");
            }

            return (null, null, null);
        }

        var entity = ReadEntity(node, ConfigRead.Join(path, "stop_actuator"), StopActuatorDomains(), ConfigurationIssueCodes.ActuatorDomainNotAllowed, "a stop actuator", issues);
        if (entity is not { } stop)
        {
            return (null, node, null);
        }

        if (!SelectActuatorDomains.Contains(Domain(stop)))
        {
            foreach (var entry in present)
            {
                issues.ErrorAtKey(
                    ConfigurationIssueCodes.KeyNotAllowed,
                    ConfigRead.Join(path, entry.Key),
                    entry,
                    $"{entry.Key} applies to a select stop_actuator only: a switch stop is turned off to stop and on to resume.");
            }

            return (stop, node, null);
        }

        var stopOption = ReadOption(mapping, "stop_option", path, issues);
        var resumeOption = ReadOption(mapping, "resume_option", path, issues);
        if (stopOption is null || resumeOption is null)
        {
            return (stop, node, null);
        }

        if (stopOption == resumeOption)
        {
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                ConfigRead.Join(path, "resume_option"),
                mapping.Find("resume_option")!.Value,
                $"resume_option must differ from stop_option ('{stopOption}'): one option stops the device, the other lets it run.");
            return (stop, node, null);
        }

        return (stop, node, new StopOptions(stopOption.Value, resumeOption.Value));
    }

    /// <summary>One option of a select stop: required, a scalar read as written, 1 to 255 characters without control characters.</summary>
    private static OptionText? ReadOption(ConfigMapping mapping, string key, string path, ConfigIssues issues)
    {
        var keyPath = ConfigRead.Join(path, key);
        if (mapping.Find(key) is not { } entry)
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, keyPath, mapping, $"'{key}' is required when stop_actuator is a select.");
            return null;
        }

        if (ConfigRead.String(entry.Value, keyPath, issues) is not { } text)
        {
            return null;
        }

        if (!OptionText.TryParse(text, out var option))
        {
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                keyPath,
                entry.Value,
                string.Create(CultureInfo.InvariantCulture, $"An option is 1 to {OptionText.MaxLength} characters of text without control characters, exactly as the select offers it."));
            return null;
        }

        return option;
    }

    /// <summary>
    /// <c>draw:</c>: the device's own sensors, a current (A or mA) and/or a power (W or kW), each
    /// either one entity (a single-phase device) or one per phase of the device, exactly.
    /// </summary>
    private static DrawDraft? ReadDraw(ConfigMapping mapping, string path, ImmutableArray<Phase> phases, Context context)
    {
        var issues = context.Issues;
        if (ConfigRead.Optional(mapping, "draw") is not { } node)
        {
            return null;
        }

        var drawPath = ConfigRead.Join(path, "draw");
        if (ConfigRead.Mapping(node, drawPath, issues) is not { } draw)
        {
            return null;
        }

        ConfigRead.CheckKeys(draw, drawPath, DrawKeys, issues);
        var current = ReadDrawEntities(draw, "current", drawPath, phases, issues);
        var power = ReadDrawEntities(draw, "power", drawPath, phases, issues);
        if (ConfigRead.Optional(draw, "current") is null && ConfigRead.Optional(draw, "power") is null)
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(drawPath, "current"), draw, "A draw needs current, power or both.");
        }

        var currentUnit = ReadUnit(draw, "current_unit", drawPath, SignalKind.Current, DefaultUnit, issues);
        var powerUnit = ReadUnit(draw, "power_unit", drawPath, SignalKind.Power, DefaultPowerUnit, issues);
        TimeSpan? maxAge = context.Control?.Devices.DrawMaxAge;
        if (ConfigRead.Optional(draw, "max_age") is { } maxAgeNode)
        {
            maxAge = ConfigRead.PositiveDuration(maxAgeNode, ConfigRead.Join(drawPath, "max_age"), issues);
            if (maxAge > context.Control?.Signals.MaxAgeLimit)
            {
                issues.Error(ConfigurationIssueCodes.OutOfRange, ConfigRead.Join(drawPath, "max_age"), maxAgeNode, "max_age may not exceed control.signals.max_age_limit: a frozen draw counts as fresh for its whole max_age.");
                maxAge = null;
            }
        }

        return current is null || power is null || currentUnit is null || powerUnit is null || maxAge is null
            ? null
            : new DrawDraft(current, power, currentUnit, powerUnit, maxAge.Value, phases.Length > 1);
    }

    private static ImmutableDictionary<Phase, (EntityId Entity, ConfigNode Node)>? ReadDrawEntities(ConfigMapping draw, string key, string drawPath, ImmutableArray<Phase> phases, ConfigIssues issues)
    {
        var keyPath = ConfigRead.Join(drawPath, key);
        if (ConfigRead.Optional(draw, key) is not { } node)
        {
            return ImmutableDictionary<Phase, (EntityId, ConfigNode)>.Empty;
        }

        if (node is ConfigScalar)
        {
            if (phases.Length > 1)
            {
                issues.Error(ConfigurationIssueCodes.WrongType, keyPath, node, $"A device on {string.Join(", ", phases)} maps its {key} per phase: {{ L1: sensor…, L2: … }}.");
                return null;
            }

            return ReadSensor(node, keyPath, issues) is { } entity && !phases.IsEmpty
                ? ImmutableDictionary<Phase, (EntityId, ConfigNode)>.Empty.Add(phases[0], (entity, node))
                : null;
        }

        if (ConfigRead.Mapping(node, keyPath, issues) is not { } perPhase)
        {
            return null;
        }

        var builder = ImmutableDictionary.CreateBuilder<Phase, (EntityId, ConfigNode)>();
        var ok = true;
        foreach (var entry in perPhase.Entries)
        {
            var entryPath = ConfigRead.Join(keyPath, entry.Key);
            if (!PhaseNames.TryGetValue(entry.Key, out var phase) || !phases.Contains(phase))
            {
                issues.ErrorAtKey(ConfigurationIssueCodes.PhaseNotInTopology, entryPath, entry, $"'{entry.Key}' is not a phase of this device ({string.Join(", ", phases)}).");
                ok = false;
            }
            else if (ReadSensor(entry.Value, entryPath, issues) is { } entity)
            {
                builder[phase] = (entity, entry.Value);
            }
            else
            {
                ok = false;
            }
        }

        if (ok && phases.Any(p => !builder.ContainsKey(p)))
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, keyPath, node, $"Map the {key} of every phase of this device ({string.Join(", ", phases)}).");
            ok = false;
        }

        return ok ? builder.ToImmutable() : null;
    }

    private static string? ReadUnit(ConfigMapping draw, string key, string drawPath, SignalKind kind, string fallback, ConfigIssues issues)
    {
        if (ConfigRead.Optional(draw, key) is not { } node)
        {
            return fallback;
        }

        if (ConfigRead.String(node, ConfigRead.Join(drawPath, key), issues) is not { } unit)
        {
            return null;
        }

        if (!SignalUnits.AllowedFor(kind).Contains(unit))
        {
            issues.Error(ConfigurationIssueCodes.UnitNotAllowedForKind, ConfigRead.Join(drawPath, key), node, $"'{unit}' is not allowed here; use one of: {string.Join(", ", SignalUnits.AllowedFor(kind))}.");
            return null;
        }

        return unit;
    }

    private static (GroupName? Name, ConfigNode? Node) ReadGroupName(ConfigMapping mapping, string key, string path, ConfigIssues issues)
    {
        if (ConfigRead.Optional(mapping, key) is not { } node || ConfigRead.String(node, ConfigRead.Join(path, key), issues) is not { } text)
        {
            return (null, null);
        }

        if (!GroupName.TryParse(text, out var name))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, ConfigRead.Join(path, key), node, $"'{text}' is not a valid group name: lower-case letters, digits and '_', starting with a letter, at most 40 characters.");
            return (null, node);
        }

        return (name, node);
    }

    /// <summary>A circuit group named by a device must be declared, and every phase of the device must be a phase of the group.</summary>
    private static GroupName? ReadCircuitGroup(ConfigMapping mapping, string path, ImmutableArray<Phase> phases, Context context)
    {
        var (name, node) = ReadGroupName(mapping, "circuit_group", path, context.Issues);
        if (name is not { } group)
        {
            return null;
        }

        var keyPath = ConfigRead.Join(path, "circuit_group");
        if (!context.GroupIds.Contains(group))
        {
            context.Issues.Error(ConfigurationIssueCodes.UnknownCircuitGroup, keyPath, node, $"No circuit group '{group}' is declared under circuit_groups.");
            return null;
        }

        if (context.Groups.TryGetValue(group, out var declared) && phases.Where(p => !declared.Phases.Contains(p)).ToList() is { Count: > 0 } outside)
        {
            context.Issues.Error(ConfigurationIssueCodes.PhaseNotInTopology, keyPath, node, $"{string.Join(", ", outside)} is not a phase of circuit group '{group}' ({string.Join(", ", declared.Phases)}).");
            return null;
        }

        return group;
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
    /// Ids are unique; an actuator (or stop actuator) is declared once across all devices and stages;
    /// every entity read (state, stop actuator, draw) feeds one signal only and is not also mapped
    /// under <c>signals:</c> or by a circuit group.
    /// </summary>
    private static void CheckUniqueness(List<DeviceDraft> drafts, IReadOnlyDictionary<EntityId, string> signalEntities, ConfigIssues issues)
    {
        var ids = new Dictionary<DeviceName, int>();
        var actuators = new Dictionary<EntityId, string>();
        var read = new Dictionary<EntityId, string>();
        void Read(DeviceDraft draft, EntityId entity, string path, ConfigNode? node, string what)
        {
            if (signalEntities.TryGetValue(entity, out var signal))
            {
                issues.Error(ConfigurationIssueCodes.DuplicateEntity, path, node, $"{entity} is already mapped by {signal}; one entity feeds one signal.");
                draft.Valid = false;
            }
            else if (!read.TryAdd(entity, path))
            {
                issues.Error(ConfigurationIssueCodes.DuplicateEntity, path, node, $"{entity} already reports {what} of {read[entity]}; one entity feeds one signal.");
                draft.Valid = false;
            }
        }

        void Command(DeviceDraft draft, EntityId entity, string owner, string path, ConfigNode? node)
        {
            if (!actuators.TryAdd(entity, owner))
            {
                issues.Error(ConfigurationIssueCodes.DuplicateActuator, path, node, $"{entity} is already the actuator of {actuators[entity]}; an entity is commanded for one device stage only.");
                draft.Valid = false;
            }
        }

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
                if (stage.Actuator is { } actuator)
                {
                    Command(draft, actuator, owner, $"{owner}.actuator", stage.ActuatorNode);
                }

                if (stage.State is { } state)
                {
                    Read(draft, state, $"{owner}.state_entity", stage.StateNode, "the state");
                }
            }

            if (draft.Stop is { } stop)
            {
                Command(draft, stop, $"{draft.Path} (stop)", $"{draft.Path}.stop_actuator", draft.StopNode);
                Read(draft, stop, $"{draft.Path}.stop_actuator", draft.StopNode, "the state");
            }

            if (draft.Draw is { } draw)
            {
                foreach (var (kind, entities) in new[] { ("current", draw.Current), ("power", draw.Power) })
                {
                    foreach (var (phase, (entity, node)) in entities.OrderBy(e => e.Key))
                    {
                        Read(draft, entity, $"{draft.Path}.draw.{kind}{(draw.PerPhase ? "." + phase : string.Empty)}", node, "the draw");
                    }
                }
            }
        }
    }

    /// <summary>
    /// A rotation group (DISP-10) holds identical elements, one per phase, whose order is free: at
    /// least two onoff devices on distinct single phases with equal rated current, rated power and
    /// value per kWh, and no shed priority (which would make their order not free).
    /// </summary>
    private static void CheckRotationGroups(List<DeviceDraft> drafts, ConfigIssues issues)
    {
        foreach (var group in drafts.Where(d => d.RotationGroup is not null).GroupBy(d => d.RotationGroup!.Value))
        {
            var members = group.ToList();
            var first = members[0];
            string? Problem(DeviceDraft d) =>
                members.Count < 2 ? $"Rotation group '{group.Key}' has a single member; it needs at least two identical elements."
                : d.Type != DeviceType.OnOff || d.Stages.Count != 1 || d.Stages[0].Phases.Length != 1 ? "A rotation group member is an onoff device on one phase."
                : members.Count(m => m.Stages[0].Phases.SequenceEqual(d.Stages[0].Phases)) > 1 ? $"Two members of rotation group '{group.Key}' are on {d.Stages[0].Phases[0]}; one element per phase."
                : d.Stages[0].RatedCurrent != first.Stages[0].RatedCurrent || d.Stages[0].RatedPower != first.Stages[0].RatedPower || d.ValuePerKwh != first.ValuePerKwh
                    ? $"Members of rotation group '{group.Key}' must have equal rated_current_a, rated_power_w and value_per_kwh."
                : d.ValueFollows != first.ValueFollows || d.ValueFactor != first.ValueFactor || !Equals(d.SourcesDeclared, first.SourcesDeclared)
                    ? $"Members of rotation group '{group.Key}' must have equal value_follows, value_factor and sources."
                : d.ShedPriorityDeclared ? "A rotation group member takes no shed_priority: the phase ranking orders the elements."
                : null;
            foreach (var member in members)
            {
                if (Problem(member) is { } problem)
                {
                    issues.Error(ConfigurationIssueCodes.RotationGroupMismatch, $"{member.Path}.rotation_group", member.RotationNode, problem);
                    member.Valid = false;
                }
            }
        }
    }

    private sealed record Context(
        ConnectionProfile? Connection,
        ControlParameters? Control,
        ConfigIssues Issues,
        IReadOnlyDictionary<GroupName, CircuitGroupDefinition> Groups,
        IReadOnlySet<GroupName> GroupIds,
        EconomicsDefinition? Economics);

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

    private sealed record DrawDraft(
        ImmutableDictionary<Phase, (EntityId Entity, ConfigNode Node)> Current,
        ImmutableDictionary<Phase, (EntityId Entity, ConfigNode Node)> Power,
        string CurrentUnit,
        string PowerUnit,
        TimeSpan MaxAge,
        bool PerPhase);

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

        public DeviceType Type => type;

        public double ValuePerKwh => valuePerKwh;

        public List<StageDraft> Stages => stages;

        public bool Valid { get; set; } = valid;

        public StateFreshness StateReports { get; init; }

        public EntityId? Stop { get; init; }

        public ConfigNode? StopNode { get; init; }

        public DrawDraft? Draw { get; init; }

        public GroupName? RotationGroup { get; init; }

        public ConfigNode? RotationNode { get; init; }

        public GroupName? CircuitGroup { get; init; }

        public bool ShedPriorityDeclared { get; init; }

        public string? Name { get; init; }

        public StopOptions? StopOptions { get; init; }

        public ValueFollows ValueFollows { get; init; }

        public double ValueFactor { get; init; } = ValueSpec.NeutralFactor;

        public SourceSet? Sources { get; init; }

        public SourceSet? SourcesDeclared { get; init; }

        /// <summary>Null when the policy was unknown or no control parameters were read: such a draft is never built.</summary>
        public StartPolicy? Start { get; init; }

        public DeviceDefinition Build()
        {
            var name = id!.Value;
            var start = Start ?? throw new InvalidOperationException("A valid device draft always has a start policy.");
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
                type is DeviceType.Modulating or DeviceType.Storage ? unit : null,
                valuePerKwh,
                shedPriority,
                reactionTime,
                noShed,
                minOn,
                minOff,
                stateMaxAge)
            {
                StateReports = StateReports,
                StopActuator = Stop,
                StopOptions = StopOptions,
                StopSignal = Stop is null ? null : SignalName.Parse($"{SignalPrefix}{name.Value}_stop"),
                Draw = Draw is null ? null : new DeviceDrawDefinition(
                    Draw.Current.ToImmutableDictionary(c => c.Key, c => DrawSignalName(name, "current", c.Key, Draw.PerPhase)),
                    Draw.Power.ToImmutableDictionary(p => p.Key, p => DrawSignalName(name, "power", p.Key, Draw.PerPhase)),
                    Draw.MaxAge),
                RotationGroup = RotationGroup,
                CircuitGroup = CircuitGroup,
                Name = Name,
                Start = start,
                ValueFollows = ValueFollows,
                ValueFactor = ValueFactor,
                Sources = Sources,
                SourcesDeclared = SourcesDeclared,
            };
        }

        /// <summary>The draw signals: measurements, always judged by age (on_change is refused for them).</summary>
        public IEnumerable<SignalDefinition> DrawSignals(DeviceDefinition device, ControlParameters control)
        {
            if (Draw is null || device.Draw is null)
            {
                yield break;
            }

            foreach (var (phase, (entity, _)) in Draw.Current.OrderBy(c => c.Key))
            {
                yield return Measurement(device.Draw.Current[phase], entity, SignalKind.Current, Draw.CurrentUnit, SignalRole.DeviceCurrent, phase, control);
            }

            foreach (var (phase, (entity, _)) in Draw.Power.OrderBy(p => p.Key))
            {
                yield return Measurement(device.Draw.Power[phase], entity, SignalKind.Power, Draw.PowerUnit, SignalRole.DevicePower, phase, control);
            }
        }

        private SignalDefinition Measurement(SignalName name, EntityId entity, SignalKind kind, string unit, SignalRole role, Phase phase, ControlParameters control) =>
            new(
                name,
                entity,
                kind,
                unit,
                SignConvention.PositiveImport,
                null,
                false,
                Draw!.MaxAge,
                control.Signals.DefaultSigma.GetValueOrDefault(kind),
                role,
                phase,
                false,
                Path)
            {
                MaxAgeKey = $"{Path}.draw.max_age",
            };
    }
}
