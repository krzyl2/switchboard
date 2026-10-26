using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Balance;
using Switchboard.Domain.Balance;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/balance-baseline.json</c>: the counters at the start of the running local hour, so the
/// hourly balance survives a restart inside the hour (BAL-01). Read as untrusted input (exact keys,
/// finite non-negative numbers); a corrupt file is set aside as <c>balance-baseline.json.corrupt</c>
/// and the balance starts unknown. Written atomically.
/// </summary>
public sealed class BalanceBaselineFile(string dataDirectory, ILogger<BalanceBaselineFile> logger)
    : PresentationFile<HourBaseline>(dataDirectory, Name, MaxFileBytes, logger), IBalanceBaselineFile
{
    public const string Name = "balance-baseline.json";
    private const int SchemaVersion = 1;

    // A bound on untrusted content, far above the few dozen bytes the file holds.
    private const int MaxFileBytes = 4096;

    protected override string LostText => "the hourly balance starts unknown until the next full hour";

    HourBaseline? IBalanceBaselineFile.Load() => Load().State;

    void IBalanceBaselineFile.Save(HourBaseline baseline) => Save(baseline);

    protected override byte[] Serialize(HourBaseline state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            PresentationJson.WriteTime(writer, "hour_start", state.HourStart);
            writer.WriteNumber("export_kwh", state.ExportKwh);
            writer.WriteNumber("import_kwh", state.ImportKwh);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    protected override HourBaseline Parse(JsonElement root)
    {
        var keys = PresentationJson.Keys(root, "the document", "schema_version", "hour_start", "export_kwh", "import_kwh");
        if (PresentationJson.Count(keys["schema_version"], "schema_version") != SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version is not {SchemaVersion}");
        }

        return new HourBaseline(
            PresentationJson.Time(keys["hour_start"], "hour_start")!.Value,
            PresentationJson.NonNegative(keys["export_kwh"], "export_kwh"),
            PresentationJson.NonNegative(keys["import_kwh"], "import_kwh"));
    }

    /// <summary>Nothing to drop: the file is one record.</summary>
    protected override HourBaseline? Trim(HourBaseline state, double ratio) => null;
}
