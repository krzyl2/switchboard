using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using static Switchboard.Infrastructure.Persistence.PresentationJson;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/excursions.json</c>: per phase the last and the worst excursion for the Limits chart
/// (schema 1). Untrusted on read: exact keys and types, closed lists for actions and reasons, at
/// most ExcursionMaxSamples samples and ExcursionMaxDecisions decisions each, samples in time
/// order, and at most MaxFileBytes; anything else sets the file aside. A downgrade to 0.4.x ignores it.
/// </summary>
public class ExcursionFile : PresentationFile<ExcursionState>
{
    public const string Name = "excursions.json";
    public const int SchemaVersion = 1;

    private const int MaxIdLength = 64;

    // Thinning stops here: a start and a peak still make a chart.
    private const int MinSamples = 2;

    // A trimmed file aims a little below the cap, so one pass usually suffices.
    private const double TrimMargin = 0.95;

    private static readonly string[] RootKeys = ["schema_version", "saved_at", "phases"];
    private static readonly string[] PhaseKeys = ["last", "worst"];
    private static readonly string[] ExcursionKeys =
        ["start", "end", "peak_a", "peak_at", "reserve_threshold_a", "rating_a", "continuous_a", "decimated", "samples", "decisions"];

    private static readonly string[] DecisionKeys = ["id", "at", "action", "reason", "device", "commanded"];

    private readonly StatisticsOptions _options;

    public ExcursionFile(string dataDirectory, StatisticsOptions options, ILogger<ExcursionFile> logger)
        : base(dataDirectory, Name, (options ?? throw new ArgumentNullException(nameof(options))).MaxFileBytes, logger)
    {
        _options = options;
    }

    protected override byte[] Serialize(ExcursionState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            WriteTime(writer, "saved_at", state.SavedAt);
            writer.WriteStartObject("phases");
            foreach (var phase in state.Phases.OrderBy(p => p.Phase))
            {
                writer.WriteStartObject(phase.Phase.ToString());
                Write(writer, "last", phase.Last);
                Write(writer, "worst", phase.Worst);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Thins every excursion's samples to about <paramref name="ratio"/> (keeping the peak and the decision instants), or null when none can shrink further.</summary>
    protected override ExcursionState? Trim(ExcursionState state, double ratio)
    {
        var shrunk = false;
        Excursion? Thin(Excursion? excursion)
        {
            if (excursion is null || excursion.Samples.Length <= MinSamples)
            {
                return excursion;
            }

            shrunk = true;
            var target = Math.Clamp((int)(excursion.Samples.Length * ratio * TrimMargin), MinSamples, excursion.Samples.Length - 1);
            var kept = ExcursionRecorder.Decimate([.. excursion.Samples], [.. excursion.Decisions.Select(d => d.At)], target);
            return excursion with { Samples = [.. kept], Decimated = true };
        }

        var phases = state.Phases.Select(p => p with { Last = Thin(p.Last), Worst = Thin(p.Worst) }).ToImmutableArray();
        return shrunk ? state with { Phases = phases } : null;
    }

    protected override ExcursionState Parse(JsonElement root)
    {
        var keys = Keys(root, "the document", RootKeys);
        if (keys["schema_version"].ValueKind != JsonValueKind.Number || !keys["schema_version"].TryGetInt32(out var version) || version != SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version is not {SchemaVersion}");
        }

        var phases = ImmutableArray.CreateBuilder<PhaseExcursions>();
        foreach (var property in Object(keys["phases"], "phases", Enum.GetValues<Phase>().Length))
        {
            if (!Enum.TryParse<Phase>(property.Name, ignoreCase: false, out var phase) || !Enum.IsDefined(phase) || property.Name != phase.ToString())
            {
                throw new InvalidPresentationFileException($"phases.{property.Name} is not a phase");
            }

            var entry = Keys(property.Value, $"phases.{property.Name}", PhaseKeys);
            phases.Add(new PhaseExcursions(
                phase,
                Read(entry["last"], $"phases.{property.Name}.last", phase),
                Read(entry["worst"], $"phases.{property.Name}.worst", phase)));
        }

        return new ExcursionState(Time(keys["saved_at"], "saved_at")!.Value, phases.ToImmutable());
    }

    private static void Write(Utf8JsonWriter writer, string name, Excursion? excursion)
    {
        if (excursion is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString("start", Time(excursion.Start));
        WriteTime(writer, "end", excursion.End);
        writer.WriteNumber("peak_a", excursion.Peak.Value);
        writer.WriteString("peak_at", Time(excursion.PeakAt));
        writer.WriteNumber("reserve_threshold_a", excursion.ReserveThreshold.Value);
        writer.WriteNumber("rating_a", excursion.Rating.Value);
        writer.WriteNumber("continuous_a", excursion.Continuous.Value);
        writer.WriteBoolean("decimated", excursion.Decimated);
        writer.WriteStartArray("samples");
        foreach (var sample in excursion.Samples)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(Time(sample.At));
            writer.WriteNumberValue(sample.Current.Value);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("decisions");
        foreach (var decision in excursion.Decisions)
        {
            writer.WriteStartObject();
            writer.WriteString("id", decision.Id);
            writer.WriteString("at", Time(decision.At));
            writer.WriteString("action", Snake(decision.Action));
            writer.WriteString("reason", ReasonCodes.Name(decision.Reason));
            if (decision.Device is { } device)
            {
                writer.WriteString("device", device.Value);
            }
            else
            {
                writer.WriteNull("device");
            }

            writer.WriteBoolean("commanded", decision.Commanded);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private Excursion? Read(JsonElement element, string path, Phase phase)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var keys = Keys(element, path, ExcursionKeys);
        var start = Time(keys["start"], $"{path}.start")!.Value;
        var end = Time(keys["end"], $"{path}.end", nullable: true);
        if (end < start)
        {
            throw new InvalidPresentationFileException($"{path}.end lies before its start");
        }

        var positive = (string key) => Number(keys[key], $"{path}.{key}") is var value && value > 0
            ? new Amperes(value)
            : throw new InvalidPresentationFileException($"{path}.{key} is not a positive current");
        var samples = ImmutableArray.CreateBuilder<ExcursionSample>();
        DateTimeOffset? previous = null;
        var items = Array(keys["samples"], $"{path}.samples", _options.ExcursionMaxSamples);
        for (var i = 0; i < items.Count; i++)
        {
            var pair = Array(items[i], $"{path}.samples[{i}]", 2);
            if (pair.Count != 2)
            {
                throw new InvalidPresentationFileException($"{path}.samples[{i}] is not a [time, current] pair");
            }

            var at = Time(pair[0], $"{path}.samples[{i}][0]")!.Value;
            if (previous is { } before && at < before)
            {
                throw new InvalidPresentationFileException($"{path}.samples[{i}] is out of time order");
            }

            previous = at;
            samples.Add(new ExcursionSample(at, new Amperes(Number(pair[1], $"{path}.samples[{i}][1]"))));
        }

        var decisions = ImmutableArray.CreateBuilder<ExcursionDecision>();
        var decisionItems = Array(keys["decisions"], $"{path}.decisions", _options.ExcursionMaxDecisions);
        for (var i = 0; i < decisionItems.Count; i++)
        {
            var decisionPath = $"{path}.decisions[{i}]";
            var entry = Keys(decisionItems[i], decisionPath, DecisionKeys);
            var reasonText = entry["reason"].ValueKind == JsonValueKind.String ? entry["reason"].GetString() : null;
            var reason = ReasonCodes.All.Where(c => ReasonCodes.Name(c) == reasonText).Select(c => (ReasonCode?)c).SingleOrDefault()
                ?? throw new InvalidPresentationFileException($"{decisionPath}.reason is not a reason code of list version {ReasonCodes.ListVersion}");
            DeviceName? device = null;
            if (entry["device"].ValueKind != JsonValueKind.Null && !DeviceName.TryParse(entry["device"].ValueKind == JsonValueKind.String ? entry["device"].GetString() : null, out device))
            {
                throw new InvalidPresentationFileException($"{decisionPath}.device is not a device name");
            }

            decisions.Add(new ExcursionDecision(
                Text(entry["id"], $"{decisionPath}.id", MaxIdLength),
                Time(entry["at"], $"{decisionPath}.at")!.Value,
                Choice<DecisionAction>(entry["action"], $"{decisionPath}.action"),
                reason,
                device,
                Bool(entry["commanded"], $"{decisionPath}.commanded")));
        }

        return new Excursion(
            phase,
            start,
            end,
            new Amperes(NonNegative(keys["peak_a"], $"{path}.peak_a")),
            Time(keys["peak_at"], $"{path}.peak_at")!.Value,
            new Amperes(NonNegative(keys["reserve_threshold_a"], $"{path}.reserve_threshold_a")),
            positive("rating_a"),
            positive("continuous_a"),
            Bool(keys["decimated"], $"{path}.decimated"),
            samples.ToImmutable(),
            decisions.ToImmutable());
    }
}
