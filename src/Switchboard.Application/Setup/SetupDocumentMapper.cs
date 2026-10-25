using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup;

/// <summary>A part of the file the wizard does not edit; kept exactly as written (UI-06).</summary>
public sealed record PreservedSection(string Path, string Summary);

/// <summary>The draft read from a file, and what in that file the wizard keeps without editing it.</summary>
public sealed record SetupLoad(SetupDraft Draft, ImmutableArray<PreservedSection> Preserved);

/// <summary>A draft applied to a document: the new tree, which field wrote which path, and what could not be applied.</summary>
public sealed record MappedDocument(ConfigMapping Root, FieldMap Fields, ImmutableArray<SetupIssue> Issues);

/// <summary>
/// Maps between switchboard.yaml (schema version 1, unchanged) and the wizard's draft. It reads the
/// parsed tree, not the validated configuration, so a file that parses but fails the schema still
/// loads into the wizard. Applying a draft patches that same tree: every key the wizard does not
/// own stays as written, signals are found by role and phase (never by name), an existing device
/// keeps its id and type, and a device the wizard cannot represent is copied verbatim.
/// </summary>
public static class SetupDocumentMapper
{
    private const string Signals = "signals";
    private const string Devices = "devices";
    private const string Connection = "connection";
    private const string ThreePhase = "three_phase";
    private const string Unsigned = "unsigned";
    private const string PhasePowerDirection = "phase_power";

    private static readonly Phase[] AllPhases = [Phase.L1, Phase.L2, Phase.L3];

    // Device keys the wizard owns, with their draft field names. Everything else under a device is kept.
    private static readonly (string Key, string Field)[] DeviceFields =
    [
        ("id", "id"), ("name", "name"), ("type", "type"), ("actuator", "actuator"), ("state_entity", "stateEntity"), ("phase", "phase"),
        ("rated_current_a", "ratedCurrentA"), ("rated_power_w", "ratedPowerW"), ("min", "min"), ("max", "max"), ("step", "step"),
        ("value_per_kwh", "valuePerKwh"), ("state_reports", "stateReports"), ("state_max_age", "stateReports"), ("reaction_time", "reactionTime"),
        ("min_on", "minOn"), ("min_off", "minOff"), ("rotation_group", "rotationGroup"), ("circuit_group", "circuitGroup"), ("draw", "draw"),
        ("stop_actuator", "stopActuator"), ("stop_option", "stopOption"), ("resume_option", "resumeOption"), ("start", "start"),
    ];

    private static readonly string[] EditableTypes = ["onoff", "modulating", "storage"];

    public static SetupLoad Read(ConfigNode? root)
    {
        if (root is not ConfigMapping document)
        {
            return new SetupLoad(new SetupDraft(), []);
        }

        var preserved = ImmutableArray.CreateBuilder<PreservedSection>();
        foreach (var entry in document.Entries.Where(e => e.Key is not ("schema_version" or Connection or Signals or Devices)))
        {
            preserved.Add(new PreservedSection(entry.Key, Summary(entry.Key, entry.Value)));
        }

        var connection = document.GetMapping(Connection);
        var reserve = connection?.GetMapping("reserve_w");
        var draft = new SetupDraft
        {
            Connection = new ConnectionDraft
            {
                Topology = ConfigTree.AsText(connection?.Get("topology")),
                BreakerRatingA = ConfigTree.AsNumber(connection?.Get("breaker_rating_a")),
                Characteristic = ConfigTree.AsText(connection?.Get("characteristic")),
                VoltageV = ConfigTree.AsNumber(connection?.Get("voltage_v")),
                ReserveW = reserve is null ? null : new PhaseValues
                {
                    L1 = ConfigTree.AsNumber(reserve.Get("L1")),
                    L2 = ConfigTree.AsNumber(reserve.Get("L2")),
                    L3 = ConfigTree.AsNumber(reserve.Get("L3")),
                },
            },
        };

        var slots = new SignalIndex(document.GetMapping(Signals));
        foreach (var other in slots.Unowned)
        {
            preserved.Add(new PreservedSection($"{Signals}.{other.Key}", $"signal {other.Key} ({ConfigTree.AsText((other.Value as ConfigMapping)?.Get("entity")) ?? "no entity"})"));
        }

        MeterPhaseDraft? Row(Phase phase)
        {
            var current = slots.Find("phase_current", phase);
            var power = slots.Find("phase_power", phase);
            var voltage = slots.Find("phase_voltage", phase);
            return current is null && power is null && voltage is null ? null : new MeterPhaseDraft
            {
                Current = SignalOf(current?.Mapping),
                Power = SignalOf(power?.Mapping),
                Voltage = SignalOf(voltage?.Mapping),
            };
        }

        var firstCurrent = AllPhases.Select(p => slots.Find("phase_current", p)).FirstOrDefault(s => s is not null) ?? slots.Find("total_current", null);
        var firstPower = AllPhases.Select(p => slots.Find("phase_power", p)).FirstOrDefault(s => s is not null) ?? slots.Find("total_power", null);
        var currentSign = ConfigTree.AsText(firstCurrent?.Mapping.Get("sign"));
        var direction = ConfigTree.AsText(firstCurrent?.Mapping.Get("direction_from"));
        var inverter = slots.Find("inverter_power", null, anyPhase: true);
        var inverterCurrents = AllPhases.Select(p => slots.Find("inverter_phase_current", p)).ToArray();
        draft = draft with
        {
            Meter = new MeterDraft
            {
                CurrentSign = currentSign,
                CurrentDirection = currentSign != Unsigned ? null : direction == SwitchboardDocumentParser.NoDirection ? SwitchboardDocumentParser.NoDirection : direction is null ? null : PhasePowerDirection,
                PowerSign = ConfigTree.AsText(firstPower?.Mapping.Get("sign")),
                Phases = new MeterPhases { L1 = Row(Phase.L1), L2 = Row(Phase.L2), L3 = Row(Phase.L3) },
                Total = new MeterTotal
                {
                    Power = SignalOf(slots.Find("total_power", null)?.Mapping),
                    Current = SignalOf(slots.Find("total_current", null)?.Mapping),
                },
            },
            Sources = new SourcesDraft
            {
                Inverter = inverter is null && inverterCurrents.All(c => c is null) ? null : new InverterDraft
                {
                    Power = SignalOf(inverter?.Mapping),
                    Sign = ConfigTree.AsText(inverter?.Mapping.Get("sign")),
                    UnavailableAsZero = ConfigTree.AsBoolean(inverter?.Mapping.Get("unavailable_as_zero")) ?? false,
                    Phase = ConfigTree.AsText(inverter?.Mapping.Get("phase")),
                    PhaseCurrents = inverterCurrents.All(c => c is null) ? null : new InverterPhaseCurrents
                    {
                        L1 = SignalOf(inverterCurrents[0]?.Mapping),
                        L2 = SignalOf(inverterCurrents[1]?.Mapping),
                        L3 = SignalOf(inverterCurrents[2]?.Mapping),
                    },
                },
            },
            Devices = document.Get(Devices) is ConfigSequence sequence ? [.. sequence.Items.Select(ReadDevice)] : [],
        };

        return new SetupLoad(draft, preserved.ToImmutable());
    }

    /// <summary>
    /// Applies <paramref name="draft"/> to the parsed document (or to an empty one). Existing and
    /// editable are decided here from the document by id; the draft's Existing flag is only used to
    /// refuse a new device that reuses the id of one in the file.
    /// </summary>
    /// <param name="defaultStart">The start policy a device without <c>start:</c> gets under this document (devices.default_start): a draft value equal to it is not written unless the file had the key.</param>
    public static MappedDocument Apply(ConfigNode? root, SetupDraft draft, string? defaultStart = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var fields = new FieldMap();
        var issues = ImmutableArray.CreateBuilder<SetupIssue>();
        var document = root as ConfigMapping ?? ConfigTree.EmptyMapping();
        var preserved = Read(root).Preserved;

        for (var i = 0; i < draft.DropPreserved.Count; i++)
        {
            var path = draft.DropPreserved[i];
            if (!preserved.Any(p => p.Path == path))
            {
                issues.Add(SetupIssue.Error(SetupIssueCodes.PreservedPathUnknown, $"dropPreserved[{i}]", $"'{path}' is not a preserved part of the file.", ("path", path)));
                continue;
            }

            document = path.StartsWith(Signals + ".", StringComparison.Ordinal)
                ? document.Set(Signals, (document.GetMapping(Signals) ?? ConfigTree.EmptyMapping()).Remove(path[(Signals.Length + 1)..]))
                : document.Remove(path);
        }

        if (ConfigTree.AsNumber(document.Get("schema_version")) != SwitchboardDocumentParser.SchemaVersion)
        {
            document = document.Set("schema_version", ConfigTree.Number(SwitchboardDocumentParser.SchemaVersion));
        }

        // Only the phases of the chosen topology are written: rows the wizard no longer shows (L2 and
        // L3 after switching to single_phase) are removed, never refused at a field the user cannot see.
        var phases = TopologyPhasesOf(draft.Connection.Topology);
        document = document.Set(Connection, ApplyConnection(document.GetMapping(Connection) ?? ConfigTree.EmptyMapping(), draft.Connection, phases, fields));
        document = document.Set(Signals, ApplySignals(document.GetMapping(Signals) ?? ConfigTree.EmptyMapping(), draft, phases, fields));
        var devices = ApplyDevices(document.Get(Devices) as ConfigSequence, draft.Devices, fields, issues, defaultStart);
        document = devices.Items.IsEmpty ? document.Remove(Devices) : document.Set(Devices, devices);
        fields.Add(Devices, Devices);
        return new MappedDocument(document, fields, issues.ToImmutable());
    }

    /// <summary>
    /// The sigma each signal of a document declares, normalized to A, W or V with its unit, as the
    /// runtime parser normalizes it; a signal without sigma (or with a unit the parser refuses) is left out.
    /// </summary>
    public static ImmutableDictionary<EntityId, double> DeclaredSigma(ConfigNode? root)
    {
        var sigma = ImmutableDictionary.CreateBuilder<EntityId, double>();
        foreach (var entry in (root as ConfigMapping)?.GetMapping(Signals)?.Entries ?? [])
        {
            if (entry.Value is ConfigMapping signal
                && EntityId.TryParse(ConfigTree.AsText(signal.Get("entity")), out var entity)
                && ConfigTree.AsNumber(signal.Get("sigma")) is { } raw and >= 0
                && ConfigTree.AsText(signal.Get("unit")) is { } unit
                && Enum.GetValues<SignalKind>().Any(k => SignalUnits.AllowedFor(k).Contains(unit)))
            {
                sigma[entity.Value] = raw * SignalUnits.Factor(unit);
            }
        }

        return sigma.ToImmutable();
    }

    /// <summary>The phases a topology has; every phase while the topology is not (yet) a known one.</summary>
    public static IReadOnlyList<Phase> TopologyPhasesOf(string? topology) => topology switch
    {
        "single_phase" => TopologyPhases.For(Topology.SinglePhase),
        "three_phase" or "three_phase_summed" => TopologyPhases.For(Topology.ThreePhase),
        _ => AllPhases,
    };

    private static string Summary(string key, ConfigNode value) => (key, value) switch
    {
        ("control", ConfigMapping m) => $"control-parameter overrides ({string.Join(", ", m.Entries.Select(e => e.Key))})",
        ("circuit_groups", ConfigSequence s) => $"{s.Items.Length} circuit group(s): {string.Join(", ", s.Items.OfType<ConfigMapping>().Select(g => ConfigTree.AsText(g.Get("id")) ?? "?"))}",
        _ => $"key '{key}'",
    };

    private static SignalDraft? SignalOf(ConfigMapping? signal) => signal is null ? null : new SignalDraft
    {
        Entity = ConfigTree.AsText(signal.Get("entity")),
        Unit = ConfigTree.AsText(signal.Get("unit")),
        MaxAge = ConfigTree.AsText(signal.Get("max_age")),
    };

    private static DeviceDraft ReadDevice(ConfigNode node)
    {
        if (node is not ConfigMapping device)
        {
            return new DeviceDraft { Existing = true, Editable = false };
        }

        var draw = device.Get("draw") as ConfigMapping;
        return new DeviceDraft
        {
            Id = ConfigTree.AsText(device.Get("id")),
            Existing = true,
            Editable = IsEditable(device),
            Name = ConfigTree.AsText(device.Get("name")),
            Type = ConfigTree.AsText(device.Get("type")),
            Actuator = ConfigTree.AsText(device.Get("actuator")),
            StateEntity = ConfigTree.AsText(device.Get("state_entity")),
            StateReports = ConfigTree.AsText(device.Get("state_reports")),
            Phase = ConfigTree.AsText(device.Get("phase")),
            RatedCurrentA = ConfigTree.AsNumber(device.Get("rated_current_a")),
            RatedPowerW = ConfigTree.AsNumber(device.Get("rated_power_w")),
            Min = ConfigTree.AsNumber(device.Get("min")),
            Max = ConfigTree.AsNumber(device.Get("max")),
            Step = ConfigTree.AsNumber(device.Get("step")),
            ReactionTime = ConfigTree.AsText(device.Get("reaction_time")),
            MinOn = ConfigTree.AsText(device.Get("min_on")),
            MinOff = ConfigTree.AsText(device.Get("min_off")),
            ValuePerKwh = ConfigTree.AsNumber(device.Get("value_per_kwh")),
            CircuitGroup = ConfigTree.AsText(device.Get("circuit_group")),
            RotationGroup = ConfigTree.AsText(device.Get("rotation_group")),
            StopActuator = ConfigTree.AsText(device.Get("stop_actuator")),
            StopOption = ConfigTree.AsText(device.Get("stop_option")),
            ResumeOption = ConfigTree.AsText(device.Get("resume_option")),
            Start = ConfigTree.AsText(device.Get("start")),
            Draw = draw is null ? null : new DrawDraft
            {
                Current = ConfigTree.AsText(draw.Get("current")),
                Power = ConfigTree.AsText(draw.Get("power")),
                MaxAge = ConfigTree.AsText(draw.Get("max_age")),
                CurrentUnit = ConfigTree.AsText(draw.Get("current_unit")),
                PowerUnit = ConfigTree.AsText(draw.Get("power_unit")),
            },
        };
    }

    /// <summary>
    /// What the wizard can edit: an onoff, modulating or storage device with a scalar phase and, if
    /// any, a single-phase draw. A stepped device or a per-phase draw is kept as written.
    /// </summary>
    private static bool IsEditable(ConfigMapping device) =>
        EditableTypes.Contains(ConfigTree.AsText(device.Get("type")))
        && device.Get("phase") is null or ConfigScalar
        && device.Get("id") is ConfigScalar { IsNull: false }
        && (device.Get("draw") is null || device.Get("draw") is ConfigMapping draw
            && draw.Get("current") is null or ConfigScalar && draw.Get("power") is null or ConfigScalar);

    private static ConfigMapping ApplyConnection(ConfigMapping connection, ConnectionDraft draft, IReadOnlyList<Phase> phases, FieldMap fields)
    {
        fields.Add(Connection, Connection);
        foreach (var (key, field) in new[] { ("topology", "topology"), ("breaker_rating_a", "breakerRatingA"), ("characteristic", "characteristic"), ("voltage_v", "voltageV"), ("reserve_w", "reserveW") })
        {
            fields.Add($"{Connection}.{key}", $"{Connection}.{field}");
        }

        connection = connection
            .SetPlain("topology", draft.Topology)
            .SetNumber("breaker_rating_a", draft.BreakerRatingA)
            .SetPlain("characteristic", draft.Characteristic)
            .SetNumber("voltage_v", draft.VoltageV);

        var reserve = connection.GetMapping("reserve_w") ?? ConfigTree.EmptyMapping();
        foreach (var phase in AllPhases)
        {
            fields.Add($"{Connection}.reserve_w.{phase}", $"{Connection}.reserveW.{phase}");
            reserve = reserve.SetNumber(phase.ToString(), !phases.Contains(phase) ? null : phase switch
            {
                Phase.L1 => draft.ReserveW?.L1,
                Phase.L2 => draft.ReserveW?.L2,
                _ => draft.ReserveW?.L3,
            });
        }

        return reserve.Entries.IsEmpty ? connection.Remove("reserve_w") : connection.Set("reserve_w", reserve);
    }

    private static ConfigMapping ApplySignals(ConfigMapping signals, SetupDraft draft, IReadOnlyList<Phase> phases, FieldMap fields)
    {
        var index = new SignalIndex(signals);
        var meter = draft.Meter;
        var writer = new SignalWriter(signals, index, fields);

        // A phase outside the topology writes nothing, which removes its owned slot.
        MeterPhaseDraft? ShownRow(Phase phase) => phases.Contains(phase) ? Row(meter.Phases, phase) : null;

        // Powers first: an unsigned current names the power of its phase as its direction.
        var powerNames = new Dictionary<Phase, string?>();
        foreach (var phase in AllPhases)
        {
            var row = ShownRow(phase);
            var field = $"meter.phases.{phase}";
            powerNames[phase] = writer.Write(new Slot("phase_power", phase, row?.Power, $"{field}.power", $"grid_power_{Lower(phase)}", "power")
            {
                Sign = meter.PowerSign,
                SignField = "meter.powerSign",
            });
            writer.Write(new Slot("phase_voltage", phase, row?.Voltage, $"{field}.voltage", $"grid_voltage_{Lower(phase)}", "voltage"));
        }

        var totalPower = writer.Write(new Slot("total_power", null, meter.Total.Power, "meter.total.power", "grid_total_power", "power")
        {
            Sign = meter.PowerSign,
            SignField = "meter.powerSign",
        });

        foreach (var phase in AllPhases)
        {
            writer.Write(new Slot("phase_current", phase, ShownRow(phase)?.Current, $"meter.phases.{phase}.current", $"grid_current_{Lower(phase)}", "current")
            {
                Sign = meter.CurrentSign,
                SignField = "meter.currentSign",
                Direction = CurrentDirection(meter, powerNames[phase]),
                DirectionField = "meter.currentDirection",
            });
        }

        writer.Write(new Slot("total_current", null, meter.Total.Current, "meter.total.current", "grid_total_current", "current")
        {
            Sign = meter.CurrentSign,
            SignField = "meter.currentSign",
            Direction = CurrentDirection(meter, totalPower),
            DirectionField = "meter.currentDirection",
        });

        var inverter = draft.Sources.Inverter;
        var pvName = writer.Write(new Slot("inverter_power", null, inverter?.Power, "sources.inverter.power", "pv_power", "power")
        {
            Sign = inverter?.Sign,
            SignField = "sources.inverter.sign",
            UnavailableAsZero = inverter?.UnavailableAsZero,
            InverterPhase = inverter?.Phase,
            AnyPhase = true,
        });
        foreach (var phase in AllPhases)
        {
            var current = !phases.Contains(phase) ? null : phase switch
            {
                Phase.L1 => inverter?.PhaseCurrents?.L1,
                Phase.L2 => inverter?.PhaseCurrents?.L2,
                _ => inverter?.PhaseCurrents?.L3,
            };
            writer.Write(new Slot("inverter_phase_current", phase, current, $"sources.inverter.phaseCurrents.{phase}", $"pv_current_{Lower(phase)}", "current")
            {
                // The inverter only produces: its current is a magnitude signed by the inverter power,
                // unless the file already signs it itself.
                Sign = Unsigned,
                KeepSignedSign = true,
                SignField = "sources.inverter",
                Direction = pvName,
                DirectionField = "sources.inverter",
            });
        }

        return writer.Result;
    }

    private static string? CurrentDirection(MeterDraft meter, string? powerName) =>
        meter.CurrentSign != Unsigned ? null
        : meter.CurrentDirection == SwitchboardDocumentParser.NoDirection ? SwitchboardDocumentParser.NoDirection
        : meter.CurrentDirection == PhasePowerDirection ? powerName
        : null;

    private static MeterPhaseDraft? Row(MeterPhases phases, Phase phase) => phase switch
    {
        Phase.L1 => phases.L1,
        Phase.L2 => phases.L2,
        _ => phases.L3,
    };

    private static string Lower(Phase phase) => phase.ToString().ToLowerInvariant();

    private static ConfigSequence ApplyDevices(ConfigSequence? existing, IReadOnlyList<DeviceDraft> drafts, FieldMap fields, ImmutableArray<SetupIssue>.Builder issues, string? defaultStart)
    {
        var byId = new Dictionary<string, ConfigMapping>(StringComparer.Ordinal);
        foreach (var item in existing?.Items ?? [])
        {
            if (item is ConfigMapping device && ConfigTree.AsText(device.Get("id")) is { } id)
            {
                byId.TryAdd(id, device);
            }
        }

        var items = new List<ConfigNode>(drafts.Count);
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            var path = $"{Devices}[{i.ToString(CultureInfo.InvariantCulture)}]";
            fields.Add(path, path);
            foreach (var (key, field) in DeviceFields)
            {
                fields.Add($"{path}.{key}", $"{path}.{field}");
            }

            foreach (var key in new[] { "current", "power", "max_age" })
            {
                fields.Add($"{path}.draw.{key}", $"{path}.draw.{(key == "max_age" ? "maxAge" : key)}");
            }

            // A unit is taken from the picked entity: its issues land on that entity's field.
            fields.Add($"{path}.draw.current_unit", $"{path}.draw.current");
            fields.Add($"{path}.draw.power_unit", $"{path}.draw.power");

            if (draft.Id is { } id && byId.TryGetValue(id, out var base_))
            {
                if (!draft.Existing)
                {
                    // A device added in this edit reusing the id of one removed from the file would be
                    // merged into the removed entry and inherit its keys (a stop actuator, a shed
                    // priority). The client's flag is only ever used to refuse.
                    issues.Add(SetupIssue.Error(
                        SetupIssueCodes.DeviceIdTaken,
                        $"{path}.id",
                        $"The id '{id}' is already used by a device in the file (removed in this edit or not); choose another id for the new device.",
                        ("device", id)));
                    items.Add(Patch(ConfigTree.EmptyMapping().SetText("id", id).SetPlain("type", draft.Type), draft, draft.Type, defaultStart));
                    continue;
                }

                if (!IsEditable(base_))
                {
                    // Kept as written: only its presence is the draft's to decide.
                    items.Add(base_);
                    continue;
                }

                var type = ConfigTree.AsText(base_.Get("type"))!;
                if (draft.Type is { } requested && requested != type)
                {
                    issues.Add(SetupIssue.Error(
                        SetupIssueCodes.ExistingDeviceImmutable,
                        $"{path}.type",
                        $"The type of the existing device '{id}' stays {type}; remove it and add a new device to change the type.",
                        ("device", id),
                        ("type", type)));
                }

                items.Add(Patch(base_, draft, type, defaultStart));
                continue;
            }

            var created = ConfigTree.EmptyMapping();
            if (draft.Id is not null)
            {
                created = created.SetText("id", draft.Id);
            }

            created = created.SetText("name", draft.Name);
            if (draft.Type is not null)
            {
                created = created.SetPlain("type", draft.Type);
            }

            items.Add(Patch(created, draft, draft.Type, defaultStart));
        }

        return ConfigTree.Sequence(items);
    }

    /// <summary>The wizard's keys of one device; keys valid only for another type are removed (they are owned too).</summary>
    private static ConfigMapping Patch(ConfigMapping device, DeviceDraft draft, string? type, string? defaultStart)
    {
        var relay = type == "onoff";
        var setpoint = type is "modulating" or "storage";

        // start: the default needs no key (a later default still applies); a key the file had stays
        // written, whatever its value, so the wizard never silently changes what the user wrote.
        var start = draft.Start is { } wanted && (device.Get("start") is not null || wanted != defaultStart) ? wanted : null;

        // The options belong to a select stop only (a switch stop is turned off and on); an option
        // the file wrote as 1 stays 1, a new one is written quoted so it reads back as text.
        var selectStop = type == "modulating" && IsSelectStop(draft.StopActuator);
        device = device
            .SetText("name", draft.Name)
            .SetText("actuator", draft.Actuator)
            .SetText("state_entity", draft.StateEntity)
            .SetPlain("phase", draft.Phase)
            .SetNumber("rated_current_a", relay ? draft.RatedCurrentA : null)
            .SetNumber("rated_power_w", relay ? draft.RatedPowerW : null)
            .SetNumber("min", setpoint ? draft.Min : null)
            .SetNumber("max", setpoint ? draft.Max : null)
            .SetNumber("step", setpoint ? draft.Step : null)
            .SetNumber("value_per_kwh", draft.ValuePerKwh)
            .SetPlain("state_reports", draft.StateReports)
            .SetPlain("reaction_time", draft.ReactionTime)
            .SetPlain("min_on", draft.MinOn)
            .SetPlain("min_off", draft.MinOff)
            .SetText("rotation_group", relay ? draft.RotationGroup : null)
            .SetText("circuit_group", draft.CircuitGroup)
            .SetText("stop_actuator", type == "modulating" ? draft.StopActuator : null)
            .SetText("stop_option", selectStop ? draft.StopOption : null)
            .SetText("resume_option", selectStop ? draft.ResumeOption : null)
            .SetPlain("start", start);

        // state_max_age belongs to an age-judged state; next to on_change the parser refuses it, at a
        // key the wizard would otherwise not own.
        if (draft.StateReports == "on_change")
        {
            device = device.Remove("state_max_age");
        }

        if (draft.Draw is not { } drawDraft || (drawDraft.Current is null && drawDraft.Power is null))
        {
            return device.Remove("draw");
        }

        // The unit belongs to the picked entity: written when it is not the parser's default, removed
        // otherwise, so a kW sensor is never read as W (and a W sensor replacing it never as kW).
        var draw = (device.Get("draw") as ConfigMapping ?? ConfigTree.EmptyMapping())
            .SetText("current", drawDraft.Current)
            .SetText("power", drawDraft.Power)
            .SetPlain("max_age", drawDraft.MaxAge)
            .SetPlain("current_unit", drawDraft.Current is null || drawDraft.CurrentUnit == DeviceSectionParser.DefaultUnit ? null : drawDraft.CurrentUnit)
            .SetPlain("power_unit", drawDraft.Power is null || drawDraft.PowerUnit == DeviceSectionParser.DefaultPowerUnit ? null : drawDraft.PowerUnit);
        return device.Set("draw", draw);
    }

    /// <summary>Whether a stop actuator is a select (or input_select), whose stop and resume options the wizard writes.</summary>
    public static bool IsSelectStop(string? stopActuator) =>
        EntityId.TryParse(stopActuator, out var entity) && DeviceSectionParser.SelectStopDomains.Contains(entity.Value.Value[..entity.Value.Value.IndexOf('.', StringComparison.Ordinal)]);

    /// <summary>One signal slot the wizard owns: a role, on a phase or not, and the draft value for it.</summary>
    private sealed record Slot(string Role, Phase? Phase, SignalDraft? Value, string Field, string DefaultName, string Kind)
    {
        public string? Sign { get; init; }

        public string? SignField { get; init; }

        /// <summary>Keep a positive_import / positive_export already written instead of <see cref="Sign"/>.</summary>
        public bool KeepSignedSign { get; init; }

        public string? Direction { get; init; }

        public string? DirectionField { get; init; }

        public bool? UnavailableAsZero { get; init; }

        public string? InverterPhase { get; init; }

        /// <summary>Found by role alone, whatever phase it declares (one inverter per installation).</summary>
        public bool AnyPhase { get; init; }
    }

    /// <summary>The document's signals by role and phase, as text: works on a document that fails the schema too.</summary>
    private sealed class SignalIndex(ConfigMapping? signals)
    {
        private static readonly string[] OwnedRoles = ["phase_current", "phase_power", "phase_voltage", "total_current", "total_power", "inverter_power", "inverter_phase_current"];

        private readonly List<(string Key, ConfigMapping Mapping, string? Role, string? Phase)> _entries =
        [
            .. (signals?.Entries ?? []).Select(e => (e.Key, e.Value as ConfigMapping ?? ConfigTree.EmptyMapping(),
                ConfigTree.AsText((e.Value as ConfigMapping)?.Get("role")), ConfigTree.AsText((e.Value as ConfigMapping)?.Get("phase")))),
        ];

        private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

        /// <summary>Signals no owned slot takes: display-only ones, and duplicates of an owned slot.</summary>
        public IEnumerable<ConfigEntry> Unowned
        {
            get
            {
                var first = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in signals?.Entries ?? [])
                {
                    var (key, _, role, phase) = _entries.First(e => e.Key == entry.Key);
                    var slot = role == "inverter_power" ? role : $"{role}/{phase}";
                    if (role is null || !OwnedRoles.Contains(role) || !first.Add(slot))
                    {
                        yield return entry;
                    }
                }
            }
        }

        public (string Key, ConfigMapping Mapping)? Find(string role, Phase? phase, bool anyPhase = false)
        {
            foreach (var entry in _entries)
            {
                if (entry.Role == role && (anyPhase || entry.Phase == phase?.ToString()) && !_claimed.Contains(entry.Key))
                {
                    return (entry.Key, entry.Mapping);
                }
            }

            return null;
        }

        public void Claim(string key) => _claimed.Add(key);

        public bool NameTaken(string name) => _entries.Any(e => e.Key == name);
    }

    private sealed class SignalWriter(ConfigMapping signals, SignalIndex index, FieldMap fields)
    {
        public ConfigMapping Result { get; private set; } = signals;

        /// <summary>Writes (or removes) one slot; returns the name of the signal now holding it.</summary>
        public string? Write(Slot slot)
        {
            var existing = index.Find(slot.Role, slot.Phase, slot.AnyPhase);
            if (existing is { } found)
            {
                index.Claim(found.Key);
            }

            if (slot.Value is not { Entity: not null } value)
            {
                if (existing is { } gone)
                {
                    Result = Result.Remove(gone.Key);
                }

                return null;
            }

            var name = existing?.Key ?? UniqueName(slot.DefaultName);
            var signal = existing?.Mapping ?? ConfigTree.EmptyMapping();
            signal = signal
                .SetText("entity", value.Entity)
                .SetPlain("kind", slot.Kind)
                .SetPlain("unit", value.Unit);

            if (slot.Kind is "current" or "power")
            {
                var written = ConfigTree.AsText(signal.Get("sign"));
                var sign = slot.KeepSignedSign && written is "positive_import" or "positive_export" ? written : slot.Sign;
                signal = signal.SetPlain("sign", sign);
                signal = sign == Unsigned ? signal.SetPlain("direction_from", slot.Direction) : signal.Remove("direction_from");
            }

            signal = signal.SetPlain("max_age", value.MaxAge).SetPlain("role", slot.Role);
            signal = slot.Role == "inverter_power"
                ? signal.SetPlain("phase", slot.InverterPhase).SetBoolean("unavailable_as_zero", slot.UnavailableAsZero == true ? true : null)
                : signal.SetPlain("phase", slot.Phase?.ToString());

            Result = Result.Set(name, signal);
            var path = $"{Signals}.{name}";
            fields.Add(path, slot.Field);
            foreach (var key in new[] { "kind", "role", "sigma" })
            {
                fields.Add($"{path}.{key}", slot.Field);
            }

            fields.Add($"{path}.entity", $"{slot.Field}.entity");
            fields.Add($"{path}.unit", $"{slot.Field}.unit");
            fields.Add($"{path}.max_age", $"{slot.Field}.maxAge");
            fields.Add($"{path}.sign", slot.SignField ?? slot.Field);
            fields.Add($"{path}.direction_from", slot.DirectionField ?? slot.Field);
            fields.Add($"{path}.phase", slot.Role == "inverter_power" ? "sources.inverter.phase" : slot.Field);
            fields.Add($"{path}.unavailable_as_zero", "sources.inverter.unavailableAsZero");
            return name;
        }

        private string UniqueName(string name)
        {
            var candidate = name;
            for (var n = 2; index.NameTaken(candidate) || Result.Find(candidate) is not null; n++)
            {
                candidate = string.Create(CultureInfo.InvariantCulture, $"{name}_{n}");
            }

            return candidate;
        }
    }
}
