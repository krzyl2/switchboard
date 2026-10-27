using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Domain.Planning;
using static Switchboard.Infrastructure.Persistence.PresentationJson;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/load-profile.json</c>: the hour-of-week load profile of the planner (schema 1, 15-PLANNER). Untrusted on read: another schema, a key
/// missing or repeated, other than 168 bins, a non-finite, negative or implausible number, a bin whose samples, weight, mean and time disagree, or a
/// file above <see cref="MaxFileBytes"/> sets it aside as <c>load-profile.json.corrupt</c> and the forecast starts from the fallback (and is seeded again
/// from the retained statistics). Written atomically. The size is fixed by the 168 bins, so there is nothing to trim.
/// </summary>
public class LoadProfileFile(string dataDirectory, ILogger<LoadProfileFile> logger)
    : PresentationFile<LoadProfile>(dataDirectory, Name, MaxFileBytes, logger)
{
    public const string Name = "load-profile.json";
    public const int SchemaVersion = 1;

    // 168 bins of about 110 bytes is 19 KB: far above that is not this file.
    internal const int MaxFileBytes = 64 * 1024;

    // Bounds on untrusted content: no household draws a megawatt, and a weight is a count of weekly samples decayed by half-life.
    internal const double MaxMeanKw = 1_000;
    internal const double MaxWeight = 1e9;

    private static readonly string[] RootKeys = ["schema_version", "fed_through", "bins"];
    private static readonly string[] BinKeys = ["mean_kw", "weight", "samples", "last_at"];

    protected override string LostText => "the load forecast starts from its fallback and is seeded again from the retained statistics";

    protected override byte[] Serialize(LoadProfile state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            WriteTime(writer, "fed_through", state.FedThrough);
            writer.WriteStartArray("bins");
            foreach (var bin in state.Bins)
            {
                writer.WriteStartObject();
                writer.WriteNumber("mean_kw", bin.MeanKw);
                writer.WriteNumber("weight", bin.Weight);
                writer.WriteNumber("samples", bin.Samples);
                WriteTime(writer, "last_at", bin.LastAt);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    protected override LoadProfile Parse(JsonElement root)
    {
        var keys = Keys(root, "the document", RootKeys);
        if (Count(keys["schema_version"], "schema_version") != SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version is not {SchemaVersion}");
        }

        var fedThrough = Time(keys["fed_through"], "fed_through", nullable: true);
        var items = Array(keys["bins"], "bins", LoadProfile.BinCount);
        if (items.Count != LoadProfile.BinCount)
        {
            throw new InvalidPresentationFileException($"bins has {items.Count} entries, not {LoadProfile.BinCount}");
        }

        var bins = ImmutableArray.CreateBuilder<LoadBin>(LoadProfile.BinCount);
        for (var i = 0; i < items.Count; i++)
        {
            bins.Add(Bin(items[i], $"bins[{i}]", fedThrough));
        }

        return new LoadProfile(bins.MoveToImmutable(), fedThrough);
    }

    private static LoadBin Bin(JsonElement element, string path, DateTimeOffset? fedThrough)
    {
        var keys = Keys(element, path, BinKeys);
        var mean = NonNegative(keys["mean_kw"], $"{path}.mean_kw");
        var weight = NonNegative(keys["weight"], $"{path}.weight");
        var samples = Count(keys["samples"], $"{path}.samples");
        var lastAt = Time(keys["last_at"], $"{path}.last_at", nullable: true);
        if (mean > MaxMeanKw || weight > MaxWeight)
        {
            throw new InvalidPresentationFileException($"{path} holds a load or weight no installation produces");
        }

        // A bin is empty in all four fields or filled in all four: a half-filled one is not something the writer produces.
        if (samples == 0 != (weight == 0) || samples == 0 != (lastAt is null) || (samples == 0 && mean != 0))
        {
            throw new InvalidPresentationFileException($"{path} disagrees with itself about being filled");
        }

        if (lastAt is { } last && fedThrough is { } fed && last >= fed)
        {
            throw new InvalidPresentationFileException($"{path}.last_at lies in an hour that was not fed yet");
        }

        return new LoadBin(mean, weight, samples, lastAt);
    }

    /// <summary>Nothing to drop: the file has a fixed number of bins.</summary>
    protected override LoadProfile? Trim(LoadProfile state, double ratio) => null;
}
