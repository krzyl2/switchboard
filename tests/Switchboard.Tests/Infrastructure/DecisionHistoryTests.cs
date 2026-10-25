using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// MODE-04: the decision log is queryable in the UI over its whole retention, not only the memory
/// window. Newest first across the daily files, filtered, paged with a cursor that has no duplicates
/// or gaps while today's file keeps growing; corrupt and partial lines are skipped; a scan budget
/// bounds each request; exactly the files the log's retention keeps are read (never one it deletes,
/// never hiding one it keeps after a long downtime).
/// </summary>
public sealed class DecisionHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Today = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDataDirectory _data = new();
    private readonly FakeTimeProvider _time = new(Today);
    private readonly DecisionFileSystem _files = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Pages_across_retained_day_files_newest_first()
    {
        Write(Day(-2), 3, "a");
        Write(Day(-1), 3, "b");
        Write(Day(0), 3, "c");

        var first = History().Query(Query(limit: 4));
        var second = History().Query(Query(limit: 4, cursor: first.Next));
        var third = History().Query(Query(limit: 4, cursor: second.Next));

        Assert.Equal(["c2", "c1", "c0", "b2"], first.Items.Select(d => d.Id));
        Assert.Equal(["b1", "b0", "a2", "a1"], second.Items.Select(d => d.Id));
        Assert.Equal(["a0"], third.Items.Select(d => d.Id));
        Assert.Null(third.Next);
        Assert.Equal(DateOnly.FromDateTime(Day(-2).UtcDateTime), first.RetainedFrom);
    }

    [Fact]
    public void Cursor_has_no_duplicates_or_gaps_while_today_is_appended()
    {
        Write(Day(-1), 3, "b");
        Write(Day(0), 3, "c");
        var first = History().Query(Query(limit: 2));

        // Two more decisions arrive today before the next page is asked for.
        Append(Day(0).AddMinutes(30), "c3");
        Append(Day(0).AddMinutes(31), "c4");
        var rest = History().Query(Query(limit: 10, cursor: first.Next));

        Assert.Equal(["c2", "c1"], first.Items.Select(d => d.Id));
        Assert.Equal(["c0", "b2", "b1", "b0"], rest.Items.Select(d => d.Id));
        Assert.Equal(["c4", "c3", "c2"], History().Query(Query(limit: 3)).Items.Select(d => d.Id));
    }

    [Fact]
    public void Filters_apply_to_history()
    {
        Write(Day(-1), 4, "b", device: i => i % 2 == 0 ? "heater_l1" : "heater_l2");
        Write(Day(0), 4, "c", device: i => i % 2 == 0 ? "heater_l1" : "ev_charger");

        var page = History().Query(Query(limit: 10, device: "heater_l1"));
        var bounded = History().Query(Query(limit: 10, from: Day(0), to: Day(0).AddMinutes(2)));

        Assert.Equal(["c2", "c0", "b2", "b0"], page.Items.Select(d => d.Id));
        Assert.Equal(["c1", "c0"], bounded.Items.Select(d => d.Id));
    }

    [Fact]
    public void Corrupt_and_partial_lines_skipped()
    {
        Write(Day(0), 2, "c");
        File.AppendAllText(Path(Day(0)), "not json\n{\"id\":\"x\"}\n{\"truncated\":");

        var page = History().Query(Query(limit: 10));

        Assert.Equal(["c1", "c0"], page.Items.Select(d => d.Id));
    }

    [Fact]
    public void Scan_budget_returns_partial_page_with_cursor()
    {
        Write(Day(-1), 5, "b", device: _ => "heater_l2");
        Write(Day(0), 5, "c", device: _ => "heater_l2");
        var history = new JsonlDecisionHistory(_files, _data.Path, TestDecisions.Options(maxPage: 3, historyScanLines: 3), () => 30, _time);

        var page = history.Query(Query(limit: 3, device: "heater_l1"));
        var next = history.Query(Query(limit: 3, device: "heater_l1", cursor: page.Next));

        Assert.True(page.Partial);
        Assert.Empty(page.Items);
        Assert.NotNull(page.Next);
        Assert.Equal(3, page.Scanned);
        Assert.True(next.Partial);
    }

    [Fact]
    public void Only_retained_files_read()
    {
        // retention_days 1: the 40-day-old file is neither recent nor among the newest one, so the
        // log's retention deletes it and the history never shows it.
        Write(Day(-40), 2, "old");
        Write(Day(0), 2, "c");

        var page = History(retention: 1).Query(Query(limit: 10));

        Assert.Equal(["c1", "c0"], page.Items.Select(d => d.Id));
        Assert.Equal(DateOnly.FromDateTime(Day(0).UtcDateTime), page.RetainedFrom);
        Assert.Equal(1, page.RetentionDays);
    }

    [Fact]
    public void Files_the_log_keeps_after_a_long_downtime_are_shown()
    {
        // The add-on was stopped for 40 days (or the clock jumped): the log keeps its newest
        // retention_days files whatever their age, so they are still the decision history (MODE-04).
        Write(Day(-45), 1, "a");
        Write(Day(-40), 2, "b");

        var page = History().Query(Query(limit: 10));

        Assert.Equal(["b1", "b0", "a0"], page.Items.Select(d => d.Id));
        Assert.Equal(DateOnly.FromDateTime(Day(-45).UtcDateTime), page.RetainedFrom);
    }

    [Fact]
    public void Retention_keeps_recent_files_and_always_the_newest_ones()
    {
        var today = DateOnly.FromDateTime(Today.UtcDateTime);
        var days = new[] { -50, -45, -40, -3, 0 }.Select(d => (Name: $"d{d}", Date: today.AddDays(d))).ToList();

        // retention 3: d-3 and d0 are recent, d-40 is among the newest three; d-45 and d-50 go.
        Assert.Equal(["d-3", "d-40", "d0"], DecisionRetention.Kept(days, today, 3).Order(StringComparer.Ordinal));
        Assert.Equal(["d0"], DecisionRetention.Kept(days, today, 1).Order(StringComparer.Ordinal));
        Assert.Equal(["d-3", "d-40", "d-45", "d0"], DecisionRetention.Kept(days, today, 4).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_cursor_not_issued_by_the_history_is_refused()
    {
        Assert.Throws<ArgumentException>(() => History().Query(Query(limit: 2, cursor: "boot-12")));
    }

    private JsonlDecisionHistory History(int retention = 30) => new(_files, _data.Path, TestDecisions.Options(), () => retention, _time);

    private static DecisionHistoryQuery Query(int limit, string? cursor = null, string? device = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(from, to, device is null ? null : DeviceName.Parse(device), null, null, null, limit, cursor);

    private static DateTimeOffset Day(int offset) => new DateTimeOffset(Today.Date, TimeSpan.Zero).AddDays(offset);

    private string Path(DateTimeOffset day) => System.IO.Path.Combine(_data.Decisions, JsonlDecisionLog.FileNameFor(day));

    private void Write(DateTimeOffset day, int count, string prefix, Func<int, string>? device = null)
    {
        for (var i = 0; i < count; i++)
        {
            Append(day.AddMinutes(i), $"{prefix}{i}", device?.Invoke(i));
        }
    }

    private void Append(DateTimeOffset at, string id, string? device = null)
    {
        Directory.CreateDirectory(_data.Decisions);
        var decision = new Decision(
            id,
            at,
            device is null ? DecisionScope.System : DecisionScope.Device,
            device is null ? null : DeviceName.Parse(device),
            null,
            null,
            device is null ? DecisionAction.ModeResolved : DecisionAction.Release,
            device is null ? ReasonCode.ModeShadow : ReasonCode.HeadroomAvailable,
            null,
            OperatingMode.Shadow,
            false,
            null,
            DecisionInputs.None);
        File.AppendAllText(Path(at), DecisionJson.Serialize(decision) + "\n");
    }
}
