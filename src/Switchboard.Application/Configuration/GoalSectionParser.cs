using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>One entity a goal reads: the signal it creates (device-scoped, like a draw) and where it was declared.</summary>
internal sealed record GoalEntityInput(
    GoalInputKind Input,
    EntityId Entity,
    ConfigNode Node,
    string Path,
    SignalKind Kind,
    string? Unit,
    SignalRole Role,
    TimeSpan MaxAge,
    string MaxAgePath);

internal sealed record GoalNumberDraft(double? Literal, GoalEntityInput? Entity);

internal sealed record GoalDeadlineDraft(TimeOnly? At, GoalEntityInput? Entity, TimeSpan Margin);

/// <summary>A goal that read without error: what <see cref="GoalSpec"/> and its signals are built from once the device id is known.</summary>
internal sealed class GoalDraft(
    GoalKind kind,
    bool hard,
    double lossFraction,
    double? capacityKwh,
    GoalNumberDraft? soc,
    GoalNumberDraft target,
    GoalDeadlineDraft? deadline,
    GoalEntityInput? want,
    SourceSet? sources,
    bool escalate,
    GoalOnMet onMet)
{
    public IEnumerable<GoalEntityInput> Entities => new[] { soc?.Entity, target.Entity, deadline?.Entity, want }.OfType<GoalEntityInput>();

    /// <summary>The logical name of the signal a goal input creates: <c>device_&lt;id&gt;_soc</c>, <c>_goal_target</c>, <c>_goal_deadline</c>, <c>_goal_want</c>.</summary>
    public static SignalName NameOf(DeviceName device, GoalInputKind input) => SignalName.Parse(input switch
    {
        GoalInputKind.Soc => $"{DeviceSectionParser.SignalPrefix}{device.Value}_soc",
        GoalInputKind.Target => $"{DeviceSectionParser.SignalPrefix}{device.Value}_goal_target",
        GoalInputKind.Deadline => $"{DeviceSectionParser.SignalPrefix}{device.Value}_goal_deadline",
        _ => $"{DeviceSectionParser.SignalPrefix}{device.Value}_goal_want",
    });

    public GoalSpec Build(DeviceName device)
    {
        GoalNumber? Number(GoalNumberDraft? draft) =>
            draft is null ? null : draft.Entity is { } entity ? GoalNumber.From(NameOf(device, entity.Input)) : GoalNumber.Of(draft.Literal!.Value);
        return new GoalSpec(
            device,
            kind,
            hard,
            lossFraction,
            capacityKwh,
            Number(soc),
            Number(target)!,
            deadline is null ? null : new GoalDeadlineSpec(deadline.At, deadline.Entity is null ? null : NameOf(device, GoalInputKind.Deadline), deadline.Margin),
            want is null ? null : NameOf(device, GoalInputKind.Want),
            sources,
            escalate,
            onMet);
    }

    /// <summary>The signals of the goal's entities: measurements judged by age, never on change, never a protection input.</summary>
    public IEnumerable<SignalDefinition> Signals(DeviceName device, string devicePath)
    {
        foreach (var input in Entities)
        {
            yield return new SignalDefinition(
                NameOf(device, input.Input),
                input.Entity,
                input.Kind,
                input.Unit,
                null,
                null,
                false,
                input.MaxAge,
                null,
                input.Role,
                null,
                false,
                devicePath)
            {
                MaxAgeKey = input.MaxAgePath,
            };
        }
    }
}

/// <summary>
/// Reads a device's <c>goal:</c> (15-PLANNER, The goal model). Each of soc, target, deadline and want is a literal or an entity,
/// and an entity becomes a device-scoped signal like a draw, so the entity ID never leaves the signal layer. Untrusted input: a
/// structural problem is an error with its position; a goal that reads but cannot ever be evaluated (a literal percentage above
/// 100) is dropped with a warning, because a configuration problem may only narrow a device, never invalidate the document.
/// </summary>
internal static class GoalSectionParser
{
    public const string Key = "goal";

    private static readonly string[] GoalKeys = ["kind", "hard", "capacity_kwh", "loss_fraction", "soc", "target", "target_kwh", "deadline", "want", "sources", "escalate", "on_met"];
    private static readonly string[] NumberKeys = ["value", "entity", "max_age"];
    private static readonly string[] EnergyNumberKeys = [.. NumberKeys, "unit"];
    private static readonly string[] DeadlineKeys = ["at", "entity", "max_age", "margin"];
    private static readonly string[] WantKeys = ["entity", "max_age"];

    private const string KindNone = "none";

    private static readonly ImmutableDictionary<string, GoalKind> Kinds = new Dictionary<string, GoalKind>
    {
        ["energy"] = GoalKind.Energy,
        ["soc"] = GoalKind.Soc,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, GoalOnMet> OnMetNames = new Dictionary<string, GoalOnMet>
    {
        ["opportunistic"] = GoalOnMet.Opportunistic,
        ["stop"] = GoalOnMet.Stop,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    // What each input may be read from: a helper or a sensor, never an actuator.
    private static readonly ImmutableArray<string> SocDomains = ["sensor", "input_number", "number"];
    private static readonly ImmutableArray<string> TargetDomains = ["input_number", "number", "sensor"];
    private static readonly ImmutableArray<string> DeadlineDomains = ["input_datetime", "sensor"];
    private static readonly ImmutableArray<string> WantDomains = ["input_boolean", "binary_sensor", "switch"];

    private const string DefaultEnergyUnit = "kWh";
    private const string PercentUnit = "%";

    /// <param name="start">The device's start policy; a goal on a restore_only device is warned.</param>
    public static GoalDraft? Read(ConfigMapping device, string devicePath, ControlParameters? control, EconomicsDefinition? economics, StartPolicy? start, ConfigIssues issues)
    {
        if (ConfigRead.Optional(device, Key) is not { } node)
        {
            return null;
        }

        var path = ConfigRead.Join(devicePath, Key);
        if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            return null;
        }

        ConfigRead.CheckKeys(mapping, path, GoalKeys, issues);
        var errorsBefore = issues.Errors.Count;
        var kindPath = ConfigRead.Join(path, "kind");
        if (ConfigRead.Required(mapping, "kind", path, issues) is not { } kindNode || ConfigRead.String(kindNode, kindPath, issues) is not { } kindText)
        {
            return null;
        }

        if (kindText == KindNone)
        {
            return null;
        }

        if (!Kinds.TryGetValue(kindText, out var kind))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, kindPath, kindNode, $"'{kindText}' is not one of: {string.Join(", ", Kinds.Keys)}, {KindNone}.");
            return null;
        }

        var soc = kind == GoalKind.Soc;
        var dropped = new List<string>();
        var hard = ConfigRead.Optional(mapping, "hard") is { } hardNode ? ConfigRead.Boolean(hardNode, ConfigRead.Join(path, "hard"), issues) : false;
        var loss = ConfigRead.Optional(mapping, "loss_fraction") is { } lossNode
            ? ConfigRead.Number(lossNode, ConfigRead.Join(path, "loss_fraction"), issues, new Range(0, 1, MaxExclusive: true))
            : control?.Planner.Goal.DefaultLossFraction;
        var escalate = ConfigRead.Optional(mapping, "escalate") is { } escalateNode ? ConfigRead.Boolean(escalateNode, ConfigRead.Join(path, "escalate"), issues) : hard;
        var onMet = ConfigRead.Optional(mapping, "on_met") is { } onMetNode
            ? ConfigRead.Choice(onMetNode, ConfigRead.Join(path, "on_met"), issues, OnMetNames)
            : GoalOnMet.Opportunistic;
        var (_, sources) = DeviceSectionParser.ReadSources(mapping, path, economics, issues);

        double? capacity = null;
        GoalNumberDraft? socDraft = null;
        GoalNumberDraft? target = null;
        if (soc)
        {
            capacity = ConfigRead.Required(mapping, "capacity_kwh", path, issues) is { } capacityNode
                ? ConfigRead.Number(capacityNode, ConfigRead.Join(path, "capacity_kwh"), issues, Range.Above(0))
                : null;
            socDraft = ConfigRead.Required(mapping, "soc", path, issues) is { } socNode
                ? ReadNumber(socNode, ConfigRead.Join(path, "soc"), GoalInputKind.Soc, false, SocDomains, "the state of charge", control, issues, dropped)
                : null;
            target = ConfigRead.Required(mapping, "target", path, issues) is { } targetNode
                ? ReadNumber(targetNode, ConfigRead.Join(path, "target"), GoalInputKind.Target, false, TargetDomains, "the target", control, issues, dropped)
                : null;
            NotAllowed(mapping, "target_kwh", path, "target_kwh applies to an energy goal; a soc goal has a target in percent.", issues);
        }
        else
        {
            target = ConfigRead.Required(mapping, "target_kwh", path, issues) is { } targetNode
                ? ReadNumber(targetNode, ConfigRead.Join(path, "target_kwh"), GoalInputKind.Target, true, TargetDomains, "the target", control, issues, dropped)
                : null;
            NotAllowed(mapping, "capacity_kwh", path, "capacity_kwh applies to a soc goal; an energy goal counts the energy its device draws.", issues);
            NotAllowed(mapping, "soc", path, "soc applies to a soc goal; an energy goal counts the energy its device draws.", issues);
            NotAllowed(mapping, "target", path, "target applies to a soc goal; an energy goal names target_kwh.", issues);
        }

        var deadline = ConfigRead.Optional(mapping, "deadline") is { } deadlineNode ? ReadDeadline(deadlineNode, ConfigRead.Join(path, "deadline"), control, issues) : null;
        var want = ConfigRead.Optional(mapping, "want") is { } wantNode ? ReadWant(wantNode, ConfigRead.Join(path, "want"), control, issues) : null;

        if (issues.Errors.Count > errorsBefore)
        {
            return null;
        }

        if (dropped.Count > 0)
        {
            issues.Warning(ConfigurationIssueCodes.GoalDropped, path, node, $"The goal is dropped and the device has none: {string.Join("; ", dropped)}.");
            return null;
        }

        if (hard is null || loss is null || escalate is null || onMet is null || target is null || (soc && (capacity is null || socDraft is null)))
        {
            return null;
        }

        if (start == StartPolicy.RestoreOnly)
        {
            issues.Warning(
                ConfigurationIssueCodes.GoalWithoutStart,
                path,
                node,
                "The device has start: restore_only, so the planner never starts it for this goal; the goal can still hold it back.");
        }

        return new GoalDraft(kind, hard.Value, loss.Value, capacity, socDraft, target, deadline, want, sources, escalate.Value, onMet.Value);
    }

    private static void NotAllowed(ConfigMapping mapping, string key, string path, string message, ConfigIssues issues)
    {
        if (mapping.Find(key) is { } entry)
        {
            issues.ErrorAtKey(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(path, key), entry, message);
        }
    }

    /// <summary>
    /// A number as a literal (<c>90</c> or <c>{ value: 90 }</c>) or an entity (<c>{ entity, max_age }</c>); the entity of an energy target
    /// may name its unit. A literal outside what the input can mean is a reason to drop the goal, not an error.
    /// </summary>
    private static GoalNumberDraft? ReadNumber(ConfigNode node, string path, GoalInputKind input, bool energy, ImmutableArray<string> domains, string what, ControlParameters? control, ConfigIssues issues, List<string> dropped)
    {
        double? literal;
        GoalEntityInput? entity = null;
        if (node is ConfigScalar)
        {
            literal = ConfigRead.Number(node, path, issues);
        }
        else
        {
            if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
            {
                return null;
            }

            ConfigRead.CheckKeys(mapping, path, energy ? EnergyNumberKeys : NumberKeys, issues);
            var value = mapping.Find("value");
            var entityNode = mapping.Find("entity");
            if ((value is null) == (entityNode is null))
            {
                issues.Error(ConfigurationIssueCodes.InvalidValue, path, mapping, "Give either value (a literal) or entity (read from Home Assistant), not both and not neither.");
                return null;
            }

            if (value is not null)
            {
                foreach (var stray in new[] { "max_age", "unit" }.Select(mapping.Find).OfType<ConfigEntry>())
                {
                    issues.ErrorAtKey(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(path, stray.Key), stray, $"{stray.Key} belongs to an entity; a literal has none.");
                }

                literal = ConfigRead.Number(value.Value, ConfigRead.Join(path, "value"), issues);
            }
            else
            {
                literal = null;
                var unit = energy ? DefaultEnergyUnit : PercentUnit;
                if (energy && ConfigRead.Optional(mapping, "unit") is { } unitNode)
                {
                    var unitPath = ConfigRead.Join(path, "unit");
                    if (ConfigRead.String(unitNode, unitPath, issues) is not { } unitText)
                    {
                        return null;
                    }

                    if (!SignalUnits.AllowedFor(SignalKind.EnergyCounter).Contains(unitText))
                    {
                        issues.Error(ConfigurationIssueCodes.UnitNotAllowedForKind, unitPath, unitNode, $"'{unitText}' is not an energy unit; use one of: {string.Join(", ", SignalUnits.AllowedFor(SignalKind.EnergyCounter))}.");
                        return null;
                    }

                    unit = unitText;
                }

                var kind = energy ? SignalKind.EnergyCounter : SignalKind.Percentage;
                entity = ReadEntityInput(mapping, entityNode!, path, input, domains, what, kind, unit, RoleOf(input), control, issues);
                if (entity is null)
                {
                    return null;
                }
            }
        }

        if (literal is not { } number)
        {
            return entity is null ? null : new GoalNumberDraft(null, entity);
        }

        var problem = input switch
        {
            GoalInputKind.Soc when number is < 0 or > 100 => "the state of charge literal must be between 0 and 100 percent",
            GoalInputKind.Target when !energy && number is < 0 or > 100 => "the target literal must be between 0 and 100 percent",
            GoalInputKind.Target when energy && number <= 0 => "the target_kwh literal must be more than 0",
            _ => null,
        };
        if (problem is not null)
        {
            dropped.Add($"{path}: {problem}");
            return null;
        }

        return new GoalNumberDraft(number, null);
    }

    private static SignalRole RoleOf(GoalInputKind input) => input switch
    {
        GoalInputKind.Soc => SignalRole.DeviceSoc,
        GoalInputKind.Target => SignalRole.GoalTarget,
        GoalInputKind.Deadline => SignalRole.GoalDeadline,
        _ => SignalRole.GoalWant,
    };

    private static GoalDeadlineDraft? ReadDeadline(ConfigNode node, string path, ControlParameters? control, ConfigIssues issues)
    {
        if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            return null;
        }

        ConfigRead.CheckKeys(mapping, path, DeadlineKeys, issues);
        var errorsBefore = issues.Errors.Count;
        var margin = ConfigRead.Optional(mapping, "margin") is { } marginNode
            ? ConfigRead.NonNegativeDuration(marginNode, ConfigRead.Join(path, "margin"), issues)
            : TimeSpan.Zero;
        var at = mapping.Find("at");
        var entityNode = mapping.Find("entity");
        if (at is null && entityNode is null)
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, path, mapping, "A deadline names at (a local time of day, \"06:30\") or entity (an input_datetime or a timestamp sensor).");
            return null;
        }

        if (at is not null && entityNode is not null)
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, path, mapping, "Give either at or entity, not both.");
            return null;
        }

        TimeOnly? time = null;
        GoalEntityInput? entity = null;
        if (at is not null)
        {
            var atPath = ConfigRead.Join(path, "at");
            if (ConfigRead.String(at.Value, atPath, issues) is not { } text)
            {
                return null;
            }

            if (!TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                issues.Error(ConfigurationIssueCodes.InvalidValue, atPath, at.Value, $"'{text}' is not a local time of day; write it as \"HH:mm\", for example \"06:30\".");
                return null;
            }

            if (mapping.Find("max_age") is { } stray)
            {
                issues.ErrorAtKey(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(path, "max_age"), stray, "max_age belongs to an entity; a recurring time has none.");
            }

            time = parsed;
        }
        else
        {
            entity = ReadEntityInput(mapping, entityNode!, path, GoalInputKind.Deadline, DeadlineDomains, "the deadline", SignalKind.State, null, SignalRole.GoalDeadline, control, issues);
            if (entity is null)
            {
                return null;
            }
        }

        return issues.Errors.Count > errorsBefore || margin is null ? null : new GoalDeadlineDraft(time, entity, margin.Value);
    }

    private static GoalEntityInput? ReadWant(ConfigNode node, string path, ControlParameters? control, ConfigIssues issues)
    {
        if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            return null;
        }

        ConfigRead.CheckKeys(mapping, path, WantKeys, issues);
        if (mapping.Find("entity") is not { } entityNode)
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(path, "entity"), mapping, "'entity' is required: want gates the goal on an input_boolean, a binary_sensor or a switch.");
            return null;
        }

        return ReadEntityInput(mapping, entityNode, path, GoalInputKind.Want, WantDomains, "the want gate", SignalKind.State, null, SignalRole.GoalWant, control, issues);
    }

    private static GoalEntityInput? ReadEntityInput(
        ConfigMapping mapping,
        ConfigEntry entityEntry,
        string path,
        GoalInputKind input,
        ImmutableArray<string> domains,
        string what,
        SignalKind kind,
        string? unit,
        SignalRole role,
        ControlParameters? control,
        ConfigIssues issues)
    {
        var entityPath = ConfigRead.Join(path, "entity");
        if (ConfigRead.String(entityEntry.Value, entityPath, issues) is not { } text)
        {
            return null;
        }

        if (!EntityId.TryParse(text, out var entity))
        {
            issues.Error(ConfigurationIssueCodes.InvalidEntityId, entityPath, entityEntry.Value, $"'{text}' is not a Home Assistant entity ID (domain.object_id).");
            return null;
        }

        var domain = entity.Value.Value[..entity.Value.Value.IndexOf('.', StringComparison.Ordinal)];
        if (!domains.Contains(domain))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, entityPath, entityEntry.Value, $"{entity} cannot be {what} of a goal; use one of: {string.Join(", ", domains.Select(d => d + "."))}.");
            return null;
        }

        var maxAge = control?.Planner.Goal.InputMaxAge;
        var maxAgePath = ConfigRead.Join(path, "max_age");
        if (ConfigRead.Optional(mapping, "max_age") is { } maxAgeNode)
        {
            maxAge = ConfigRead.PositiveDuration(maxAgeNode, maxAgePath, issues);
            if (maxAge > control?.Signals.MaxAgeLimit)
            {
                issues.Error(ConfigurationIssueCodes.OutOfRange, maxAgePath, maxAgeNode, "max_age may not exceed control.signals.max_age_limit: a frozen value counts as fresh for its whole max_age.");
                return null;
            }
        }

        return maxAge is null ? null : new GoalEntityInput(input, entity.Value, entityEntry.Value, entityPath, kind, unit, role, maxAge.Value, maxAgePath);
    }
}
