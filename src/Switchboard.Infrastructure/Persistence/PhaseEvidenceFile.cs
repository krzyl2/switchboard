using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Options;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/phase-evidence.json</c>: the self-test's raw phase observations (passive and active), so
/// evidence survives a restart (maintainer survey 2026-10-25). Read as untrusted input like the
/// other data files (exact keys and types, bounded per entity and in size); a corrupt file is set
/// aside as <c>phase-evidence.json.corrupt</c> and the store starts empty. Written atomically.
/// </summary>
public sealed class PhaseEvidenceFile : PresentationFile<PhaseEvidenceState>, IPhaseEvidenceFile
{
    public const string Name = "phase-evidence.json";
    private const int SchemaVersion = 1;

    // Bounds on untrusted content, far above anything a configuration produces.
    private const int MaxEntities = 256;
    private const int MaxNote = 64;

    private readonly int _perEntity;

    public PhaseEvidenceFile(string dataDirectory, SetupOptions options, ILogger<PhaseEvidenceFile> logger)
        : base(dataDirectory, Name, options?.EvidenceMaxFileBytes ?? throw new ArgumentNullException(nameof(options)), logger)
    {
        _perEntity = options.EvidencePerDevice;
    }

    protected override string LostText => "the self-test started without its earlier evidence";

    PhaseEvidenceLoad IPhaseEvidenceFile.Load()
    {
        var loaded = Load();
        return new PhaseEvidenceLoad(loaded.State, loaded.Status, loaded.Warning);
    }

    protected override byte[] Serialize(PhaseEvidenceState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            PresentationJson.WriteTime(writer, "saved_at", state.SavedAt);
            writer.WriteStartObject("entities");
            foreach (var (entity, observations) in state.Observations.OrderBy(o => o.Key.Value, StringComparer.Ordinal))
            {
                writer.WriteStartArray(entity.Value);
                foreach (var observation in observations)
                {
                    WriteObservation(writer, observation);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    protected override PhaseEvidenceState Parse(JsonElement root)
    {
        var keys = PresentationJson.Keys(root, "the document", "schema_version", "saved_at", "entities");
        if (PresentationJson.Count(keys["schema_version"], "schema_version") != SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version is not {SchemaVersion}");
        }

        var observations = ImmutableDictionary.CreateBuilder<EntityId, ImmutableArray<PhaseObservation>>();
        foreach (var property in PresentationJson.Object(keys["entities"], "entities", MaxEntities))
        {
            if (!EntityId.TryParse(property.Name, out var entity))
            {
                throw new InvalidPresentationFileException($"entities.{property.Name} is not an entity ID");
            }

            var list = PresentationJson.Array(property.Value, $"entities.{property.Name}", _perEntity)
                .Select((o, i) => ReadObservation(o, $"entities.{property.Name}[{i}]"))
                .ToImmutableArray();
            if (list.Any(o => o.StateEntity != entity.Value))
            {
                throw new InvalidPresentationFileException($"entities.{property.Name} holds an observation of another entity");
            }

            observations.Add(entity.Value, list);
        }

        return new PhaseEvidenceState(PresentationJson.Time(keys["saved_at"], "saved_at")!.Value, observations.ToImmutable());
    }

    /// <summary>The newest observations of each entity, about <paramref name="ratio"/> of them; null when every entity is down to one.</summary>
    protected override PhaseEvidenceState? Trim(PhaseEvidenceState state, double ratio)
    {
        if (state.Observations.Values.All(o => o.Length <= 1))
        {
            return null;
        }

        return state with
        {
            Observations = state.Observations.ToImmutableDictionary(
                o => o.Key,
                o => o.Value.Length <= 1 ? o.Value : [.. o.Value.Skip(o.Value.Length - Math.Max(1, (int)Math.Floor(o.Value.Length * ratio)))]),
        };
    }

    private static void WriteObservation(Utf8JsonWriter writer, PhaseObservation observation)
    {
        writer.WriteStartObject();
        PresentationJson.WriteTime(writer, "at", observation.At);
        writer.WriteString("direction", observation.Direction > 0 ? "on" : "off");
        writer.WriteString("state_entity", observation.StateEntity.Value);
        writer.WriteString("source", PresentationJson.Snake(observation.Source));
        writer.WriteString("unit", observation.Unit);
        writer.WriteBoolean("unsigned", observation.Unsigned);
        writer.WriteBoolean("house_load", observation.HouseLoad);
        writer.WriteStartObject("meter");
        foreach (var (phase, term) in observation.Meter.OrderBy(m => m.Key))
        {
            writer.WriteStartObject(phase.ToString());
            writer.WriteString("entity", term.Entity.Value);
            writer.WriteString("unit", term.Unit);
            writer.WriteString("sign", term.Sign);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteStartObject("phases");
        foreach (var (phase, measurement) in observation.Phases.OrderBy(m => m.Key))
        {
            writer.WriteStartObject(phase.ToString());
            Optional(writer, "previous", measurement.Previous);
            Optional(writer, "before", measurement.Before);
            Optional(writer, "after", measurement.After);
            writer.WriteNumber("sigma", measurement.Sigma);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteBoolean("isolated", observation.Isolated);
        if (observation.Note is { } note)
        {
            writer.WriteString("note", note);
        }
        else
        {
            writer.WriteNull("note");
        }

        writer.WriteEndObject();
    }

    private static PhaseObservation ReadObservation(JsonElement element, string path)
    {
        var keys = PresentationJson.Keys(element, path, "at", "direction", "state_entity", "source", "unit", "unsigned", "house_load", "meter", "phases", "isolated", "note");
        var direction = keys["direction"].ValueKind == JsonValueKind.String ? keys["direction"].GetString() : null;
        if (direction is not ("on" or "off"))
        {
            throw new InvalidPresentationFileException($"{path}.direction is neither on nor off");
        }

        if (!EntityId.TryParse(PresentationJson.Text(keys["state_entity"], $"{path}.state_entity", 255), out var state))
        {
            throw new InvalidPresentationFileException($"{path}.state_entity is not an entity ID");
        }

        var unit = PresentationJson.Text(keys["unit"], $"{path}.unit", 8);
        if (unit is not ("W" or "A"))
        {
            throw new InvalidPresentationFileException($"{path}.unit is neither W nor A");
        }

        var meter = ImmutableDictionary.CreateBuilder<Phase, MeterTerm>();
        foreach (var property in PresentationJson.Object(keys["meter"], $"{path}.meter", 3))
        {
            var term = PresentationJson.Keys(property.Value, $"{path}.meter.{property.Name}", "entity", "unit", "sign");
            if (!EntityId.TryParse(PresentationJson.Text(term["entity"], $"{path}.meter.{property.Name}.entity", 255), out var entity))
            {
                throw new InvalidPresentationFileException($"{path}.meter.{property.Name}.entity is not an entity ID");
            }

            var sign = PresentationJson.Text(term["sign"], $"{path}.meter.{property.Name}.sign", 32);
            if (sign is not ("positive_import" or "positive_export" or "unsigned"))
            {
                throw new InvalidPresentationFileException($"{path}.meter.{property.Name}.sign is not a sign convention");
            }

            var termUnit = term["unit"].ValueKind == JsonValueKind.String ? term["unit"].GetString()! : throw new InvalidPresentationFileException($"{path}.meter.{property.Name}.unit is not a text");
            meter.Add(PhaseOf(property.Name, path), new MeterTerm(entity.Value, termUnit, sign));
        }

        var phases = ImmutableDictionary.CreateBuilder<Phase, PhaseStepMeasurement>();
        foreach (var property in PresentationJson.Object(keys["phases"], $"{path}.phases", 3))
        {
            var p = PresentationJson.Keys(property.Value, $"{path}.phases.{property.Name}", "previous", "before", "after", "sigma");
            double? Value(string key) => p[key].ValueKind == JsonValueKind.Null ? null : PresentationJson.Number(p[key], $"{path}.phases.{property.Name}.{key}");
            phases.Add(PhaseOf(property.Name, path), new PhaseStepMeasurement(Value("previous"), Value("before"), Value("after"), PresentationJson.NonNegative(p["sigma"], $"{path}.phases.{property.Name}.sigma")));
        }

        return new PhaseObservation(
            PresentationJson.Time(keys["at"], $"{path}.at")!.Value,
            direction == "on" ? 1 : -1,
            state.Value,
            PresentationJson.Choice<ObservationSource>(keys["source"], $"{path}.source"),
            unit,
            PresentationJson.Bool(keys["unsigned"], $"{path}.unsigned"),
            PresentationJson.Bool(keys["house_load"], $"{path}.house_load"),
            meter.ToImmutable(),
            phases.ToImmutable(),
            PresentationJson.Bool(keys["isolated"], $"{path}.isolated"),
            keys["note"].ValueKind == JsonValueKind.Null ? null : PresentationJson.Text(keys["note"], $"{path}.note", MaxNote));
    }

    private static Phase PhaseOf(string name, string path) =>
        Enum.TryParse<Phase>(name, ignoreCase: false, out var phase) && Enum.IsDefined(phase) && phase.ToString() == name
            ? phase
            : throw new InvalidPresentationFileException($"{path}: '{name}' is not a phase");

    private static void Optional(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
