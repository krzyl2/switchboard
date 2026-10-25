using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Decisions;
using Switchboard.Application.Options;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// The decision log's retained history (MODE-04), read from the daily JSONL files the log writes:
/// newest first, filtered, paged. The cursor is <c>yyyyMMdd:line</c>, the position in a day file
/// where the next (older) page continues; lines are only ever appended, so a position never moves
/// and a page boundary has no duplicates or gaps while today's file grows. Exactly the files the
/// log's retention keeps are read (DecisionRetention); corrupt and partial lines are skipped; a search reads at most
/// HistoryMaxScanLines lines per request and then answers a partial page with its cursor.
/// </summary>
public sealed class JsonlDecisionHistory : IDecisionHistory
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string CursorDateFormat = "yyyyMMdd";

    private readonly IDecisionFileSystem _files;
    private readonly string _directory;
    private readonly PersistenceOptions _options;
    private readonly Func<int> _retentionDays;
    private readonly TimeProvider _time;

    /// <param name="retentionDays">retention_days of the configuration in force (the shipped default without one).</param>
    public JsonlDecisionHistory(IDecisionFileSystem files, string dataDirectory, PersistenceOptions options, Func<int> retentionDays, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        _files = files;
        _directory = Path.Combine(dataDirectory, JsonlDecisionLog.DirectoryName);
        _options = options;
        _retentionDays = retentionDays;
        _time = time;
    }

    public DecisionHistoryPage Query(DecisionHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = query.Cursor is null ? ((DateOnly Date, int Line)?)null : ParseCursor(query.Cursor);
        var retention = _retentionDays();
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        // The writer's own keep rule: a file the log still keeps is never hidden (a long downtime).
        var files = DayFiles();
        var kept = DecisionRetention.Kept(files, today, retention);
        var days = files.Where(d => kept.Contains(d.Name)).OrderByDescending(d => d.Date).ToList();
        var retainedFrom = days.Count == 0 ? (DateOnly?)null : days[^1].Date;
        var fromDate = query.From is { } from ? DateOnly.FromDateTime(from.UtcDateTime) : (DateOnly?)null;
        var toDate = query.To is { } to ? DateOnly.FromDateTime(to.UtcDateTime) : (DateOnly?)null;

        var items = ImmutableArray.CreateBuilder<Decision>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long scanned = 0;
        for (var d = 0; d < days.Count; d++)
        {
            var day = days[d];
            if ((cursor is { } c && day.Date > c.Date) || (fromDate is { } f && day.Date < f) || (toDate is { } t && day.Date > t))
            {
                continue;
            }

            var path = Path.Combine(_directory, day.Name);
            if (_files.Length(path) > _options.HistoryMaxFileBytes)
            {
                continue;
            }

            var lines = _files.ReadLines(path).ToList();
            var start = cursor is { } position && position.Date == day.Date ? Math.Min(position.Line, lines.Count) : lines.Count;
            for (var i = start - 1; i >= 0; i--)
            {
                if (scanned >= _options.HistoryMaxScanLines)
                {
                    return new DecisionHistoryPage(items.ToImmutable(), Cursor(day.Date, i + 1), retainedFrom, retention, scanned, true);
                }

                scanned++;
                if (DecisionJson.TryParse(lines[i]) is not { } decision || !Matches(decision, query) || !seen.Add(decision.Id))
                {
                    continue;
                }

                items.Add(decision);
                if (items.Count == query.Limit)
                {
                    var more = i > 0 || days.Skip(d + 1).Any(o => fromDate is not { } floor || o.Date >= floor);
                    return new DecisionHistoryPage(items.ToImmutable(), more ? Cursor(day.Date, i) : null, retainedFrom, retention, scanned, false);
                }
            }
        }

        return new DecisionHistoryPage(items.ToImmutable(), null, retainedFrom, retention, scanned, false);
    }

    private static bool Matches(Decision decision, DecisionHistoryQuery query) =>
        (query.From is not { } from || decision.At >= from)
        && (query.To is not { } to || decision.At < to)
        && (query.Device is null || decision.Device == query.Device)
        && (query.Phase is null || decision.Phase == query.Phase)
        && (query.Reason is null || decision.Reason == query.Reason)
        && (query.Kind is null || DecisionKinds.Of(decision) == query.Kind);

    private static string Cursor(DateOnly date, int line) =>
        string.Create(CultureInfo.InvariantCulture, $"{date.ToString(CursorDateFormat, CultureInfo.InvariantCulture)}:{line}");

    private static (DateOnly Date, int Line) ParseCursor(string cursor)
    {
        var parts = cursor.Split(':');
        return parts.Length == 2
            && DateOnly.TryParseExact(parts[0], CursorDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var line)
                ? (date, line)
                : throw new ArgumentException("The cursor is not one GET api/decisions/history issued; start again without it.", nameof(cursor));
    }

    private List<(string Name, DateOnly Date)> DayFiles() =>
        [.. _files.List(_directory)
            .Where(name => name.EndsWith(JsonlDecisionLog.Extension, StringComparison.Ordinal))
            .Select(name => DateOnly.TryParseExact(name[..^JsonlDecisionLog.Extension.Length], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? (Name: name, Date: date, Valid: true)
                : (Name: name, Date: default, Valid: false))
            .Where(f => f.Valid)
            .Select(f => (f.Name, f.Date))];
}
