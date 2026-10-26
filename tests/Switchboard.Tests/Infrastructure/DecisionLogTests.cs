using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// MODE-03: every decision is one JSON line in the file of its UTC date, with its reason code and
/// inputs. The log must survive what a Raspberry Pi in a utility room does to it: a crash in the
/// middle of a line, a full SD card, a clock that jumps, and 23- and 25-hour local days. None of
/// that may ever reach the guard: recording only touches memory.
/// </summary>
public sealed class DecisionLogTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDataDirectory _data = new();
    private readonly FlakyFileSystem _files = new();
    private readonly FakeTimeProvider _time = new(Noon);
    private int? _retention = 30;

    [Fact]
    public async Task One_json_line_in_the_utc_day_file()
    {
        var log = Start();
        var decision = TestDecisions.Shed(Noon);

        log.Record([decision]);
        await log.FlushAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-24.jsonl"));
        var line = Assert.Single(lines);
        Assert.Contains("\"reason\":\"guard_tier_exceeded\"", line, StringComparison.Ordinal);
        Assert.Contains("\"codes\":6", line, StringComparison.Ordinal);
        Assert.Contains("\"refused_by\":\"mode_shadow\"", line, StringComparison.Ordinal);
        Assert.Contains("\"commanded\":false", line, StringComparison.Ordinal);

        // The inputs that produced it come back exactly: the log is the audit trail.
        var parsed = DecisionJson.TryParse(line)!;
        Assert.Equal(decision.Inputs, parsed.Inputs);
        Assert.Equal((decision.Id, decision.At, decision.Device, decision.Phase, decision.Action, decision.Reason, decision.Overridden), (parsed.Id, parsed.At, parsed.Device, parsed.Phase, parsed.Action, parsed.Reason, parsed.Overridden));
    }

    [Fact]
    public async Task Rotation_at_utc_midnight_to_the_tick()
    {
        var log = Start();
        var midnight = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

        log.Record([TestDecisions.Shed(midnight - TimeSpan.FromTicks(1)), TestDecisions.Shed(midnight)]);
        await log.FlushAsync(CancellationToken.None);

        Assert.Single(File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-24.jsonl")));
        Assert.Single(File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-25.jsonl")));
    }

    [Fact]
    public async Task A_25_hour_local_day_spans_two_utc_files_without_loss()
    {
        // 25 October 2026 in Poland runs from 00:00+02:00 to 24:00+01:00: 25 hours (DST ends).
        var localStart = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2));
        var localEnd = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromHours(25), localEnd - localStart);
        var log = Start();
        var decisions = Enumerable.Range(0, 25).Select(h => TestDecisions.Shed(localStart.AddHours(h))).ToList();

        log.Record(decisions);
        await log.FlushAsync(CancellationToken.None);

        // Files follow UTC dates, never local ones, so nothing is lost or written twice.
        var first = File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-24.jsonl"));
        var second = File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-25.jsonl"));
        Assert.Equal((2, 23), (first.Length, second.Length));
        Assert.Equal(decisions.Select(d => d.Id), first.Concat(second).Select(l => DecisionJson.TryParse(l)!.Id));
    }

    [Fact]
    public async Task Partial_last_line_isolated()
    {
        // A crash in the middle of a write leaves half a line; the next decision must not be glued to it.
        Directory.CreateDirectory(_data.Decisions);
        var file = Path.Combine(_data.Decisions, "2026-10-24.jsonl");
        File.WriteAllText(file, DecisionJson.Serialize(TestDecisions.Shed(Noon)) + "\n{\"v\":1,\"codes\":2,\"id\":\"cut-off");
        var log = Start();

        var next = TestDecisions.Shed(Noon.AddSeconds(1));
        log.Record([next]);
        await log.FlushAsync(CancellationToken.None);

        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.Null(DecisionJson.TryParse(lines[1]));
        Assert.Equal(next.Id, DecisionJson.TryParse(lines[2])!.Id);
        Assert.Equal(2, Start().Query(Query()).Items.Length);
    }

    [Fact]
    public async Task Append_after_failed_write_starts_a_new_line()
    {
        // Disk full in the middle of a line: the retried write starts on a new line, so the partial
        // line costs only itself.
        var log = Start();
        _files.Fail = true;
        _files.PartialCharacters = 20;
        var first = TestDecisions.Shed(Noon);
        log.Record([first]);
        await log.FlushAsync(CancellationToken.None);

        _files.Fail = false;
        await log.FlushAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-24.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Null(DecisionJson.TryParse(lines[0]));
        Assert.Equal(first.Id, DecisionJson.TryParse(lines[1])!.Id);
    }

    [Fact]
    public async Task Disk_full_keeps_guard_running_and_reports_unwritable()
    {
        using var stop = new CancellationTokenSource();
        var time = new ObservableTimeProvider();
        var retry = TestDecisions.Options().WriteRetryInterval;
        var log = new JsonlDecisionLog(_files, _data.Path, TestDecisions.Options(), () => _retention, time, NullLogger<JsonlDecisionLog>.Instance);
        log.Start();
        var writer = log.RunAsync(stop.Token);
        _files.Fail = true;

        // Record is the guard's call: it returns at once and never throws for the disk.
        log.Record([TestDecisions.Shed(Noon)]);
        await time.WaitForTimerAsync(retry);

        Assert.Equal((false, "No space left on device"), (log.Status.Writable, log.Status.LastError));
        Assert.Single(log.Query(Query()).Items);

        // The writer retries on its own clock, and keeps doing so while the disk stays full.
        time.Advance(retry);
        await time.WaitForTimerAsync(retry);
        Assert.False(log.Status.Writable);

        _files.Fail = false;
        time.Advance(retry);
        await log.FlushAsync(CancellationToken.None);

        Assert.True(log.Status.Writable);
        Assert.Single(File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-24.jsonl")));
        await stop.CancelAsync();
        await writer.Soon();
    }

    [Fact]
    public async Task Unwritable_log_memory_is_bounded()
    {
        // A full disk for days must not end in an out-of-memory kill: the oldest are dropped,
        // counted, and the gap is written first when the disk recovers.
        var log = Start(pending: 5);
        _files.Fail = true;
        var decisions = Enumerable.Range(0, 12).Select(i => TestDecisions.Shed(Noon.AddSeconds(i))).ToList();
        foreach (var decision in decisions)
        {
            log.Record([decision]);
            await log.FlushAsync(CancellationToken.None);
        }

        Assert.Equal(7, log.Status.Dropped);
        _files.Fail = false;
        await log.FlushAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(_data.Decisions, "2026-10-24.jsonl"));
        Assert.Contains("\"gap\":{\"dropped\":7", lines[0], StringComparison.Ordinal);
        Assert.Equal(decisions.TakeLast(5).Select(d => d.Id), lines.Skip(1).Select(l => DecisionJson.TryParse(l)!.Id));
        Assert.Equal(12, log.Query(Query(limit: 50)).Items.Length);
    }

    [Fact]
    public async Task Recent_decisions_reloaded_after_restart()
    {
        var log = Start();
        var decisions = Enumerable.Range(0, 5).Select(i => TestDecisions.Shed(Noon.AddMinutes(i))).Append(TestDecisions.Mode(Noon.AddMinutes(6))).ToList();
        log.Record(decisions);
        await log.FlushAsync(CancellationToken.None);

        var restarted = Start();

        Assert.Equal(decisions.Select(d => d.Id).Reverse(), restarted.Query(Query()).Items.Select(d => d.Id));
        Assert.Equal(decisions[^1].Inputs.Mode, restarted.Query(Query()).Items[0].Inputs.Mode);
    }

    [Fact]
    public void Retention_deletes_only_days_older_than_retention()
    {
        WriteDayFiles(Noon.AddDays(-39), 40);
        Start();

        // 30 days back from 2026-10-24 is 2026-09-24: that day and every later one stays.
        var kept = Directory.GetFiles(_data.Decisions).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(31, kept.Count);
        Assert.Equal("2026-09-24.jsonl", kept[0]);
    }

    [Fact]
    public void Retention_survives_a_forward_clock_jump()
    {
        // A clock a year ahead (a dead RTC battery, a bad NTP answer) must not wipe the log: the
        // newest retention_days files are always kept.
        WriteDayFiles(Noon.AddDays(-39), 40);
        _time.Advance(TimeSpan.FromDays(365));

        Start();

        Assert.Equal(30, Directory.GetFiles(_data.Decisions).Length);
    }

    [Fact]
    public void Retention_waits_for_a_valid_configuration()
    {
        // Without a valid configuration nobody knows retention_days; nothing is deleted on a guess.
        WriteDayFiles(Noon.AddDays(-99), 100);
        _retention = null;

        Start();

        Assert.Equal(100, Directory.GetFiles(_data.Decisions).Length);
    }

    [Fact]
    public void Paging_stable_across_clock_step_back()
    {
        // Decisions taken after a backward clock step carry earlier times; pages follow the order
        // they were taken in, so every decision appears exactly once.
        var log = Start();
        var taken = new List<Decision>();
        for (var i = 0; i < 9; i++)
        {
            var at = i < 5 ? Noon.AddMinutes(i) : Noon.AddMinutes(i - 8);
            taken.Add(TestDecisions.Shed(at));
            log.Record([taken[^1]]);
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = log.Query(Query(limit: 2, before: cursor));
            seen.AddRange(page.Items.Select(d => d.Id));
            cursor = page.Next;
        }
        while (cursor is not null);

        Assert.Equal(taken.Select(d => d.Id).Reverse(), seen);
    }

    [Fact]
    public void Filters_by_device_phase_and_reason()
    {
        var log = Start();
        log.Record([
            TestDecisions.Shed(Noon, "heater_l1", Phase.L1),
            TestDecisions.Shed(Noon, "heater_l2", Phase.L2, ReasonCode.GuardBudgetLow),
            TestDecisions.Event(Noon)]);

        Assert.Equal("heater_l2", Assert.Single(log.Query(Query() with { Device = DeviceName.Parse("heater_l2") }).Items).Device!.Value.Value);
        Assert.Equal(Phase.L1, Assert.Single(log.Query(Query() with { Phase = Phase.L1 }).Items).Phase);
        Assert.Equal(DecisionScope.System, Assert.Single(log.Query(Query() with { Reason = ReasonCode.SafeState }).Items).Scope);
    }

    [Fact]
    public void A_cursor_from_another_process_is_refused()
    {
        var log = Start();
        log.Record([TestDecisions.Shed(Noon), TestDecisions.Shed(Noon)]);
        var cursor = log.Query(Query(limit: 1)).Next!;

        Assert.Throws<ArgumentException>(() => Start().Query(Query(before: cursor)));
        Assert.Throws<ArgumentException>(() => log.Query(Query(before: "nonsense")));
    }

    [Fact]
    public void Lines_that_are_not_decisions_are_skipped()
    {
        Assert.Null(DecisionJson.TryParse(DecisionJson.Gap(3, Noon, Noon)));
        Assert.Null(DecisionJson.TryParse("""{"v":1,"codes":2,"id":"x","at":"2026-10-24T12:00:00.0000000Z","scope":"device","device":null,"stage":null,"phase":null,"action":"switch_off","reason":"guard_tier_exceeded","overridden":null,"mode":"shadow","commanded":false,"refused_by":null,"inputs":{}}"""));
        Assert.Null(DecisionJson.TryParse(DecisionJson.Serialize(TestDecisions.Shed(Noon)).Replace("guard_tier_exceeded", "made_up_code", StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse(DecisionJson.Serialize(TestDecisions.Shed(Noon)).Replace("\"codes\":6", "\"codes\":1", StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse("[]"));
    }

    [Fact]
    public void Lines_written_with_older_reason_list_versions_still_read_after_the_upgrade()
    {
        // 0.6.x wrote codes 2, 0.7.x/0.8.x codes 3, 0.9.x codes 4 and the command path codes 5; the Log
        // page, the API after a restart and the 30-day history must keep showing them (each version only
        // appended codes). A line naming a code of a later version is no decision of its own version and is skipped.
        var current = DecisionJson.Serialize(TestDecisions.Shed(Noon));
        foreach (var version in new[] { 2, 3, 4, 5 })
        {
            var line = current.Replace("\"codes\":6", $"\"codes\":{version}", StringComparison.Ordinal);
            Assert.Equal(ReasonCode.GuardTierExceeded, DecisionJson.TryParse(line)!.Reason);
        }

        var foreign = current.Replace("\"reason\":\"guard_tier_exceeded\"", "\"reason\":\"foreign_load\"", StringComparison.Ordinal);
        Assert.NotEqual(current, foreign);
        Assert.Null(DecisionJson.TryParse(foreign.Replace("\"codes\":6", "\"codes\":2", StringComparison.Ordinal)));
        Assert.Equal(ReasonCode.ForeignLoad, DecisionJson.TryParse(foreign.Replace("\"codes\":6", "\"codes\":3", StringComparison.Ordinal))!.Reason);
        Assert.Equal(ReasonCode.ForeignLoad, DecisionJson.TryParse(foreign)!.Reason);

        var balance = current.Replace("\"reason\":\"guard_tier_exceeded\"", "\"reason\":\"balance_projection_low\"", StringComparison.Ordinal);
        Assert.Null(DecisionJson.TryParse(balance.Replace("\"codes\":6", "\"codes\":3", StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse(balance.Replace("\"codes\":6", "\"codes\":2", StringComparison.Ordinal)));
        Assert.Equal(ReasonCode.BalanceProjectionLow, DecisionJson.TryParse(balance.Replace("\"codes\":6", "\"codes\":4", StringComparison.Ordinal))!.Reason);
        Assert.Equal(ReasonCode.BalanceProjectionLow, DecisionJson.TryParse(balance)!.Reason);

        // Version 5 (mode_active, command_failed, command_not_sent) is no decision of version 4 or earlier.
        foreach (var code in new[] { "mode_active", "command_failed", "command_not_sent" })
        {
            var command = current.Replace("\"reason\":\"guard_tier_exceeded\"", $"\"reason\":\"{code}\"", StringComparison.Ordinal);
            Assert.NotEqual(current, command);
            Assert.Null(DecisionJson.TryParse(command.Replace("\"codes\":6", "\"codes\":4", StringComparison.Ordinal)));
            Assert.Null(DecisionJson.TryParse(command.Replace("\"codes\":6", "\"codes\":2", StringComparison.Ordinal)));
            Assert.Equal(code, ReasonCodes.Name(DecisionJson.TryParse(command)!.Reason));
            Assert.Equal(code, ReasonCodes.Name(DecisionJson.TryParse(command.Replace("\"codes\":6", "\"codes\":5", StringComparison.Ordinal))!.Reason));
        }

        // Version 6 (the economic codes) is no decision of version 5 or any earlier one: a line that names one of
        // them under an older list would be a line that was edited, not one this add-on wrote.
        foreach (var code in new[] { "zone_allowed", "source_not_allowed", "value_below_cost", "surplus_available", "surplus_low" })
        {
            var economic = current.Replace("\"reason\":\"guard_tier_exceeded\"", $"\"reason\":\"{code}\"", StringComparison.Ordinal);
            Assert.NotEqual(current, economic);
            foreach (var older in new[] { 5, 4, 3, 2 })
            {
                Assert.Null(DecisionJson.TryParse(economic.Replace("\"codes\":6", $"\"codes\":{older}", StringComparison.Ordinal)));
            }

            Assert.Equal(code, ReasonCodes.Name(DecisionJson.TryParse(economic)!.Reason));
        }
    }

    [Theory]
    [InlineData("1e300")]
    [InlineData("-1")]
    public void A_line_with_an_impossible_duration_is_skipped_and_never_blocks_the_start(string seconds)
    {
        // A hand-edited or corrupted line that is still valid JSON ("min_on_remaining_s":1e300 does
        // not fit a TimeSpan) is not a decision. Reading it at start must skip it: a throw there
        // stopped the host, and the add-on crash-looped with no guard at all.
        var good = TestDecisions.Shed(Noon);
        var line = DecisionJson.Serialize(TestDecisions.Shed(Noon.AddMinutes(1)));
        var bad = line.Replace("\"min_on_remaining_s\":42", $"\"min_on_remaining_s\":{seconds}", StringComparison.Ordinal);
        Assert.NotEqual(line, bad);
        Directory.CreateDirectory(_data.Decisions);
        File.WriteAllLines(Path.Combine(_data.Decisions, "2026-10-24.jsonl"), [DecisionJson.Serialize(good), bad]);

        Assert.Null(DecisionJson.TryParse(bad));
        Assert.Equal([good.Id], Start().Query(Query()).Items.Select(d => d.Id));
    }

    [Fact]
    public void Safe_state_cause_round_trips()
    {
        // MODE-03: the cause of a system-wide safe state (here: the add-on stopping) is part of the
        // inputs the log keeps, and comes back exactly.
        var stop = new Decision(
            "test-cause",
            Noon,
            DecisionScope.System,
            null,
            null,
            null,
            DecisionAction.SafeStateEntered,
            ReasonCode.SafeState,
            null,
            Switchboard.Application.Modes.OperatingMode.Shadow,
            false,
            null,
            DecisionInputs.None with { SafeStateCause = Switchboard.Application.Guard.SafeStateCause.Shutdown });

        var line = DecisionJson.Serialize(stop);

        Assert.Contains("\"safe_state_cause\":\"shutdown\"", line, StringComparison.Ordinal);
        Assert.Equal(stop.Inputs, DecisionJson.TryParse(line)!.Inputs);
        Assert.Null(DecisionJson.TryParse(line.Replace("\"shutdown\"", "\"reboot\"", StringComparison.Ordinal)));
    }

    public void Dispose() => _data.Dispose();

    private static DecisionQuery Query(int limit = 100, string? before = null) => new(null, null, null, limit, before);

    private JsonlDecisionLog Start(int pending = 10000)
    {
        var log = new JsonlDecisionLog(_files, _data.Path, TestDecisions.Options(pending: pending), () => _retention, _time, NullLogger<JsonlDecisionLog>.Instance);
        log.Start();
        return log;
    }

    private void WriteDayFiles(DateTimeOffset first, int days)
    {
        Directory.CreateDirectory(_data.Decisions);
        for (var i = 0; i < days; i++)
        {
            var at = first.AddDays(i);
            File.WriteAllText(Path.Combine(_data.Decisions, JsonlDecisionLog.FileNameFor(at)), DecisionJson.Serialize(TestDecisions.Shed(at)) + "\n");
        }
    }
}
