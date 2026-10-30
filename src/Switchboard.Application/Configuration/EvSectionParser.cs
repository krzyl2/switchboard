using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Reads the optional top-level <c>ev:</c> (docs/EV.md, S1): data only, the algorithm parameters live in <c>control.ev</c>. Absent (or empty
/// of a value) means no EV module and the file is what it was; nothing here changes a decision in this slice. Structural problems (a wrong
/// key, type or range, an unknown device, a SoC entity another signal already reads) are errors with their position; a device that is
/// merely unsuitable (own goal, restore_only, no sources, no planner dispatch) is a warning, because a configuration problem may only
/// narrow what the module does, never invalidate the document.
/// </summary>
internal static partial class EvSectionParser
{
    public const string Key = "ev";

    /// <summary>The longest battery the section accepts; a schema bound against a typo (18 vs 180), not a control parameter.</summary>
    private const double MaxCapacityKwh = 500;

    private static readonly string[] SectionKeys = ["device", "capacity_kwh", "soc", "range", "plugged", "calendar", "cheap_zone"];
    private static readonly string[] SocKeys = ["entity", "max_age"];
    private static readonly string[] RangeKeys = ["entity", "unit", "max_age"];
    private static readonly string[] PluggedKeys = ["entity", "states", "state_reports", "max_age"];
    private static readonly string[] CalendarKeys = ["entity", "max_age"];

    // A car's SoC is a sensor, or a helper that mirrors it; never an actuator-like entity.
    private static readonly ImmutableArray<string> SocDomains = ["sensor", "input_number", "number"];

    // The range is a number in km or mi.
    private static readonly ImmutableArray<string> RangeDomains = SocDomains;

    // The plugged state is a discrete state: a car-state sensor, a binary sensor, or a helper that mirrors one.
    private static readonly ImmutableArray<string> PluggedDomains = ["sensor", "binary_sensor", "input_boolean", "input_select", "select"];

    private static readonly ImmutableArray<string> CalendarDomains = ["calendar"];

    private static readonly IReadOnlyDictionary<string, StateFreshness> Reports = new Dictionary<string, StateFreshness>(StringComparer.Ordinal) { ["age"] = StateFreshness.Age, ["on_change"] = StateFreshness.OnChange };

    /// <summary>The logical name of the SoC signal of a device's EV section; the reserved <c>device_</c> prefix keeps it clear of user signal names.</summary>
    public static SignalName SocSignal(DeviceName device) => SignalName.Parse($"{DeviceSectionParser.SignalPrefix}{device.Value}_ev_soc");

    /// <param name="signals">Every signal the document declared so far (user, group and device signals): the SoC entity may not be one of their entities.</param>
    /// <returns>The definition and the one signal it created, or nothing when the section is absent or not usable.</returns>
    public static (EvDefinition? Definition, ImmutableArray<SignalDefinition> Signals) Read(
        ConfigMapping root,
        ControlParameters? control,
        ImmutableArray<DeviceDefinition> devices,
        ImmutableArray<SignalDefinition> signals,
        EconomicsDefinition? economics,
        PlannerDefinition? planner,
        ConfigIssues issues)
    {
        if (root.Find(Key) is not { } entry || ConfigRead.IsNull(entry.Value))
        {
            return (null, []);
        }

        if (ConfigRead.Mapping(entry.Value, Key, issues) is not { } mapping)
        {
            return (null, []);
        }

        var errorsBefore = issues.Errors.Count;
        ConfigRead.CheckKeys(mapping, Key, SectionKeys, issues);
        if (control is not null)
        {
            CheckParameters(control.Ev, root, issues);
        }

        var device = ReadDevice(mapping, devices, issues);
        var capacity = ConfigRead.Required(mapping, "capacity_kwh", Key, issues) is { } capacityNode
            ? ConfigRead.Number(capacityNode, ConfigRead.Join(Key, "capacity_kwh"), issues, new Range(0, MaxCapacityKwh, MinExclusive: true))
            : null;
        var soc = ReadSoc(mapping, device, devices, signals, control, issues);
        var created = soc is { } socRead ? ImmutableArray.Create(socRead.Definition) : [];
        var range = ReadRange(mapping, device, devices, signals.AddRange(created), control, issues);
        created = range is null ? created : created.Add(range);
        var plugged = ReadPlugged(mapping, device, devices, signals.AddRange(created), control, issues);
        created = plugged is null ? created : created.Add(plugged.Value.Definition);
        var calendar = ReadCalendar(mapping, device, devices, signals.AddRange(created), control, issues);
        created = calendar is null ? created : created.Add(calendar);
        var cheapZone = ReadCheapZone(mapping, economics, issues);

        if (issues.Errors.Count > errorsBefore || device is null || capacity is null || soc is null)
        {
            return (null, []);
        }

        if (device.Goal is not null)
        {
            // The module makes the soc goal of its device itself (EV B9); two goals for one device would double count. The device keeps its own goal.
            issues.Warning(
                ConfigurationIssueCodes.EvDeviceHasGoal,
                ConfigRead.Join(Key, "device"),
                mapping.Find("device")?.Value,
                $"Device '{device.Id}' has a goal: of its own. The EV module makes the goal of its device itself, so ev: is ignored until the device goal is removed.");
            return (null, []);
        }

        if (device.Start == StartPolicy.RestoreOnly)
        {
            issues.Warning(
                ConfigurationIssueCodes.EvDeviceCannotStart,
                ConfigRead.Join(Key, "device"),
                mapping.Find("device")?.Value,
                $"Device '{device.Id}' has start: restore_only, so the planner never starts the charger; the EV module can only hold it back. Use start: headroom to let a plan start it.");
        }

        if (device.SourcesDeclared is null)
        {
            issues.Warning(
                ConfigurationIssueCodes.EvDeviceWithoutSources,
                ConfigRead.Join(Key, "device"),
                mapping.Find("device")?.Value,
                $"Device '{device.Id}' declares no sources:, so an EV plan cannot tell PV from grid energy for it. Write sources: pv_and_zone:<zone> on the device.");
        }

        if (planner is null || planner.Dispatch == PlannerDispatchMode.Off)
        {
            issues.Warning(
                ConfigurationIssueCodes.EvWithoutPlannerDispatch,
                Key,
                mapping,
                "planner.dispatch is off or there is no planner: section, so the EV module only shows a preview: no plan reaches the charger and it follows the v2 rules.");
        }

        var definition = new EvDefinition(
            device.Id,
            capacity.Value,
            soc.Value.Definition.Name,
            cheapZone,
            range?.Name,
            plugged?.Definition.Name,
            plugged?.States ?? [],
            calendar?.Name);
        return (definition, created);
    }

    /// <summary>The longest "charge now"; a schema bound against a typo, not a control parameter.</summary>
    private static readonly TimeSpan MaxChargeNowDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// The checks that relate <c>control.ev</c> keys to each other. They run only when an <c>ev:</c> section exists: a file without one must
    /// load whatever its (unused) <c>control.ev</c> says, as before the module existed. Each key's own range is checked in the binder.
    /// </summary>
    private static void CheckParameters(EvParameters p, ConfigMapping root, ConfigIssues issues)
    {
        void Invalid(string key, string message) =>
            issues.Error(ConfigurationIssueCodes.InvalidValue, $"control.ev.{key}", NodeAt(root, key), message);

        if (p.CapPercent > 0 && p.FloorPercent > p.CapPercent)
        {
            Invalid("floor", "ev.floor may not be above ev.cap: the floor would be held above the surplus ceiling.");
        }

        if (p.WeeklyFull.TargetPercent > 0 && p.WeeklyFull.TargetPercent < p.CapPercent)
        {
            Invalid("weekly_full.target", "ev.weekly_full.target may not be below ev.cap: a weekly full charge that charges less than the cap is no full charge.");
        }

        if (p.Learn.MinKwhPerKm > 0 && p.Learn.MaxKwhPerKm > 0 && p.Learn.MinKwhPerKm >= p.Learn.MaxKwhPerKm)
        {
            Invalid("learn.min_kwh_per_km", "ev.learn.min_kwh_per_km must be below max_kwh_per_km: no sample could be plausible.");
        }

        if (p.FallbackKwhPerKm > 0 && p.Learn.MaxKwhPerKm > 0 && (p.FallbackKwhPerKm < p.Learn.MinKwhPerKm || p.FallbackKwhPerKm > p.Learn.MaxKwhPerKm))
        {
            Invalid("kwh_per_km", "ev.kwh_per_km must lie within ev.learn.min_kwh_per_km and max_kwh_per_km: the fallback would be a value the learner itself rejects.");
        }

        if (p.ChargeNow.Duration > MaxChargeNowDuration)
        {
            Invalid("charge_now.duration", "ev.charge_now.duration may be at most 7 days.");
        }
    }

    /// <summary>The node the user wrote for <c>control.ev.&lt;dotted key&gt;</c>; when the key is a default, the nearest block the user did write (so the issue still has a position).</summary>
    private static ConfigNode? NodeAt(ConfigMapping root, string dotted)
    {
        ConfigNode? node = root.Find("control")?.Value;
        ConfigNode? nearest = null;
        foreach (var segment in new[] { "ev" }.Concat(dotted.Split('.')))
        {
            node = (node as ConfigMapping)?.Find(segment)?.Value;
            nearest = node ?? nearest;
        }

        return nearest;
    }

    private static DeviceDefinition? ReadDevice(ConfigMapping mapping, ImmutableArray<DeviceDefinition> devices, ConfigIssues issues)
    {
        var path = ConfigRead.Join(Key, "device");
        if (ConfigRead.Required(mapping, "device", Key, issues) is not { } node || ConfigRead.String(node, path, issues) is not { } text)
        {
            return null;
        }

        if (!DeviceName.TryParse(text, out var name))
        {
            issues.Error(ConfigurationIssueCodes.InvalidDeviceId, path, node, $"'{text}' is not a valid device id.");
            return null;
        }

        // A device id declared twice is already an error of devices: (duplicate_device_id) and drops both drafts, so it is unknown here too.
        var found = devices.FirstOrDefault(d => d.Id == name);
        if (found is null)
        {
            issues.Error(ConfigurationIssueCodes.UnknownDevice, path, node, $"'{text}' is not a device declared under devices:; the EV module can only charge a device the user declared.");
        }

        return found;
    }

    /// <summary>The logical name of a signal the EV section creates; the reserved <c>device_</c> prefix keeps it clear of user signal names.</summary>
    private static SignalName Signal(DeviceName device, string suffix) => SignalName.Parse($"{DeviceSectionParser.SignalPrefix}{device.Value}_ev_{suffix}");

    /// <summary>The entity a sub-block names, checked as the SoC always was: a well-formed ID of an allowed domain that no other signal reads and no device commands.</summary>
    private sealed record EntityRead(EntityId Entity, ConfigNode Node, ConfigMapping Block, string Path);

    /// <summary>
    /// The block <paramref name="key"/> and the entity in it. Null when the block is absent (an error first when it is required) or unusable; the issue is
    /// raised here, so a caller only checks the error count.
    /// </summary>
    private static EntityRead? ReadEntity(
        ConfigMapping mapping,
        string key,
        bool required,
        string what,
        string[] keys,
        ImmutableArray<string> domains,
        ImmutableArray<DeviceDefinition> devices,
        ImmutableArray<SignalDefinition> signals,
        ConfigIssues issues)
    {
        var path = ConfigRead.Join(Key, key);
        var node = required ? ConfigRead.Required(mapping, key, Key, issues) : ConfigRead.Optional(mapping, key);
        if (node is null || ConfigRead.IsNull(node) || ConfigRead.Mapping(node, path, issues) is not { } block)
        {
            return null;
        }

        ConfigRead.CheckKeys(block, path, keys, issues);
        var entityPath = ConfigRead.Join(path, "entity");
        if (ConfigRead.Required(block, "entity", path, issues) is not { } entityNode || ConfigRead.String(entityNode, entityPath, issues) is not { } text)
        {
            return null;
        }

        if (!EntityId.TryParse(text, out var entity))
        {
            issues.Error(ConfigurationIssueCodes.InvalidEntityId, entityPath, entityNode, $"'{text}' is not a Home Assistant entity ID (domain.object_id).");
            return null;
        }

        var domain = entity.Value.Value[..entity.Value.Value.IndexOf('.', StringComparison.Ordinal)];
        if (!domains.Contains(domain))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, entityPath, entityNode, $"{entity} cannot be {what}; use one of: {string.Join(", ", domains.Select(d => d + "."))}.");
            return null;
        }

        // One entity feeds one signal; an entity a device commands is no sensor of ours either.
        if (signals.FirstOrDefault(s => s.Entity == entity) is { } clash)
        {
            issues.Error(ConfigurationIssueCodes.DuplicateEntity, entityPath, entityNode, $"{entity} is already mapped by signal '{clash.Name}'; one entity feeds one signal.");
            return null;
        }

        if (devices.Any(d => d.Stages.Any(s => s.Actuator == entity) || d.StopActuator == entity))
        {
            issues.Error(ConfigurationIssueCodes.DuplicateEntity, entityPath, entityNode, $"{entity} is an actuator of a device; {what} must be read from a sensor of the car.");
            return null;
        }

        return new EntityRead(entity.Value, node, block, path);
    }

    /// <summary>
    /// <c>max_age</c> of a block: the declared one (positive, not above <c>control.signals.max_age_limit</c>), else <paramref name="fallback"/>. Null after an issue,
    /// or when there is no control section to take a default from.
    /// </summary>
    private static (TimeSpan? MaxAge, string Path) ReadMaxAge(EntityRead read, TimeSpan? fallback, ControlParameters? control, ConfigIssues issues)
    {
        var maxAgePath = ConfigRead.Join(read.Path, "max_age");
        if (ConfigRead.Optional(read.Block, "max_age") is not { } maxAgeNode)
        {
            return (fallback, maxAgePath);
        }

        var maxAge = ConfigRead.PositiveDuration(maxAgeNode, maxAgePath, issues);
        if (maxAge > control?.Signals.MaxAgeLimit)
        {
            issues.Error(ConfigurationIssueCodes.OutOfRange, maxAgePath, maxAgeNode, "max_age may not exceed control.signals.max_age_limit: a frozen value counts as fresh for its whole max_age.");
            return (null, maxAgePath);
        }

        return (maxAge, maxAgePath);
    }

    private static (SignalDefinition Definition, ConfigNode Node)? ReadSoc(
        ConfigMapping mapping,
        DeviceDefinition? device,
        ImmutableArray<DeviceDefinition> devices,
        ImmutableArray<SignalDefinition> signals,
        ControlParameters? control,
        ConfigIssues issues)
    {
        var errorsBefore = issues.Errors.Count;
        if (ReadEntity(mapping, "soc", true, "the state of charge", SocKeys, SocDomains, devices, signals, issues) is not { } read)
        {
            return null;
        }

        var (maxAge, maxAgePath) = ReadMaxAge(read, control?.Planner.Goal.InputMaxAge, control, issues);
        if (issues.Errors.Count > errorsBefore || maxAge is null || device is null)
        {
            return null;
        }

        // A measurement judged by age, never on change, never a protection input; its role is the EV module's.
        var definition = new SignalDefinition(SocSignal(device.Id), read.Entity, SignalKind.Percentage, "%", null, null, false, maxAge.Value, null, SignalRole.EvSoc, null, false, read.Path)
        {
            MaxAgeKey = maxAgePath,
        };
        return (definition, read.Node);
    }

    /// <summary>The electric range (not the total range with fuel): optional; <c>unit</c> km (the default) or mi, converted in the signal layer.</summary>
    private static SignalDefinition? ReadRange(
        ConfigMapping mapping,
        DeviceDefinition? device,
        ImmutableArray<DeviceDefinition> devices,
        ImmutableArray<SignalDefinition> signals,
        ControlParameters? control,
        ConfigIssues issues)
    {
        var errorsBefore = issues.Errors.Count;
        if (ReadEntity(mapping, "range", false, "the electric range", RangeKeys, RangeDomains, devices, signals, issues) is not { } read)
        {
            return null;
        }

        var unit = SignalUnits.AllowedFor(SignalKind.Distance)[0];
        var unitPath = ConfigRead.Join(read.Path, "unit");
        if (ConfigRead.Optional(read.Block, "unit") is { } unitNode && ConfigRead.String(unitNode, unitPath, issues) is { } text)
        {
            if (SignalUnits.AllowedFor(SignalKind.Distance).Contains(text))
            {
                unit = text;
            }
            else
            {
                issues.Error(ConfigurationIssueCodes.UnitNotAllowedForKind, unitPath, unitNode, $"'{text}' is not a distance unit; use one of: {string.Join(", ", SignalUnits.AllowedFor(SignalKind.Distance))}.");
            }
        }

        var (maxAge, maxAgePath) = ReadMaxAge(read, control?.Planner.Goal.InputMaxAge, control, issues);
        if (issues.Errors.Count > errorsBefore || maxAge is null || device is null)
        {
            return null;
        }

        return new SignalDefinition(Signal(device.Id, "range"), read.Entity, SignalKind.Distance, unit, null, null, false, maxAge.Value, null, SignalRole.EvRange, null, false, read.Path)
        {
            MaxAgeKey = maxAgePath,
        };
    }

    /// <summary>
    /// The plugged state: optional. <c>states</c> (required) lists the states of the entity that mean plugged in, so no vendor's wording is in code; compared
    /// without regard to case. It reports on change by default (a car-state sensor writes only when the state changes); <c>state_reports: age</c> with a
    /// <c>max_age</c> judges it by age instead.
    /// </summary>
    private static (SignalDefinition Definition, ImmutableArray<string> States)? ReadPlugged(
        ConfigMapping mapping,
        DeviceDefinition? device,
        ImmutableArray<DeviceDefinition> devices,
        ImmutableArray<SignalDefinition> signals,
        ControlParameters? control,
        ConfigIssues issues)
    {
        var errorsBefore = issues.Errors.Count;
        if (ReadEntity(mapping, "plugged", false, "the plugged state", PluggedKeys, PluggedDomains, devices, signals, issues) is not { } read)
        {
            return null;
        }

        var states = ReadStates(read, issues);
        var reports = StateFreshness.OnChange;
        var reportsPath = ConfigRead.Join(read.Path, "state_reports");
        if (ConfigRead.Optional(read.Block, "state_reports") is { } reportsNode && ConfigRead.Choice(reportsNode, reportsPath, issues, Reports) is { } declared)
        {
            reports = declared;
        }

        (TimeSpan? MaxAge, string Path) maxAge;
        if (reports == StateFreshness.OnChange)
        {
            // An on-change state has no age limit by declaration; an age next to it would be ignored silently.
            if (ConfigRead.Optional(read.Block, "max_age") is { } ignored)
            {
                issues.Error(ConfigurationIssueCodes.KeyNotAllowed, ConfigRead.Join(read.Path, "max_age"), ignored, "max_age does not apply to state_reports: on_change (freshness then comes from availability); remove one of them.");
            }

            maxAge = (control?.Planner.Goal.InputMaxAge, ConfigRead.Join(read.Path, "max_age"));
        }
        else
        {
            maxAge = ReadMaxAge(read, control?.Planner.Goal.InputMaxAge, control, issues);
        }

        if (issues.Errors.Count > errorsBefore || maxAge.MaxAge is null || states.IsDefaultOrEmpty || device is null)
        {
            return null;
        }

        var definition = new SignalDefinition(Signal(device.Id, "plugged"), read.Entity, SignalKind.State, null, null, null, false, maxAge.MaxAge.Value, null, SignalRole.EvPlugged, null, false, read.Path)
        {
            MaxAgeKey = maxAge.Path,
            Freshness = reports,
        };
        return (definition, states);
    }

    private static ImmutableArray<string> ReadStates(EntityRead read, ConfigIssues issues)
    {
        var path = ConfigRead.Join(read.Path, "states");
        if (ConfigRead.Required(read.Block, "states", read.Path, issues) is not { } node)
        {
            return [];
        }

        var items = node is ConfigSequence sequence ? sequence.Items : [node];
        if (items.IsEmpty)
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, path, node, "states may not be an empty list: name the states of the entity that mean the car is plugged in.");
            return [];
        }

        var states = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < items.Length; i++)
        {
            var itemPath = node is ConfigSequence ? $"{path}[{i}]" : path;
            if (ConfigRead.String(items[i], itemPath, issues) is not { } text)
            {
                continue;
            }

            if (text.Trim().Length == 0 || text.Trim() is SignalNormalizer.UnavailableState or SignalNormalizer.UnknownState)
            {
                issues.Error(ConfigurationIssueCodes.InvalidValue, itemPath, items[i], $"'{text}' cannot mean plugged in: an unavailable or unknown entity is no evidence of a car.");
                continue;
            }

            states.Add(text.Trim());
        }

        return states.ToImmutable();
    }

    /// <summary>
    /// The calendar of trips: optional. A calendar entity re-reports rarely (its state changes at an event's start and end), so without a
    /// <c>max_age</c> it may be as old as <c>control.signals.max_age_limit</c>; the trip itself is dropped when its start passes, whatever the age.
    /// </summary>
    private static SignalDefinition? ReadCalendar(
        ConfigMapping mapping,
        DeviceDefinition? device,
        ImmutableArray<DeviceDefinition> devices,
        ImmutableArray<SignalDefinition> signals,
        ControlParameters? control,
        ConfigIssues issues)
    {
        var errorsBefore = issues.Errors.Count;
        if (ReadEntity(mapping, "calendar", false, "the trip calendar", CalendarKeys, CalendarDomains, devices, signals, issues) is not { } read)
        {
            return null;
        }

        var (maxAge, maxAgePath) = ReadMaxAge(read, control?.Signals.MaxAgeLimit, control, issues);
        if (issues.Errors.Count > errorsBefore || maxAge is null || device is null)
        {
            return null;
        }

        return new SignalDefinition(Signal(device.Id, "calendar"), read.Entity, SignalKind.CalendarEvent, null, null, null, false, maxAge.Value, null, SignalRole.EvCalendar, null, false, read.Path)
        {
            MaxAgeKey = maxAgePath,
        };
    }

    /// <summary>
    /// <c>cheap_zone</c>: a zone id of the economics tariff. It is checked only against a zoned tariff; with none (or a dynamic or flat
    /// one) there is nothing to check it against and the id is kept as written.
    /// </summary>
    private static string? ReadCheapZone(ConfigMapping mapping, EconomicsDefinition? economics, ConfigIssues issues)
    {
        var path = ConfigRead.Join(Key, "cheap_zone");
        if (ConfigRead.Optional(mapping, "cheap_zone") is not { } node || ConfigRead.String(node, path, issues) is not { } text)
        {
            return null;
        }

        if (!ZoneId().IsMatch(text))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, path, node, $"'{text}' is not a zone id: lower-case letters, digits and '_', starting with a letter, at most 63 characters.");
            return null;
        }

        var zones = PriceSpecs.Schedules(economics?.Tariff).SelectMany(s => s.Zones).Select(z => z.Id).Distinct().ToList();
        if (zones.Count > 0 && !zones.Contains(text))
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, path, node, $"'{text}' is not a zone of economics.tariff; the zones are: {string.Join(", ", zones)}.");
            return null;
        }

        return text;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneId();
}
