using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Reads <c>circuit_groups:</c> (CONN-03, schema version 1): a downstream breaker shared by some
/// devices, with its own rating, characteristic (its tier table comes from the same defaults and
/// envelope as the main breaker), reserve and, optionally, a sub-meter current per phase. Without a
/// sub-meter the guard estimates the group current as the worst case of its members. Untrusted
/// input: every key is checked and reported with its position.
/// </summary>
internal static class CircuitGroupSectionParser
{
    /// <summary>
    /// Prefix of the sub-meter signals created here (<c>device__&lt;id&gt;_current_l1</c>). It lies
    /// inside the reserved <c>device_</c> space, and a device id starts with a letter, so no user
    /// signal and no device signal can take one; no new prefix is reserved in schema version 1.
    /// </summary>
    public const string SignalPrefix = DeviceSectionParser.SignalPrefix + "_";

    private const string Key = "circuit_groups";
    private const string DefaultUnit = "A";

    // Schema bounds, not control parameters: plausibility of user input.
    private const int MaxGroups = 16;
    private static readonly Range RatingRange = new(1, 250);

    private static readonly string[] GroupKeys = ["id", "breaker_rating_a", "characteristic", "phases", "reserve_w", "current", "current_max_age", "current_unit"];

    private static readonly ImmutableDictionary<string, BreakerCharacteristic> Characteristics = new Dictionary<string, BreakerCharacteristic>
    {
        ["B"] = BreakerCharacteristic.B,
        ["C"] = BreakerCharacteristic.C,
        ["unknown"] = BreakerCharacteristic.Unknown,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, Phase> PhaseNames = new Dictionary<string, Phase>
    {
        ["L1"] = Phase.L1,
        ["L2"] = Phase.L2,
        ["L3"] = Phase.L3,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>The sub-meter signal of a group phase.</summary>
    public static SignalName CurrentSignalName(GroupName group, Phase phase) =>
        SignalName.Parse($"{SignalPrefix}{group.Value}_current_{phase.ToString().ToLowerInvariant()}");

    /// <param name="entities">Entities already mapped (signals and devices); a sub-meter entity may not be one of them.</param>
    public static (ImmutableArray<CircuitGroupDefinition> Groups, ImmutableArray<SignalDefinition> Signals) Read(
        ConfigMapping root,
        ConnectionProfile? connection,
        ControlParameters? control,
        ConfigIssues issues)
    {
        if (root.Find(Key) is not { } entry || ConfigRead.IsNull(entry.Value))
        {
            return ([], []);
        }

        if (entry.Value is not ConfigSequence sequence)
        {
            issues.Error(ConfigurationIssueCodes.WrongType, Key, entry.Value, "Expected a list of circuit groups.");
            return ([], []);
        }

        if (sequence.Items.Length > MaxGroups)
        {
            issues.Error(ConfigurationIssueCodes.OutOfRange, Key, sequence, $"At most {MaxGroups} circuit groups may be declared.");
        }

        var groups = ImmutableArray.CreateBuilder<CircuitGroupDefinition>();
        var signals = ImmutableArray.CreateBuilder<SignalDefinition>();
        var ids = new Dictionary<GroupName, int>();
        var valid = true;
        for (var i = 0; i < sequence.Items.Length; i++)
        {
            var errorsBefore = issues.Errors.Count;
            var group = ReadGroup(sequence.Items[i], i, connection, control, issues);
            if (group is not null && !ids.TryAdd(group.Id, i))
            {
                issues.Error(ConfigurationIssueCodes.InvalidValue, $"{group.Path}.id", (sequence.Items[i] as ConfigMapping)?.Find("id")?.Value, $"Circuit group '{group.Id}' is already declared by circuit_groups[{ids[group.Id]}].");
                group = null;
            }

            if (group is null || issues.Errors.Count > errorsBefore)
            {
                valid = false;
                continue;
            }

            groups.Add(group);
            foreach (var (phase, name) in group.Current)
            {
                // A downstream load circuit cannot export: a sub-meter is read unsigned, so a
                // negative sample (a reversed CT, a meter signing current by power direction) is
                // refused as a sign violation and the group's members are held off, instead of being
                // read as an export that protection must never shed.
                signals.Add(new SignalDefinition(
                    name,
                    SubMeterEntity(sequence.Items[i], phase)!.Value,
                    SignalKind.Current,
                    UnitOf(sequence.Items[i]),
                    SignConvention.Unsigned,
                    null,
                    false,
                    group.CurrentMaxAge!.Value,
                    control!.Signals.DefaultSigma.GetValueOrDefault(SignalKind.Current),
                    SignalRole.GroupCurrent,
                    phase,
                    false,
                    group.Path)
                {
                    MaxAgeKey = $"{group.Path}.current_max_age",
                });
            }
        }

        return valid && control is not null ? (groups.ToImmutable(), signals.ToImmutable()) : ([], []);
    }

    private static CircuitGroupDefinition? ReadGroup(ConfigNode node, int index, ConnectionProfile? connection, ControlParameters? control, ConfigIssues issues)
    {
        var path = $"{Key}[{index.ToString(CultureInfo.InvariantCulture)}]";
        if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            if (ConfigRead.IsNull(node))
            {
                issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(path, "id"), node, "'id' is required.");
            }

            return null;
        }

        ConfigRead.CheckKeys(mapping, path, GroupKeys, issues);
        GroupName? id = null;
        if (ConfigRead.Required(mapping, "id", path, issues) is { } idNode && ConfigRead.String(idNode, ConfigRead.Join(path, "id"), issues) is { } idText)
        {
            if (!GroupName.TryParse(idText, out id))
            {
                issues.Error(
                    ConfigurationIssueCodes.InvalidValue,
                    ConfigRead.Join(path, "id"),
                    idNode,
                    $"'{idText}' is not a valid group id: lower-case letters, digits and '_', starting with a letter, at most 40 characters.");
            }
        }

        double? rating = null;
        if (ConfigRead.Required(mapping, "breaker_rating_a", path, issues) is { } ratingNode)
        {
            rating = ConfigRead.Number(ratingNode, ConfigRead.Join(path, "breaker_rating_a"), issues, RatingRange);
            if (rating > connection?.BreakerRating.Value)
            {
                // Accepted: the main limit binds anyway, and both are enforced.
                issues.Warning(
                    ConfigurationIssueCodes.GroupRatingAboveMain,
                    ConfigRead.Join(path, "breaker_rating_a"),
                    ratingNode,
                    string.Create(CultureInfo.InvariantCulture, $"The group breaker ({rating} A) is rated above the main breaker ({connection!.BreakerRating.Value} A); the main limit binds first."));
            }
        }

        var characteristic = ConfigRead.Required(mapping, "characteristic", path, issues) is { } characteristicNode
            ? ConfigRead.Choice(characteristicNode, ConfigRead.Join(path, "characteristic"), issues, Characteristics)
            : null;
        var phases = ReadPhases(mapping, path, connection, issues);
        var reserve = phases is null || rating is null || connection is null || control is null
            ? null
            : ReadReserve(mapping, path, phases.Value, rating.Value * connection.Voltage.Value, control, issues);
        var (current, maxAge) = ReadCurrent(mapping, path, id, phases, control, issues);

        if (id is null || rating is null || characteristic is null || phases is null || reserve is null || current is null)
        {
            return null;
        }

        return new CircuitGroupDefinition(index, id.Value, new Amperes(rating.Value), characteristic.Value, phases.Value, reserve, current, maxAge, []);
    }

    private static ImmutableArray<Phase>? ReadPhases(ConfigMapping mapping, string path, ConnectionProfile? connection, ConfigIssues issues)
    {
        var phasesPath = ConfigRead.Join(path, "phases");
        if (ConfigRead.Required(mapping, "phases", path, issues) is not { } node)
        {
            return null;
        }

        if (node is not ConfigSequence sequence || sequence.Items.IsEmpty)
        {
            issues.Error(ConfigurationIssueCodes.WrongType, phasesPath, node, "Expected a non-empty list of phases (L1, L2, L3).");
            return null;
        }

        var phases = new List<Phase>();
        var ok = true;
        foreach (var item in sequence.Items)
        {
            if (ConfigRead.Choice(item, phasesPath, issues, PhaseNames) is not { } phase)
            {
                ok = false;
                continue;
            }

            if (connection is not null && !connection.Phases.Contains(phase))
            {
                issues.Error(ConfigurationIssueCodes.PhaseNotInTopology, phasesPath, item, $"{phase} is not a phase of this connection.");
                ok = false;
            }
            else if (phases.Contains(phase))
            {
                issues.Error(ConfigurationIssueCodes.InvalidValue, phasesPath, item, $"{phase} is listed twice.");
                ok = false;
            }
            else
            {
                phases.Add(phase);
            }
        }

        return ok ? [.. phases.Order()] : null;
    }

    private static ImmutableDictionary<Phase, Watts>? ReadReserve(ConfigMapping mapping, string path, ImmutableArray<Phase> phases, double capacityW, ControlParameters control, ConfigIssues issues)
    {
        var reservePath = ConfigRead.Join(path, "reserve_w");
        var configured = new Dictionary<Phase, (double Value, ConfigNode Node)>();
        var errorsBefore = issues.Errors.Count;
        if (ConfigRead.Optional(mapping, "reserve_w") is { } node && ConfigRead.Mapping(node, reservePath, issues) is { } reserve)
        {
            ConfigRead.CheckKeys(reserve, reservePath, PhaseNames.Keys.ToArray(), issues);
            foreach (var entry in reserve.Entries)
            {
                if (!PhaseNames.TryGetValue(entry.Key, out var phase))
                {
                    continue;
                }

                if (!phases.Contains(phase))
                {
                    issues.ErrorAtKey(ConfigurationIssueCodes.PhaseNotInTopology, ConfigRead.Join(reservePath, entry.Key), entry, $"{entry.Key} is not a phase of this group.");
                }
                else if (ConfigRead.Number(entry.Value, ConfigRead.Join(reservePath, entry.Key), issues) is { } value)
                {
                    configured[phase] = (value, entry.Value);
                }
            }
        }

        var builder = ImmutableDictionary.CreateBuilder<Phase, Watts>();
        foreach (var phase in phases)
        {
            var (value, at) = configured.TryGetValue(phase, out var set) ? set : (control.CircuitGroups.DefaultReserve.Value, (ConfigNode?)null);
            if (value < 0 || value >= capacityW)
            {
                issues.Error(
                    ConfigurationIssueCodes.ReserveOutOfRange,
                    ConfigRead.Join(reservePath, phase.ToString()),
                    at,
                    $"Reserve {value.ToString(CultureInfo.InvariantCulture)} W must be at least 0 and below the group's phase capacity (rating × voltage = {capacityW.ToString(CultureInfo.InvariantCulture)} W).");
                continue;
            }

            builder[phase] = new Watts(value);
        }

        return issues.Errors.Count > errorsBefore ? null : builder.ToImmutable();
    }

    /// <summary>The optional sub-meter: one sensor per group phase, exactly, with a required current_max_age.</summary>
    private static (ImmutableDictionary<Phase, SignalName>? Current, TimeSpan? MaxAge) ReadCurrent(
        ConfigMapping mapping, string path, GroupName? id, ImmutableArray<Phase>? phases, ControlParameters? control, ConfigIssues issues)
    {
        var currentNode = ConfigRead.Optional(mapping, "current");
        var maxAgeNode = ConfigRead.Optional(mapping, "current_max_age");
        var unitNode = ConfigRead.Optional(mapping, "current_unit");
        if (currentNode is null)
        {
            foreach (var (key, stray) in new[] { ("current_max_age", maxAgeNode), ("current_unit", unitNode) })
            {
                if (stray is not null)
                {
                    issues.Error(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(path, key), stray, $"{key} applies only with a sub-meter (current).");
                }
            }

            return (ImmutableDictionary<Phase, SignalName>.Empty, null);
        }

        var currentPath = ConfigRead.Join(path, "current");
        TimeSpan? maxAge = null;
        if (maxAgeNode is null)
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(path, "current_max_age"), mapping, "A sub-meter needs current_max_age: a frozen current must turn stale.");
        }
        else
        {
            maxAge = ConfigRead.PositiveDuration(maxAgeNode, ConfigRead.Join(path, "current_max_age"), issues);
            if (maxAge > control?.Signals.MaxAgeLimit)
            {
                issues.Error(ConfigurationIssueCodes.OutOfRange, ConfigRead.Join(path, "current_max_age"), maxAgeNode, "current_max_age may not exceed control.signals.max_age_limit.");
                maxAge = null;
            }
        }

        if (unitNode is not null && ConfigRead.String(unitNode, ConfigRead.Join(path, "current_unit"), issues) is { } unit && !SignalUnits.AllowedFor(SignalKind.Current).Contains(unit))
        {
            issues.Error(ConfigurationIssueCodes.UnitNotAllowedForKind, ConfigRead.Join(path, "current_unit"), unitNode, $"'{unit}' is not a current unit; use one of: {string.Join(", ", SignalUnits.AllowedFor(SignalKind.Current))}.");
        }

        if (ConfigRead.Mapping(currentNode, currentPath, issues) is not { } perPhase)
        {
            return (null, maxAge);
        }

        var names = ImmutableDictionary.CreateBuilder<Phase, SignalName>();
        var ok = true;
        foreach (var entry in perPhase.Entries)
        {
            var entryPath = ConfigRead.Join(currentPath, entry.Key);
            if (!PhaseNames.TryGetValue(entry.Key, out var phase) || phases is { } declared && !declared.Contains(phase))
            {
                issues.ErrorAtKey(ConfigurationIssueCodes.PhaseNotInTopology, entryPath, entry, $"'{entry.Key}' is not a phase of this group.");
                ok = false;
                continue;
            }

            if (DeviceSectionParser.ReadSensor(entry.Value, entryPath, issues) is null)
            {
                ok = false;
                continue;
            }

            if (id is { } group)
            {
                names[phase] = CurrentSignalName(group, phase);
            }
        }

        if (phases is { } all && all.Any(p => !names.ContainsKey(p)) && ok && id is not null)
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, currentPath, currentNode, $"A sub-meter maps every phase of the group ({string.Join(", ", all)}).");
            ok = false;
        }

        return ok && maxAge is not null ? (names.ToImmutable(), maxAge) : (null, maxAge);
    }

    private static EntityId? SubMeterEntity(ConfigNode group, Phase phase) =>
        group is ConfigMapping mapping
        && ConfigRead.Optional(mapping, "current") is ConfigMapping current
        && ConfigRead.Optional(current, phase.ToString()) is ConfigScalar { IsNull: false } scalar
        && EntityId.TryParse(scalar.Value, out var entity)
            ? entity
            : null;

    private static string UnitOf(ConfigNode group) =>
        group is ConfigMapping mapping && ConfigRead.Optional(mapping, "current_unit") is ConfigScalar { IsNull: false } unit ? unit.Value : DefaultUnit;

    /// <summary>The entities a group reads, for the document-wide one-entity-one-signal check.</summary>
    public static IEnumerable<(EntityId Entity, string Path, ConfigNode Node)> Entities(ConfigMapping root)
    {
        if (root.Find(Key)?.Value is not ConfigSequence sequence)
        {
            yield break;
        }

        for (var i = 0; i < sequence.Items.Length; i++)
        {
            if (sequence.Items[i] is not ConfigMapping group || ConfigRead.Optional(group, "current") is not ConfigMapping current)
            {
                continue;
            }

            foreach (var entry in current.Entries)
            {
                if (entry.Value is ConfigScalar { IsNull: false } scalar && EntityId.TryParse(scalar.Value, out var entity))
                {
                    yield return (entity.Value, $"{Key}[{i.ToString(CultureInfo.InvariantCulture)}].current.{entry.Key}", entry.Value);
                }
            }
        }
    }
}
