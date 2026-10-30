using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Domain.Ev;
using static Switchboard.Infrastructure.Persistence.PresentationJson;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/ev.json</c>: what the EV module remembers across a restart (schema 1, docs/EV.md section 3 point 6): the manual mode, a running
/// "charge now", the learned kWh/km with its samples, and the last trip with its frozen target. Deliberately not part of state.json: a schema bump there
/// makes the previous version treat that file as untrusted and engage the latched kill switch. Untrusted on read: another schema, a key missing or
/// repeated, an unknown mode, a non-finite or implausible number, or a file above <see cref="MaxFileBytes"/> sets it aside as <c>ev.json.corrupt</c>
/// and the module starts from its defaults (a warning, never the kill switch). A file of a <i>newer</i> schema is not damaged: it is moved to
/// <c>ev.json.schema&lt;N&gt;</c> (never over an existing copy) so a rollback does not destroy what the newer version learned. Written atomically; the
/// writer produces only what the reader accepts (values it would refuse are dropped or clamped first), so the module never sets aside its own file.
/// </summary>
public class EvStateFile(string dataDirectory, ILogger<EvStateFile> logger)
    : PresentationFile<EvState>(dataDirectory, Name, MaxFileBytes, logger)
{
    public const string Name = "ev.json";
    public const int SchemaVersion = 1;

    // The worst honest document is 200 samples of about 80 bytes plus a trip key of 200 characters (6 bytes each if every one is escaped): under 18 KiB.
    internal const int MaxFileBytes = 32 * 1024;

    // What the file holds, whatever the learner is asked to keep (configuration cannot ask for more: EvLearnParameters.MaxSamplesLimit).
    internal const int MaxSamplesOnRead = EvLearnParameters.MaxSamplesLimit;
    internal const int MaxTripKeyLength = 200;

    // Sanity of untrusted numbers: configuration cannot allow a consumption above this (EvLearnParameters.MaxKwhPerKmLimit).
    internal const double MaxSaneKwhPerKm = EvLearnParameters.MaxKwhPerKmLimit;

    private static readonly string[] RootKeys = ["schema_version", "mode", "charge_now", "kwh_per_km", "last_trip"];

    // Optional, written only when set: a schema 1 file without it stays valid (EV S5), so no schema bump.
    private static readonly string[] RootKeysWithFull = [.. RootKeys, "last_full_at"];
    private static readonly string[] ChargeNowKeys = ["started_at", "target_percent", "expires_at"];
    private static readonly string[] LearnedKeys = ["smoothed", "samples"];
    private static readonly string[] SampleKeys = ["at", "value"];
    private static readonly string[] TripKeys = ["trip_key", "target_percent", "trip_soc_percent", "exceeds_electric_range", "clamped", "frozen_at"];

    protected override string LostText => "the EV module starts from its defaults (mode auto, nothing learned yet)";

    protected override byte[] Serialize(EvState state)
    {
        state = Sanitize(state);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("mode", Snake(state.Mode));
            if (state.ChargeNow is { } now)
            {
                writer.WriteStartObject("charge_now");
                WriteTime(writer, "started_at", now.StartedAt);
                writer.WriteNumber("target_percent", now.TargetPercent);
                WriteTime(writer, "expires_at", now.ExpiresAt);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("charge_now");
            }

            writer.WriteStartObject("kwh_per_km");
            if (state.Learned.Smoothed is { } smoothed)
            {
                writer.WriteNumber("smoothed", smoothed);
            }
            else
            {
                writer.WriteNull("smoothed");
            }

            writer.WriteStartArray("samples");
                        foreach (var sample in state.Learned.Samples)
            {
                writer.WriteStartObject();
                WriteTime(writer, "at", sample.At);
                writer.WriteNumber("value", sample.KwhPerKm);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            if (state.LastTrip is { } trip)
            {
                writer.WriteStartObject("last_trip");
                writer.WriteString("trip_key", trip.TripKey);
                writer.WriteNumber("target_percent", trip.TargetPercent);
                writer.WriteNumber("trip_soc_percent", trip.TripSocPercent);
                writer.WriteBoolean("exceeds_electric_range", trip.ExceedsElectricRange);
                writer.WriteBoolean("clamped", trip.Clamped);
                WriteTime(writer, "frozen_at", trip.FrozenAt);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("last_trip");
            }

            if (state.LastFullAt is { } full)
            {
                WriteTime(writer, "last_full_at", full);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    protected override EvState Parse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("schema_version", out var declared)
            && declared.ValueKind == JsonValueKind.Number && declared.TryGetInt32(out var found) && found > SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version {found} is newer than this version understands ({SchemaVersion})", $".schema{found}");
        }

        var keys = Keys(root, "the document", root.ValueKind == JsonValueKind.Object && root.TryGetProperty("last_full_at", out _) ? RootKeysWithFull : RootKeys);
        if (Count(keys["schema_version"], "schema_version") != SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version is not {SchemaVersion}");
        }

        return new EvState(
            Choice<EvMode>(keys["mode"], "mode"),
            keys["charge_now"].ValueKind == JsonValueKind.Null ? null : ChargeNow(keys["charge_now"]),
            Learned(keys["kwh_per_km"]),
            keys["last_trip"].ValueKind == JsonValueKind.Null ? null : Trip(keys["last_trip"]),
            keys.TryGetValue("last_full_at", out var full) ? Time(full, "last_full_at") : null);
    }

    /// <summary>Nothing useful to drop: Sanitize already bounds the document to far below the cap, and a test holds the worst case under it.</summary>
    protected override EvState? Trim(EvState state, double ratio) => null;

    /// <summary>
    /// What the reader accepts, made of any state: a value the reader would refuse is dropped (a smoothed value outside the range takes the samples with it,
    /// as the reader wants one for the other) or clamped, the oldest samples beyond the file's limit go, a trip key is cut to its limit.
    /// </summary>
    internal static EvState Sanitize(EvState state)
    {
        var samples = state.Learned.Samples.IsDefault ? [] : state.Learned.Samples.Where(s => IsConsumption(s.KwhPerKm)).ToImmutableArray();
        if (samples.Length > MaxSamplesOnRead)
        {
            samples = samples.RemoveRange(0, samples.Length - MaxSamplesOnRead);
        }

        var smoothed = state.Learned.Smoothed is { } value && IsConsumption(value) ? value : (double?)null;
        var learned = smoothed is null ? KwhPerKmState.Empty : new KwhPerKmState(smoothed, samples);

        var now = state.ChargeNow;
        EvChargeNow? chargeNow = now is not null && double.IsFinite(now.TargetPercent) && now.ExpiresAt > now.StartedAt
            ? now with { TargetPercent = Math.Clamp(now.TargetPercent, 0, 100) }
            : null;

        var trip = state.LastTrip;
        EvLastTrip? lastTrip = trip is not null && !string.IsNullOrEmpty(trip.TripKey) && double.IsFinite(trip.TargetPercent) && double.IsFinite(trip.TripSocPercent)
            ? trip with
            {
                TripKey = trip.TripKey.Length > MaxTripKeyLength ? CutKey(trip.TripKey) : trip.TripKey,
                TargetPercent = Math.Clamp(trip.TargetPercent, 0, 100),
                TripSocPercent = Math.Max(0, trip.TripSocPercent),
            }
            : null;

        return new EvState(System.Enum.IsDefined(state.Mode) ? state.Mode : EvMode.Auto, chargeNow, learned, lastTrip, state.LastFullAt);
    }

    private static bool IsConsumption(double value) => double.IsFinite(value) && value > 0 && value <= MaxSaneKwhPerKm;

    // Not in the middle of a surrogate pair.
    private static string CutKey(string key) => char.IsHighSurrogate(key[MaxTripKeyLength - 1]) ? key[..(MaxTripKeyLength - 1)] : key[..MaxTripKeyLength];

    private static EvChargeNow ChargeNow(JsonElement element)
    {
        var keys = Keys(element, "charge_now", ChargeNowKeys);
        var started = Time(keys["started_at"], "charge_now.started_at")!.Value;
        var expires = Time(keys["expires_at"], "charge_now.expires_at")!.Value;
        var target = Percent(keys["target_percent"], "charge_now.target_percent");
        return expires > started ? new EvChargeNow(started, target, expires) : throw new InvalidPresentationFileException("charge_now ends before it starts");
    }

    private static KwhPerKmState Learned(JsonElement element)
    {
        var keys = Keys(element, "kwh_per_km", LearnedKeys);
        double? smoothed = keys["smoothed"].ValueKind == JsonValueKind.Null ? null : Consumption(keys["smoothed"], "kwh_per_km.smoothed");
        var items = Array(keys["samples"], "kwh_per_km.samples", MaxSamplesOnRead);
        var samples = ImmutableArray.CreateBuilder<KwhPerKmSample>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var sample = Keys(items[i], $"kwh_per_km.samples[{i}]", SampleKeys);
            samples.Add(new KwhPerKmSample(
                Time(sample["at"], $"kwh_per_km.samples[{i}].at")!.Value,
                Consumption(sample["value"], $"kwh_per_km.samples[{i}].value")));
        }

        if (smoothed is null && samples.Count > 0)
        {
            throw new InvalidPresentationFileException("kwh_per_km has samples but no smoothed value");
        }

        return new KwhPerKmState(smoothed, samples.ToImmutable());
    }

    private static EvLastTrip Trip(JsonElement element)
    {
        var keys = Keys(element, "last_trip", TripKeys);
        return new EvLastTrip(
            Text(keys["trip_key"], "last_trip.trip_key", MaxTripKeyLength),
            Percent(keys["target_percent"], "last_trip.target_percent"),
            NonNegative(keys["trip_soc_percent"], "last_trip.trip_soc_percent"),
            Bool(keys["exceeds_electric_range"], "last_trip.exceeds_electric_range"),
            Bool(keys["clamped"], "last_trip.clamped"),
            Time(keys["frozen_at"], "last_trip.frozen_at")!.Value);
    }

    private static double Percent(JsonElement element, string path) =>
        Number(element, path) is var value && value is >= 0 and <= 100 ? value : throw new InvalidPresentationFileException($"{path} is not a percentage");

    private static double Consumption(JsonElement element, string path) =>
        Number(element, path) is var value && value > 0 && value <= MaxSaneKwhPerKm
            ? value
            : throw new InvalidPresentationFileException($"{path} is not a consumption any car has");
}
