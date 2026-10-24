using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.Replay;

/// <summary>
/// One recorded state. History records changes only, so between two rows of an entity its value
/// simply repeated. <see cref="LastReported"/> (optional fourth column) declares that the entity
/// stopped reporting at that instant until its next row: how a fixture models a frozen sensor.
/// </summary>
public sealed record ReplayRow(EntityId Entity, DateTimeOffset Timestamp, string State, DateTimeOffset? LastReported);

/// <summary>Recorded Home Assistant history (<c>entity_id,ts_utc,state[,last_reported]</c>, plain or gzip), in time order.</summary>
public sealed class ReplayData
{
    private static readonly string[] Header = ["entity_id", "ts_utc", "state"];
    private const string LastReportedColumn = "last_reported";

    private ReplayData(ImmutableArray<ReplayRow> rows)
    {
        Rows = rows;
    }

    public ImmutableArray<ReplayRow> Rows { get; }

    public DateTimeOffset Start => Rows[0].Timestamp;

    public DateTimeOffset End => Rows[^1].Timestamp;

    public static ReplayData Load(string path)
    {
        using var file = File.OpenRead(path);
        var gzip = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        using Stream stream = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader, path);
    }

    public static ReplayData Parse(TextReader reader, string source)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var header = Fields(reader.ReadLine() ?? throw new FormatException($"{source} is empty."));
        var withLastReported = header.Count == 4 && header[3] == LastReportedColumn;
        if (header.Count < 3 || !header.Take(3).SequenceEqual(Header) || (header.Count == 4 && !withLastReported) || header.Count > 4)
        {
            throw new FormatException($"{source}: the header must be entity_id,ts_utc,state[,last_reported].");
        }

        var rows = new List<ReplayRow>();
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            var fields = Fields(line);
            if (fields.Count != header.Count || !EntityId.TryParse(fields[0], out var entity) || Time(fields[1]) is not { } timestamp)
            {
                throw new FormatException($"{source}:{lineNumber}: expected {header.Count} fields with a valid entity ID and an ISO 8601 time.");
            }

            DateTimeOffset? lastReported = null;
            if (withLastReported && fields[3].Length > 0)
            {
                lastReported = Time(fields[3]) ?? throw new FormatException($"{source}:{lineNumber}: last_reported is not an ISO 8601 time.");
            }

            rows.Add(new ReplayRow(entity.Value, timestamp, fields[2], lastReported));
        }

        if (rows.Count == 0)
        {
            throw new FormatException($"{source} has no rows.");
        }

        // Recorder exports are grouped by entity; replay needs global time order (stable for equal times).
        return new ReplayData([.. rows.OrderBy(r => r.Timestamp)]);
    }

    private static DateTimeOffset? Time(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    /// <summary>Comma-separated fields; double quotes allow commas and doubled quotes inside a field.</summary>
    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
