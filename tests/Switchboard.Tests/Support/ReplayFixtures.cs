using System.Globalization;
using System.Text;
using Switchboard.Infrastructure.Replay;

namespace Switchboard.Tests.Support;

/// <summary>
/// Recorded and synthetic replay data. The live recording is the maintainer's household
/// consumption and is gitignored; tests that need it fail loudly, naming the file, when it is absent
/// (they are never skipped silently).
/// </summary>
internal static class ReplayFixtures
{
    public const string SyntheticFile = "synthetic-three-phase-import-export.csv";

    private static readonly Lazy<ReplayData> Synthetic = new(() => ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", SyntheticFile)));

    private static readonly Lazy<ReplayData?> Live = new(LoadLive);

    public static ReplayData SyntheticData => Synthetic.Value;

    public static ReplayData LiveData
    {
        get
        {
            if (Live.Value is { } data)
            {
                return data;
            }

            Assert.Fail(
                "The recorded live fixture tests/fixtures/replay/live-*.csv.gz is missing. It is gitignored (the maintainer's "
                + "household data). Place it there, or run the suite with --filter Category!=LiveReplay to leave these tests out.");
            return null!;
        }
    }

    /// <summary>
    /// The recording with one row of <paramref name="entity"/> at its first instant: an entity the
    /// recording predates (it was not mapped then) is assumed to have held <paramref name="state"/>
    /// throughout, as Home Assistant reports an unchanged state only once.
    /// </summary>
    public static ReplayData WithAssumedState(ReplayData data, string entity, string state)
    {
        ArgumentNullException.ThrowIfNull(data);
        var csv = new StringBuilder("entity_id,ts_utc,state,last_reported\n");
        void Row(string id, DateTimeOffset at, string value, DateTimeOffset? reported) =>
            csv.Append(CultureInfo.InvariantCulture, $"{id},{at.UtcDateTime:O},\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\",{(reported is { } r ? r.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : string.Empty)}\n");

        Row(entity, data.Start, state, null);
        foreach (var row in data.Rows)
        {
            Row(row.Entity.Value, row.Timestamp, row.State, row.LastReported);
        }

        using var reader = new StringReader(csv.ToString());
        return ReplayData.Parse(reader, $"{entity} assumed {state}");
    }

    private static ReplayData? LoadLive()
    {
        var directory = RepoPaths.Combine("tests", "fixtures", "replay");
        var file = Directory.Exists(directory) ? Directory.GetFiles(directory, "live-*.csv.gz").Order().LastOrDefault() : null;

        // The recording (2026-10-21 to 24) predates the go-e force-state select the example declares
        // as the charger's stop (0.7.1): it is assumed neutral ("0") throughout, as it was.
        return file is null ? null : WithAssumedState(ReplayData.Load(file), "select.goe_422752_frc", "0");
    }
}
