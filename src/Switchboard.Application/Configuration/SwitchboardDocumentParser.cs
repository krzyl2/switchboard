using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Validates <c>switchboard.yaml</c> (schema version 1). The document is untrusted: every key and
/// value is checked, every problem is collected with its position, and only a document without a
/// single error becomes a configuration. Pure: no file access, no clock.
/// </summary>
public static class SwitchboardDocumentParser
{
    public const int SchemaVersion = 1;

    /// <summary>The literal that declares "no direction source" (SIG-03 cautious mode); never a valid signal name.</summary>
    public const string NoDirection = "none";

    private const string ControlKey = "control";

    // Schema plausibility bounds for user input, not control parameters: a rating or voltage
    // outside them is a typo (25 vs 250 A, 230 vs 23 V), not an installation.
    private static readonly Range BreakerRatingRange = new(1, 250);
    private static readonly Range VoltageRange = new(100, 300);

    private static readonly string[] RootKeys = ["schema_version", "connection", "signals", "devices", "circuit_groups", ControlKey];
    private static readonly string[] ReservedKeys = ["circuit_groups"];
    private static readonly string[] ConnectionKeys = ["topology", "breaker_rating_a", "characteristic", "voltage_v", "reserve_w"];
    private static readonly string[] SignalKeys = ["entity", "kind", "unit", "sign", "direction_from", "max_age", "sigma", "role", "phase", "unavailable_as_zero"];

    private static readonly ImmutableDictionary<string, Topology> Topologies = Choices(
        ("three_phase", Topology.ThreePhase), ("three_phase_summed", Topology.ThreePhaseSummed), ("single_phase", Topology.SinglePhase));

    private static readonly ImmutableDictionary<string, BreakerCharacteristic> Characteristics = Choices(
        ("B", BreakerCharacteristic.B), ("C", BreakerCharacteristic.C), ("unknown", BreakerCharacteristic.Unknown));

    private static readonly ImmutableDictionary<string, Phase> Phases = Choices(("L1", Phase.L1), ("L2", Phase.L2), ("L3", Phase.L3));

    private static readonly ImmutableDictionary<string, SignalKind> Kinds = Choices(
        ("current", SignalKind.Current),
        ("power", SignalKind.Power),
        ("voltage", SignalKind.Voltage),
        ("energy_counter", SignalKind.EnergyCounter),
        ("state", SignalKind.State),
        ("percentage", SignalKind.Percentage));

    private static readonly ImmutableDictionary<string, SignConvention> Signs = Choices(
        ("positive_import", SignConvention.PositiveImport),
        ("positive_export", SignConvention.PositiveExport),
        ("unsigned", SignConvention.Unsigned));

    private static readonly ImmutableDictionary<string, SignalRole> Roles = Choices(
        ("phase_current", SignalRole.PhaseCurrent),
        ("phase_power", SignalRole.PhasePower),
        ("phase_voltage", SignalRole.PhaseVoltage),
        ("inverter_power", SignalRole.InverterPower),
        ("inverter_phase_current", SignalRole.InverterPhaseCurrent),
        ("total_current", SignalRole.TotalCurrent),
        ("total_power", SignalRole.TotalPower));

    /// <param name="document">The user's document, or null for an empty file.</param>
    /// <param name="defaults">The embedded control defaults; <c>control:</c> overrides them key by key.</param>
    public static ConfigurationResult Parse(ConfigNode? document, ConfigNode defaults, string path, DateTimeOffset loadedAt)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var issues = new ConfigIssues();

        var root = document is null
            ? new ConfigMapping([], 1, 1, ConfigOrigin.Document)
            : ConfigRead.Mapping(document, string.Empty, issues);
        if (root is null)
        {
            return Finish(null, issues, path, loadedAt);
        }

        ConfigRead.CheckKeys(root, string.Empty, RootKeys, issues);
        ReadSchemaVersion(root, issues);

        var merged = ConfigNodeMerge.Merge(defaults, WithoutLimits(root.Find(ControlKey)?.Value, issues), ControlKey, issues);
        var control = ControlParametersBinder.Bind(merged, ControlKey, issues);

        var connection = ReadConnection(root, control, issues);
        var userSignals = ReadSignals(root, connection?.Topology, control, issues);
        var signalEntities = new Dictionary<EntityId, SignalName>();
        foreach (var signal in userSignals)
        {
            signalEntities.TryAdd(signal.Entity, signal.Name);
        }

        var (devices, deviceSignals) = DeviceSectionParser.Read(root, connection, control, signalEntities, issues);
        var signals = userSignals.AddRange(deviceSignals);

        foreach (var key in ReservedKeys)
        {
            if (root.Find(key) is { } reserved && !IsEmpty(reserved.Value))
            {
                issues.Warning(
                    ConfigurationIssueCodes.ReservedKeyIgnored,
                    key,
                    reserved.Value,
                    $"'{key}' is reserved for a later version and is ignored by this one.");
            }
        }

        var configuration = issues.HasErrors || connection is null || control is null
            ? null
            : new SwitchboardConfiguration(connection, signals, control, devices);
        return Finish(configuration, issues, path, loadedAt);
    }

    /// <summary>The safety envelope is never merged: <c>control.limits</c> is refused and left out of the merge.</summary>
    private static ConfigNode? WithoutLimits(ConfigNode? control, ConfigIssues issues)
    {
        if (control is not ConfigMapping mapping || mapping.Find(ControlParametersBinder.LimitsKey) is not { } limits)
        {
            return control;
        }

        issues.ErrorAtKey(
            ConfigurationIssueCodes.KeyNotAllowed,
            ConfigRead.Join(ControlKey, limits.Key),
            limits,
            "The safety envelope (limits) cannot be overridden: it bounds what the tier table may allow.");
        return mapping with { Entries = mapping.Entries.Remove(limits) };
    }

    private static ConfigurationResult Finish(SwitchboardConfiguration? configuration, ConfigIssues issues, string path, DateTimeOffset loadedAt) =>
        new(
            configuration is null ? ConfigurationStatus.Invalid : ConfigurationStatus.Valid,
            configuration,
            [.. issues.Errors],
            [.. issues.Warnings],
            path,
            loadedAt);

    private static void ReadSchemaVersion(ConfigMapping root, ConfigIssues issues)
    {
        if (ConfigRead.Required(root, "schema_version", string.Empty, issues) is not { } node)
        {
            return;
        }

        if (node is not ConfigScalar { Quoted: false } scalar
            || !int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            issues.Error(ConfigurationIssueCodes.WrongType, "schema_version", node, "Expected a whole number.");
            return;
        }

        if (version != SchemaVersion)
        {
            issues.Error(
                ConfigurationIssueCodes.UnsupportedSchemaVersion,
                "schema_version",
                node,
                $"Schema version {version} is not supported; this version reads schema version {SchemaVersion}.");
        }
    }

    private static ConnectionProfile? ReadConnection(ConfigMapping root, ControlParameters? control, ConfigIssues issues)
    {
        const string path = "connection";
        if (ConfigRead.Required(root, path, string.Empty, issues) is not { } node
            || ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            return null;
        }

        ConfigRead.CheckKeys(mapping, path, ConnectionKeys, issues);
        var topology = Read(mapping, "topology", path, issues, (n, p) => ConfigRead.Choice(n, p, issues, Topologies));
        var rating = Read(mapping, "breaker_rating_a", path, issues, (n, p) => ConfigRead.Number(n, p, issues, BreakerRatingRange));
        var characteristic = Read(mapping, "characteristic", path, issues, (n, p) => ConfigRead.Choice(n, p, issues, Characteristics));
        var voltage = Read(mapping, "voltage_v", path, issues, (n, p) => ConfigRead.Number(n, p, issues, VoltageRange));
        if (topology is not { } t || rating is not { } r || characteristic is not { } c || voltage is not { } v)
        {
            return null;
        }

        var reserve = ReadReserve(mapping, t, r * v, control, issues);
        return reserve is null ? null : new ConnectionProfile(t, new Amperes(r), c, new Volts(v), reserve);
    }

    private static ImmutableDictionary<Phase, Watts>? ReadReserve(
        ConfigMapping connection, Topology topology, double phaseCapacityW, ControlParameters? control, ConfigIssues issues)
    {
        const string path = "connection.reserve_w";
        var phases = TopologyPhases.For(topology);
        var configured = new Dictionary<Phase, (double Value, ConfigNode Node)>();
        var errorsBefore = issues.Errors.Count;

        if (ConfigRead.Optional(connection, "reserve_w") is { } node && ConfigRead.Mapping(node, path, issues) is { } mapping)
        {
            ConfigRead.CheckKeys(mapping, path, Phases.Keys.ToArray(), issues);
            foreach (var entry in mapping.Entries)
            {
                if (!Phases.TryGetValue(entry.Key, out var phase))
                {
                    continue;
                }

                var phasePath = ConfigRead.Join(path, entry.Key);
                if (!phases.Contains(phase))
                {
                    issues.ErrorAtKey(ConfigurationIssueCodes.PhaseNotInTopology, phasePath, entry, $"{entry.Key} is not a phase of a {TopologyName(topology)} connection.");
                }
                else if (ConfigRead.Number(entry.Value, phasePath, issues) is { } value)
                {
                    configured[phase] = (value, entry.Value);
                }
            }
        }

        if (control is null)
        {
            return null;
        }

        var builder = ImmutableDictionary.CreateBuilder<Phase, Watts>();
        foreach (var phase in phases)
        {
            var (value, at) = configured.TryGetValue(phase, out var set) ? set : (control.Connection.DefaultReserve.Value, (ConfigNode?)null);

            // A reserve of the whole phase capacity or more leaves nothing to allocate; a negative one is nonsense.
            if (value < 0 || value >= phaseCapacityW)
            {
                issues.Error(
                    ConfigurationIssueCodes.ReserveOutOfRange,
                    ConfigRead.Join(path, phase.ToString()),
                    at,
                    $"Reserve {value.ToString(CultureInfo.InvariantCulture)} W must be at least 0 and below the phase capacity "
                    + $"(breaker rating × voltage = {phaseCapacityW.ToString(CultureInfo.InvariantCulture)} W).");
                continue;
            }

            builder[phase] = new Watts(value);
        }

        return issues.Errors.Count > errorsBefore ? null : builder.ToImmutable();
    }

    private static ImmutableArray<SignalDefinition> ReadSignals(ConfigMapping root, Topology? topology, ControlParameters? control, ConfigIssues issues)
    {
        const string path = "signals";
        if (ConfigRead.Required(root, path, string.Empty, issues) is not { } node
            || ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            return [];
        }

        var drafts = new List<SignalDraft>();
        foreach (var entry in mapping.Entries)
        {
            if (ReadSignal(entry, topology, control, issues) is { } draft)
            {
                drafts.Add(draft);
            }
        }

        CheckDuplicateEntities(drafts, issues);
        var resolved = ResolveDirections(drafts, issues);
        if (topology is { } t)
        {
            CheckRoles(drafts, t, mapping, issues);
        }

        return resolved;
    }

    private static SignalDraft? ReadSignal(ConfigEntry entry, Topology? topology, ControlParameters? control, ConfigIssues issues)
    {
        var path = ConfigRead.Join("signals", entry.Key);
        var nameValid = SignalName.TryParse(entry.Key, out var name)
            && entry.Key != NoDirection
            && !entry.Key.StartsWith(DeviceSectionParser.SignalPrefix, StringComparison.Ordinal);
        if (!nameValid)
        {
            issues.ErrorAtKey(
                ConfigurationIssueCodes.InvalidSignalName,
                path,
                entry,
                $"'{entry.Key}' is not a valid signal name: lower-case letters, digits and '_', starting with a letter, at most 63 characters; "
                + $"'{NoDirection}' and names starting with '{DeviceSectionParser.SignalPrefix}' (device states) are reserved.");
        }

        if (ConfigRead.Mapping(entry.Value, path, issues) is not { } mapping)
        {
            if (ConfigRead.IsNull(entry.Value))
            {
                issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(path, "entity"), entry.Value, "'entity' is required.");
            }

            return null;
        }

        var errorsBefore = issues.Errors.Count;
        ConfigRead.CheckKeys(mapping, path, SignalKeys, issues);

        EntityId? entity = null;
        ConfigNode? entityNode = ConfigRead.Required(mapping, "entity", path, issues);
        if (entityNode is not null && ConfigRead.String(entityNode, ConfigRead.Join(path, "entity"), issues) is { } entityText)
        {
            if (!EntityId.TryParse(entityText, out entity))
            {
                issues.Error(ConfigurationIssueCodes.InvalidEntityId, ConfigRead.Join(path, "entity"), entityNode, $"'{entityText}' is not a Home Assistant entity ID (domain.object_id).");
            }
        }

        var kind = Read(mapping, "kind", path, issues, (n, p) => ConfigRead.Choice(n, p, issues, Kinds));
        var unit = kind is { } k ? ReadUnit(mapping, k, path, issues) : null;
        var sign = kind is { } k2 ? ReadSign(mapping, k2, path, issues) : null;
        var directionNode = ConfigRead.Optional(mapping, "direction_from");
        string? directionText = null;
        if (sign == SignConvention.Unsigned)
        {
            if (directionNode is null)
            {
                issues.Error(
                    ConfigurationIssueCodes.DirectionFromRequired,
                    ConfigRead.Join(path, "direction_from"),
                    mapping,
                    $"An unsigned signal needs direction_from: a signed power signal, or '{NoDirection}' for permanent cautious mode.");
            }
            else
            {
                directionText = ConfigRead.String(directionNode, ConfigRead.Join(path, "direction_from"), issues);
            }
        }
        else if (directionNode is not null && kind is { } directionKind
            && (sign is not null || directionKind is not (SignalKind.Current or SignalKind.Power)))
        {
            // With a missing or invalid sign the sign error already says what is wrong.
            issues.Error(ConfigurationIssueCodes.DirectionFromNotAllowed, ConfigRead.Join(path, "direction_from"), directionNode, "direction_from applies only to unsigned current or power signals.");
        }

        var maxAgeNode = ConfigRead.Required(mapping, "max_age", path, issues);
        var maxAge = maxAgeNode is null ? null : ConfigRead.PositiveDuration(maxAgeNode, ConfigRead.Join(path, "max_age"), issues);
        if (maxAge > control?.Signals.MaxAgeLimit)
        {
            issues.Error(
                ConfigurationIssueCodes.OutOfRange,
                ConfigRead.Join(path, "max_age"),
                maxAgeNode,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"max_age may be at most {control!.Signals.MaxAgeLimit.TotalSeconds:0.###} s (control.signals.max_age_limit): a frozen sensor counts as fresh for its whole max_age."));
            maxAge = null;
        }

        double? sigma = null;
        if (ConfigRead.Optional(mapping, "sigma") is { } sigmaNode)
        {
            if (kind == SignalKind.State)
            {
                issues.Error(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(path, "sigma"), sigmaNode, "A state signal has no measurement noise; remove sigma.");
            }
            else if (ConfigRead.Number(sigmaNode, ConfigRead.Join(path, "sigma"), issues, Range.AtLeast(0)) is { } raw && unit is not null)
            {
                sigma = raw * SignalUnits.Factor(unit);
            }
        }
        else if (kind is { } sigmaKind && control?.Signals.DefaultSigma.TryGetValue(sigmaKind, out var defaultSigma) == true)
        {
            sigma = defaultSigma;
        }

        var role = SignalRole.None;
        if (ConfigRead.Optional(mapping, "role") is { } roleNode && ConfigRead.Choice(roleNode, ConfigRead.Join(path, "role"), issues, Roles) is { } r)
        {
            role = r;
            CheckRoleKind(role, kind, ConfigRead.Join(path, "role"), roleNode, issues);
            if (topology == Topology.SinglePhase && role is SignalRole.TotalCurrent or SignalRole.TotalPower)
            {
                issues.Error(ConfigurationIssueCodes.RoleNotInTopology, ConfigRead.Join(path, "role"), roleNode, "A single-phase connection has no summed signals; use phase_current / phase_power on L1.");
            }
        }

        var phase = ReadPhase(mapping, role, topology, path, issues);

        var unavailableAsZero = false;
        if (ConfigRead.Optional(mapping, "unavailable_as_zero") is { } zeroNode)
        {
            if (role != SignalRole.InverterPower)
            {
                issues.Error(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(path, "unavailable_as_zero"), zeroNode, "unavailable_as_zero applies only to a signal with role inverter_power.");
            }
            else
            {
                unavailableAsZero = ConfigRead.Boolean(zeroNode, ConfigRead.Join(path, "unavailable_as_zero"), issues) ?? false;
            }
        }

        if (!nameValid || issues.Errors.Count > errorsBefore || entity is null || kind is null || maxAge is null)
        {
            return null;
        }

        return new SignalDraft(
            new SignalDefinition(name!.Value, entity.Value, kind.Value, unit, sign, null, false, maxAge.Value, sigma, role, phase, unavailableAsZero),
            directionText,
            directionNode,
            entityNode!,
            mapping);
    }

    private static string? ReadUnit(ConfigMapping mapping, SignalKind kind, string path, ConfigIssues issues)
    {
        var unitPath = ConfigRead.Join(path, "unit");
        var allowed = SignalUnits.AllowedFor(kind);
        if (allowed.IsEmpty)
        {
            if (ConfigRead.Optional(mapping, "unit") is { } stray)
            {
                issues.Error(ConfigurationIssueCodes.UnitNotAllowedForKind, unitPath, stray, $"A {KindName(kind)} signal has no unit.");
            }

            return null;
        }

        if (ConfigRead.Required(mapping, "unit", path, issues) is not { } node || ConfigRead.String(node, unitPath, issues) is not { } unit)
        {
            return null;
        }

        if (!allowed.Contains(unit))
        {
            issues.Error(ConfigurationIssueCodes.UnitNotAllowedForKind, unitPath, node, $"'{unit}' is not a unit for a {KindName(kind)} signal; use one of: {string.Join(", ", allowed)}.");
            return null;
        }

        return unit;
    }

    private static SignConvention? ReadSign(ConfigMapping mapping, SignalKind kind, string path, ConfigIssues issues)
    {
        var signPath = ConfigRead.Join(path, "sign");
        var node = ConfigRead.Optional(mapping, "sign");
        if (kind is not (SignalKind.Current or SignalKind.Power))
        {
            if (node is not null)
            {
                issues.Error(ConfigurationIssueCodes.SignNotAllowedForKind, signPath, node, $"A {KindName(kind)} signal has no sign convention.");
            }

            return null;
        }

        if (node is null)
        {
            issues.Error(ConfigurationIssueCodes.SignRequired, signPath, mapping, "Current and power signals need sign: positive_import, positive_export or unsigned.");
            return null;
        }

        return ConfigRead.Choice(node, signPath, issues, Signs);
    }

    private static void CheckRoleKind(SignalRole role, SignalKind? kind, string path, ConfigNode at, ConfigIssues issues)
    {
        var expected = role switch
        {
            SignalRole.PhaseCurrent or SignalRole.InverterPhaseCurrent or SignalRole.TotalCurrent => SignalKind.Current,
            SignalRole.PhasePower or SignalRole.InverterPower or SignalRole.TotalPower => SignalKind.Power,
            _ => SignalKind.Voltage,
        };
        if (kind is { } actual && actual != expected)
        {
            issues.Error(ConfigurationIssueCodes.RoleKindMismatch, path, at, $"Role {RoleName(role)} needs a {KindName(expected)} signal, not {KindName(actual)}.");
        }
    }

    private static Phase? ReadPhase(ConfigMapping mapping, SignalRole role, Topology? topology, string path, ConfigIssues issues)
    {
        var phasePath = ConfigRead.Join(path, "phase");
        var node = ConfigRead.Optional(mapping, "phase");
        var requirement = role switch
        {
            SignalRole.PhaseCurrent or SignalRole.PhasePower or SignalRole.PhaseVoltage or SignalRole.InverterPhaseCurrent => true,
            SignalRole.InverterPower => (bool?)null,
            _ => false,
        };

        if (node is null)
        {
            if (requirement == true)
            {
                issues.Error(ConfigurationIssueCodes.PhaseRequired, phasePath, mapping, $"Role {RoleName(role)} needs phase: L1, L2 or L3.");
            }

            return null;
        }

        if (requirement == false)
        {
            issues.Error(
                ConfigurationIssueCodes.PhaseNotAllowed,
                phasePath,
                node,
                role == SignalRole.None ? "Only a signal with a per-phase role (or inverter_power) has a phase." : $"Role {RoleName(role)} has no phase.");
            return null;
        }

        if (ConfigRead.Choice(node, phasePath, issues, Phases) is not { } phase)
        {
            return null;
        }

        if (topology is { } t && !TopologyPhases.For(t).Contains(phase))
        {
            issues.Error(ConfigurationIssueCodes.PhaseNotInTopology, phasePath, node, $"{phase} is not a phase of a {TopologyName(t)} connection.");
            return null;
        }

        return phase;
    }

    private static void CheckDuplicateEntities(List<SignalDraft> drafts, ConfigIssues issues)
    {
        var seen = new Dictionary<EntityId, SignalName>();
        foreach (var draft in drafts)
        {
            if (!seen.TryAdd(draft.Definition.Entity, draft.Definition.Name))
            {
                issues.Error(
                    ConfigurationIssueCodes.DuplicateEntity,
                    $"{draft.Definition.Path}.entity",
                    draft.EntityNode,
                    $"{draft.Definition.Entity} is already mapped by signal '{seen[draft.Definition.Entity]}'; one entity feeds one signal.");
            }
        }
    }

    private static ImmutableArray<SignalDefinition> ResolveDirections(List<SignalDraft> drafts, ConfigIssues issues)
    {
        var byName = drafts.ToDictionary(d => d.Definition.Name);
        var resolved = ImmutableArray.CreateBuilder<SignalDefinition>(drafts.Count);
        foreach (var draft in drafts)
        {
            var definition = draft.Definition;
            if (draft.DirectionText is not { } text)
            {
                resolved.Add(definition);
                continue;
            }

            var path = $"{definition.Path}.direction_from";
            if (text == NoDirection)
            {
                issues.Warning(
                    ConfigurationIssueCodes.DirectionUnavailableCautious,
                    path,
                    draft.DirectionNode,
                    "No direction source: the value always counts as import (cautious mode), so export can never be told apart from import.");
                resolved.Add(definition with { DirectionDeclaredUnavailable = true });
                continue;
            }

            if (!SignalName.TryParse(text, out var targetName) || !byName.TryGetValue(targetName.Value, out var target))
            {
                issues.Error(ConfigurationIssueCodes.DirectionFromUnknownSignal, path, draft.DirectionNode, $"'{text}' is not a signal declared in this document.");
                continue;
            }

            var targetDefinition = target.Definition;
            if (targetDefinition.Kind != SignalKind.Power || targetDefinition.Sign is null or SignConvention.Unsigned)
            {
                issues.Error(
                    ConfigurationIssueCodes.DirectionFromNotSignedPower,
                    path,
                    draft.DirectionNode,
                    $"'{text}' must be a power signal with sign positive_import or positive_export.");
                continue;
            }

            if (!PhasesCompatible(definition, targetDefinition))
            {
                issues.Error(
                    ConfigurationIssueCodes.DirectionFromPhaseMismatch,
                    path,
                    draft.DirectionNode,
                    DirectionPhaseRule(definition));
                continue;
            }

            resolved.Add(definition with { DirectionFrom = targetName });
        }

        return resolved.ToImmutable();
    }

    /// <summary>
    /// A main-meter phase signal takes its direction from the power of the same phase only: the
    /// total can be exporting while one phase imports (seen in recorded data with &gt; 5 A on the
    /// importing phase). The inverter's phase currents may use the inverter total, because the
    /// inverter only ever produces.
    /// </summary>
    private static bool PhasesCompatible(SignalDefinition signal, SignalDefinition target) => signal.Role switch
    {
        SignalRole.PhaseCurrent or SignalRole.PhasePower or SignalRole.PhaseVoltage => target.Phase == signal.Phase,
        SignalRole.InverterPhaseCurrent or SignalRole.InverterPower => target.Phase is null || target.Phase == signal.Phase,
        SignalRole.TotalCurrent or SignalRole.TotalPower => target.Phase is null,
        _ => true,
    };

    private static string DirectionPhaseRule(SignalDefinition signal) => signal.Role switch
    {
        SignalRole.TotalCurrent or SignalRole.TotalPower => "A summed signal takes its direction from a power signal without a phase (the total).",
        SignalRole.InverterPhaseCurrent or SignalRole.InverterPower => $"Use a power signal of phase {signal.Phase} or one without a phase.",
        _ => $"A phase signal takes its direction from the power of the same phase ({signal.Phase}); the total can export while one phase imports.",
    };

    private static void CheckRoles(List<SignalDraft> drafts, Topology topology, ConfigMapping signals, ConfigIssues issues)
    {
        var seen = new Dictionary<(SignalRole, Phase?), SignalName>();
        foreach (var draft in drafts.Where(d => d.Definition.Role != SignalRole.None))
        {
            var definition = draft.Definition;

            // One inverter and one summed measurement per installation, whatever phase they declare.
            var key = definition.Role is SignalRole.InverterPower or SignalRole.TotalCurrent or SignalRole.TotalPower
                ? (definition.Role, (Phase?)null)
                : (definition.Role, definition.Phase);
            if (!seen.TryAdd(key, definition.Name))
            {
                issues.Error(
                    ConfigurationIssueCodes.DuplicateRole,
                    $"{definition.Path}.role",
                    draft.Mapping.Find("role")?.Value,
                    $"Signal '{seen[key]}' already has role {RoleName(definition.Role)}{(key.Item2 is { } p ? $" on {p}" : string.Empty)}.");
            }
        }

        var phases = TopologyPhases.For(topology);
        if (topology == Topology.ThreePhaseSummed)
        {
            if (!seen.ContainsKey((SignalRole.TotalCurrent, null)))
            {
                issues.Error(ConfigurationIssueCodes.RequiredSignalMissing, "signals", signals, "A three_phase_summed connection needs a signal with role total_current.");
            }
        }
        else
        {
            foreach (var phase in phases.Where(p => !seen.ContainsKey((SignalRole.PhaseCurrent, p))))
            {
                issues.Error(
                    ConfigurationIssueCodes.RequiredSignalMissing,
                    "signals",
                    signals,
                    $"A {TopologyName(topology)} connection needs a signal with role phase_current on {phase}.");
            }
        }

        var inverterCurrents = phases.Count(p => seen.ContainsKey((SignalRole.InverterPhaseCurrent, p)));
        if (inverterCurrents > 0 && !seen.ContainsKey((SignalRole.InverterPower, null)))
        {
            issues.Error(ConfigurationIssueCodes.RequiredSignalMissing, "signals", signals, "inverter_phase_current signals need a signal with role inverter_power.");
        }
        else if (inverterCurrents > 0 && inverterCurrents < phases.Length)
        {
            issues.Error(
                ConfigurationIssueCodes.RequiredSignalMissing,
                "signals",
                signals,
                $"Map inverter_phase_current on every phase ({string.Join(", ", phases)}) or on none; production cannot be split from some phases only.");
        }
    }

    private static T? Read<T>(ConfigMapping mapping, string key, string path, ConfigIssues issues, Func<ConfigNode, string, T?> read)
        where T : struct =>
        ConfigRead.Required(mapping, key, path, issues) is { } node ? read(node, ConfigRead.Join(path, key)) : null;

    private static bool IsEmpty(ConfigNode node) => node switch
    {
        ConfigMapping m => m.Entries.IsEmpty,
        ConfigSequence s => s.Items.IsEmpty,
        _ => ConfigRead.IsNull(node),
    };

    private static ImmutableDictionary<string, T> Choices<T>(params (string Key, T Value)[] choices) =>
        choices.ToImmutableDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

    private static string KindName(SignalKind kind) => Kinds.First(k => k.Value == kind).Key;

    private static string RoleName(SignalRole role) => Roles.First(r => r.Value == role).Key;

    private static string TopologyName(Topology topology) => Topologies.First(t => t.Value == topology).Key;

    private sealed record SignalDraft(
        SignalDefinition Definition,
        string? DirectionText,
        ConfigNode? DirectionNode,
        ConfigNode EntityNode,
        ConfigMapping Mapping);
}
