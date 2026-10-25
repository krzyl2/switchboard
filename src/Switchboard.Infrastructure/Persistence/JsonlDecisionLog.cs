using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Options;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// The decision log (MODE-03): <c>/data/decisions/YYYY-MM-DD.jsonl</c>, one file per UTC date of
/// the decision, one decision per line, plus the recent decisions in memory for the API.
/// <para>
/// <see cref="Record"/> runs on the guard's thread and only touches memory: the decision joins the
/// recent list and a bounded write queue, and the writer task (<see cref="RunAsync"/>) appends it.
/// A failed write (disk full, read-only) marks the log unwritable, is logged once, and is retried
/// every WriteRetryInterval; while it lasts the queue keeps at most DecisionPendingCapacity
/// decisions, dropping the oldest, and the next successful write records the gap first. Control
/// never waits for any of it.
/// </para>
/// <para>
/// A crash can leave a partial last line: at start a missing final line feed is added, and after a
/// failed write the next one starts on a new line, so a partial line never swallows a decision.
/// Readers skip anything that is not a decision. Retention runs at start and once per UTC date: a
/// file is deleted only when it is older than retention_days and also not among the newest
/// retention_days files, so a clock jumping forward cannot wipe the log.
/// </para>
/// </summary>
public sealed class JsonlDecisionLog : IDecisionRecorder
{
    public const string DirectoryName = "decisions";
    public const string Extension = ".jsonl";

    private const string DateFormat = "yyyy-MM-dd";

    private readonly IDecisionFileSystem _files;
    private readonly string _directory;
    private readonly PersistenceOptions _options;
    private readonly Func<int?> _retentionDays;
    private readonly TimeProvider _time;
    private readonly ILogger<JsonlDecisionLog> _logger;
    private readonly string _instance = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly List<Entry> _recent = [];
    private readonly LinkedList<Decision> _pending = new();
    private long _sequence;
    private long _dropped;
    private long _gapDropped;
    private DateTimeOffset _gapFirst;
    private DateTimeOffset _gapLast;
    private bool _writable = true;
    private string? _lastError;
    private bool _needsNewline;
    private DateOnly? _retentionRanFor;

    /// <param name="dataDirectory">The data folder; the log lives in its <c>decisions</c> subfolder.</param>
    /// <param name="retentionDays">retention_days of the configuration in force; null (no valid configuration yet) deletes nothing.</param>
    public JsonlDecisionLog(
        IDecisionFileSystem files,
        string dataDirectory,
        PersistenceOptions options,
        Func<int?> retentionDays,
        TimeProvider time,
        ILogger<JsonlDecisionLog> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);
        _files = files;
        _directory = Path.Combine(dataDirectory, DirectoryName);
        _options = options;
        _retentionDays = retentionDays;
        _time = time;
        _logger = logger;
    }

    public string DirectoryPath => _directory;

    public DecisionLogStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new DecisionLogStatus(_writable, _lastError, _dropped);
            }
        }
    }

    /// <summary>The file a decision taken at <paramref name="at"/> belongs to: its UTC date.</summary>
    public static string FileNameFor(DateTimeOffset at) => at.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension;

    /// <summary>
    /// At start, before the guard runs: isolates a partial last line, reloads the recent decisions
    /// from the newest files and applies retention. A failure here marks the log unwritable; it
    /// never stops the process.
    /// </summary>
    public void Start()
    {
        try
        {
            _files.CreateDirectory(_directory);
            var days = DayFiles();
            if (days.Count > 0 && !_files.EndsWithNewline(Path.Combine(_directory, days[^1].Name)))
            {
                _files.Append(Path.Combine(_directory, days[^1].Name), "\n");
            }

            Reload(days);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failed(ex);
        }

        RunRetentionIfDue();
    }

    public void Record(IReadOnlyList<Decision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var decision in decisions)
            {
                Remember(decision);
                _pending.AddLast(decision);
            }

            TrimPending();
        }

        _wake.Writer.TryWrite(true);
    }

    public DecisionPage Query(DecisionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var before = query.Before is null ? long.MaxValue : ParseCursor(query.Before);
        lock (_gate)
        {
            var matches = new List<Entry>(query.Limit + 1);
            for (var i = _recent.Count - 1; i >= 0 && matches.Count <= query.Limit; i--)
            {
                var entry = _recent[i];
                var decision = entry.Decision;
                if (entry.Sequence < before
                    && (query.Device is null || decision.Device == query.Device)
                    && (query.Phase is null || decision.Phase == query.Phase)
                    && (query.Reason is null || decision.Reason == query.Reason)
                    && (query.Kind is null || DecisionKinds.Of(decision) == query.Kind))
                {
                    matches.Add(entry);
                }
            }

            var more = matches.Count > query.Limit;
            var page = matches.Take(query.Limit).ToList();
            return new DecisionPage(
                [.. page.Select(e => e.Decision)],
                more ? Cursor(page[^1].Sequence) : null,
                _writable);
        }
    }

    /// <summary>The writer task: appends queued decisions; after a failure, retries on its own clock.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                bool writable;
                lock (_gate)
                {
                    writable = _writable;
                }

                if (writable)
                {
                    await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(_options.WriteRetryInterval, _time, cancellationToken).ConfigureAwait(false);
                }

                await WritePendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping; the shutdown decisions are written by FlushAsync.
        }
    }

    /// <summary>Writes everything queued now, once; used at shutdown after the shutdown safe state.</summary>
    public Task FlushAsync(CancellationToken cancellationToken) => WritePendingAsync(cancellationToken);

    private async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WritePending();
            RunRetentionIfDue();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void WritePending()
    {
        List<Decision> batch;
        long gap;
        DateTimeOffset gapFirst, gapLast;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            (gap, gapFirst, gapLast) = (_gapDropped, _gapFirst, _gapLast);
        }

        if (batch.Count == 0 && gap == 0)
        {
            return;
        }

        var written = 0;
        try
        {
            _files.CreateDirectory(_directory);
            if (gap > 0)
            {
                Append(FileNameFor(gapLast), DecisionJson.Gap(gap, gapFirst, gapLast) + "\n");
                lock (_gate)
                {
                    _gapDropped -= gap;
                }

                _logger.LogWarning("{Count} decisions were lost while the decision log could not be written ({First:O} to {Last:O})", gap, gapFirst, gapLast);
            }

            // Consecutive decisions of the same UTC date go to that date's file in one append.
            while (written < batch.Count)
            {
                var file = FileNameFor(batch[written].At);
                var count = batch.Skip(written).TakeWhile(d => FileNameFor(d.At) == file).Count();
                var text = new StringBuilder();
                foreach (var decision in batch.Skip(written).Take(count))
                {
                    text.Append(DecisionJson.Serialize(decision)).Append('\n');
                }

                Append(file, text.ToString());
                written += count;
            }

            lock (_gate)
            {
                if (!_writable)
                {
                    _logger.LogInformation("The decision log at {Directory} is writable again", _directory);
                }

                _writable = true;
                _lastError = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                // Back to the front of the queue, oldest first; the bound applies again.
                for (var i = batch.Count - 1; i >= written; i--)
                {
                    _pending.AddFirst(batch[i]);
                }

                TrimPending();
            }

            Failed(ex);
        }
    }

    private void Append(string file, string text)
    {
        // A failed append may have left a partial line; the next record must not continue it.
        var prefix = _needsNewline ? "\n" : string.Empty;
        try
        {
            _files.Append(Path.Combine(_directory, file), prefix + text);
            _needsNewline = false;
        }
        catch
        {
            _needsNewline = true;
            throw;
        }
    }

    private void Failed(Exception ex)
    {
        lock (_gate)
        {
            if (_writable)
            {
                // Once per failure streak; control continues and the API reports it.
                _logger.LogError("The decision log at {Directory} cannot be written: {Reason}; decisions are kept in memory and retried every {Interval}", _directory, ex.Message, _options.WriteRetryInterval);
            }

            _writable = false;
            _lastError = ex.Message;
        }
    }

    private void TrimPending()
    {
        while (_pending.Count > _options.DecisionPendingCapacity)
        {
            var dropped = _pending.First!.Value;
            _pending.RemoveFirst();
            _gapFirst = _gapDropped == 0 || dropped.At < _gapFirst ? dropped.At : _gapFirst;
            _gapLast = _gapDropped == 0 || dropped.At > _gapLast ? dropped.At : _gapLast;
            _gapDropped++;
            _dropped++;
        }
    }

    private void Remember(Decision decision)
    {
        _recent.Add(new Entry(++_sequence, decision));
        if (_recent.Count > _options.DecisionMemoryCapacity)
        {
            _recent.RemoveRange(0, _recent.Count - _options.DecisionMemoryCapacity);
        }
    }

    private void Reload(IReadOnlyList<DayFile> days)
    {
        var loaded = new List<Decision>();
        for (var i = days.Count - 1; i >= 0 && loaded.Count < _options.DecisionMemoryCapacity; i--)
        {
            var file = new List<Decision>();
            foreach (var line in _files.ReadLines(Path.Combine(_directory, days[i].Name)))
            {
                if (DecisionJson.TryParse(line) is { } decision)
                {
                    file.Add(decision);
                }
            }

            loaded.InsertRange(0, file);
        }

        // A write retried after a partial failure can repeat lines; the id keeps one of each.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var decision in loaded.Where(d => seen.Add(d.Id)).TakeLast(_options.DecisionMemoryCapacity))
            {
                Remember(decision);
            }
        }
    }

    private void RunRetentionIfDue()
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (_retentionRanFor == today || _retentionDays() is not { } retention)
        {
            return;
        }

        try
        {
            var days = DayFiles();
            var keep = DecisionRetention.Kept(days.Select(d => (d.Name, d.Date)), today, retention);
            foreach (var day in days.Where(d => !keep.Contains(d.Name)))
            {
                _files.Delete(Path.Combine(_directory, day.Name));
                _logger.LogInformation("Deleted decision log {File} (older than {Days} days)", day.Name, retention);
            }

            _retentionRanFor = today;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Decision log retention could not run: {Reason}", ex.Message);
        }
    }

    /// <summary>The daily files present, oldest first; other files in the folder are left alone.</summary>
    private List<DayFile> DayFiles() =>
        [.. _files.List(_directory)
            .Select(name => name.EndsWith(Extension, StringComparison.Ordinal)
                && DateOnly.TryParseExact(name[..^Extension.Length], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? new DayFile(name, date)
                    : null)
            .OfType<DayFile>()
            .OrderBy(d => d.Date)];

    private string Cursor(long sequence) => string.Create(CultureInfo.InvariantCulture, $"{_instance}-{sequence}");

    /// <summary>A cursor is valid only in the process that issued it: sequences restart with the process.</summary>
    private long ParseCursor(string cursor)
    {
        var parts = cursor.Split('-');
        return parts.Length == 2 && parts[0] == _instance && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : throw new ArgumentException("The cursor was not issued by this decision log; start again without 'before'.", nameof(cursor));
    }

    private sealed record Entry(long Sequence, Decision Decision);

    private sealed record DayFile(string Name, DateOnly Date);
}
