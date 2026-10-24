using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// The store hands out one immutable snapshot per cycle: no consumer ever reads a signal set that is
/// half old and half new, and nothing a consumer holds ever changes under it.
/// </summary>
public sealed class SnapshotTests
{
    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase());

    [Fact]
    public void New_immutable_instance_per_input_old_ones_never_change()
    {
        _store.Push("sensor.meter_current_a", "5.0");
        var before = _store.Snapshot();
        var readingBefore = before.Signals.Single(s => s.Name?.Value == "grid_current_l1");

        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push("sensor.meter_current_a", "9.0");
        var after = _store.Snapshot();

        Assert.NotSame(before, after);
        Assert.Same(readingBefore, before.Signals.Single(s => s.Name?.Value == "grid_current_l1"));
        Assert.Equal(5.0, before.Protection.For(Phase.L1)!.Current!.Magnitude.Value);
        Assert.Equal(9.0, after.Protection.For(Phase.L1)!.Current!.Magnitude.Value);
    }

    [Fact]
    public void Sequence_increases_monotonically()
    {
        var sequences = Enumerable.Range(0, 5).Select(_ => _store.Snapshot().Sequence).ToList();

        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(5, sequences.Distinct().Count());
    }

    [Fact]
    public void Unmapped_role_slots_are_listed_so_the_user_sees_what_could_be_mapped()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase(inverter: false));

        var unmapped = store.Snapshot().Signals.Where(s => s.State == SignalState.Unmapped).ToList();

        Assert.Contains(unmapped, s => s.Role == SignalRole.InverterPower && s.Name is null);
        Assert.DoesNotContain(unmapped, s => s.Role == SignalRole.PhaseCurrent);
    }

    [Fact]
    public void Valid_reload_keeps_samples_of_unchanged_signals()
    {
        // Editing a comment or a max_age must not blank protection data for a moment.
        _store.Push("sensor.meter_current_a", "5.0");
        _store.Store.TryTakeTrackedChange(out _);

        _store.Store.ApplyConfiguration(TestConfigurations.Valid(TestConfigurations.ThreePhase(currentMaxAge: "120s")), _store.Now);

        Assert.True(_store.Protection(Phase.L1).DataValid);
        Assert.False(_store.Store.TryTakeTrackedChange(out _), "The same entities need no new subscription.");
    }

    [Fact]
    public void Fixing_a_unit_rereads_the_last_state_without_waiting_for_a_push()
    {
        // HA sends nothing while a value holds, so a phase current that waited for its next change
        // after the fix would keep that phase's protection data invalid for no reason.
        var reported = _store.Now;
        _store.Push("sensor.meter_current_a", "5400", unit: "mA");
        Assert.Equal(ProtectionInvalidReason.CurrentRejected, _store.Protection(Phase.L1).InvalidReason);
        _store.Advance(TimeSpan.FromSeconds(10));

        _store.Store.ApplyConfiguration(
            TestConfigurations.Valid(Reading(TestConfigurations.ThreePhase(), "sensor.meter_current_a", "kind: current\n    unit: A", "kind: current\n    unit: mA")),
            _store.Now);

        var reading = _store.Reading("grid_current_l1");
        Assert.Equal((SignalState.Fresh, 5.4), (reading.State, Assert.IsType<CurrentValue>(reading.Value).Current.Value));
        Assert.Equal(reported, reading.LastReported);
        Assert.True(_store.Protection(Phase.L1).DataValid);
    }

    [Fact]
    public void Changing_a_sign_rereads_the_last_state_under_the_new_sign()
    {
        _store.Push("sensor.meter_power_a", "-500");
        Assert.Equal(500, _store.Dispatch(Phase.L1).NetImport!.Value.Value);

        _store.Store.ApplyConfiguration(
            TestConfigurations.Valid(Reading(TestConfigurations.ThreePhase(inverter: false), "sensor.meter_power_a", "sign: positive_export", "sign: positive_import")),
            _store.Now);

        Assert.Equal(-500, _store.Dispatch(Phase.L1).NetImport!.Value.Value);
    }

    [Fact]
    public void Reloading_an_inverter_asleep_keeps_it_asleep_instead_of_awaiting_until_sunrise()
    {
        // At night the inverter reports "unavailable" once and then nothing for hours.
        var asleep = TestConfigurations.ThreePhase(inverterCurrents: false)
            .Replace("role: inverter_power", "role: inverter_power\n    unavailable_as_zero: true", StringComparison.Ordinal);
        var store = new StoreHarness(asleep);
        store.Push("sensor.inverter_power", "unavailable");
        store.Push("sensor.meter_power_a", "-500");

        store.Store.ApplyConfiguration(
            TestConfigurations.Valid(Reading(asleep, "sensor.inverter_power", "sign: positive_import", "sign: positive_export")),
            store.Now);

        Assert.Equal((SignalState.Unavailable, SignalDetail.StateUnavailable), (store.Reading("pv_power").State, store.Reading("pv_power").Detail));
        Assert.True(store.Dispatch(Phase.L1).DataValid);
        Assert.Equal(500, store.Dispatch(Phase.L1).Load!.Value.Value);
    }

    /// <summary>Replaces <paramref name="from"/> with <paramref name="to"/> in the signal block of <paramref name="entity"/> only.</summary>
    private static string Reading(string yaml, string entity, string from, string to)
    {
        var start = yaml.IndexOf("entity: " + entity + "\n", StringComparison.Ordinal);
        var at = yaml.IndexOf(from, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && at > start && at - start < 120, $"'{from}' not found in the block of {entity}.");
        return string.Concat(yaml.AsSpan(0, at), to, yaml.AsSpan(at + from.Length));
    }
}

/// <summary>Detected refresh comes from pushed reports within one subscription only (SIG-07).</summary>
public sealed class RefreshDetectionTests
{
    private const string Current = "sensor.meter_current_a";

    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase());

    [Fact]
    public void Refresh_is_measured_from_pushed_reports_only()
    {
        var changed = _store.Now;
        _store.Initial((Current, "1.0", changed));
        for (var i = 0; i < 6; i++)
        {
            _store.Advance(TimeSpan.FromSeconds(10));
            _store.ProbeAnswer(Current, "1.0", changed, _store.Now);
        }

        Assert.Equal(0, _store.Reading("grid_current_l1").RefreshIntervals);

        PushEvery(TimeSpan.FromSeconds(10), 9);
        Assert.Equal(8, _store.Reading("grid_current_l1").RefreshIntervals);
        Assert.Equal(TimeSpan.FromSeconds(10), _store.Reading("grid_current_l1").DetectedRefresh);
    }

    [Fact]
    public void An_interval_never_spans_a_disconnect()
    {
        PushEvery(TimeSpan.FromSeconds(10), 5);
        _store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        _store.Advance(TimeSpan.FromMinutes(5));
        _store.Store.ApplySourceStatus(SignalSourceStatus.Connected);
        PushEvery(TimeSpan.FromSeconds(10), 5);

        Assert.Equal(8, _store.Reading("grid_current_l1").RefreshIntervals);
        Assert.Equal(TimeSpan.FromSeconds(10), _store.Reading("grid_current_l1").DetectedRefresh);
    }

    [Fact]
    public void Detected_refresh_drives_the_reserve_derivation()
    {
        Assert.False(_store.Snapshot().Reserve.Single(r => r.Phase == Phase.L1).RefreshDetected);

        PushEvery(TimeSpan.FromSeconds(30), 9);

        var reserve = _store.Snapshot().Reserve.Single(r => r.Phase == Phase.L1);
        Assert.True(reserve.RefreshDetected);
        Assert.Equal(631.5, reserve.Minimum.Value, 6);
    }

    [Fact]
    public void Max_age_too_short_for_the_detected_refresh_is_warned()
    {
        // 30 s max_age, 10 s refresh, 5 s probe allowance: 15 s left after the probe starts
        // covers the refresh plus the answer; 20 s refresh does not.
        var store = new StoreHarness(TestConfigurations.ThreePhase(currentMaxAge: "30s"));
        for (var i = 0; i < 9; i++)
        {
            store.Push(Current, (1 + (i * 0.1)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            store.Advance(TimeSpan.FromSeconds(20));
        }

        var warning = Assert.Single(store.Store.RuntimeWarnings());
        Assert.Equal(("max_age_short_for_probe", "signals.grid_current_l1.max_age"), (warning.Code, warning.Path));
    }

    private void PushEvery(TimeSpan interval, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _store.Advance(interval);
            _store.Push(Current, (2 + (i * 0.01)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>The runner: one reader, one snapshot per drained burst, one timer at the store's next deadline.</summary>
public sealed class SignalStoreRunnerTests : IAsyncDisposable
{
    private readonly ObservableTimeProvider _time = new();
    private readonly SignalInbox _inbox = new();
    private readonly SignalStore _store = new(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
    private readonly RecordingSignalSource _source = new();
    private readonly SignalSnapshotHolder _holder;
    private readonly SignalStoreRunner _runner;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<SignalDiagnostics> _published = [];
    private Task? _run;

    public SignalStoreRunnerTests()
    {
        _holder = new SignalSnapshotHolder(new SignalDiagnostics(_store.Snapshot(_time.GetUtcNow()), null, [], []));
        ((IProtectionInputSource)_holder).Published += (_, _) =>
        {
            lock (_published)
            {
                _published.Add(_holder.Current);
            }
        };
        _runner = new SignalStoreRunner(_inbox, _store, _source, _holder, new SignalClock(_time), NullLogger<SignalStoreRunner>.Instance);
    }

    [Fact]
    public void Three_phase_batch_published_once_never_half_applied()
    {
        _inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase()));
        _inbox.Post(SignalSourceStatus.Connected);
        _runner.Step();
        var publishedBefore = Count();

        var now = _time.GetUtcNow();
        _inbox.Post(new EntityUpdateBatch(BatchOrigin.Change, [Update("a", "5", now), Update("b", "6", now), Update("c", "7", now)], [], []));
        _runner.Step();

        Assert.Equal(publishedBefore + 1, Count());
        var protection = _holder.Current.Snapshot.Protection;
        Assert.Equal([5.0, 6.0, 7.0], protection.Phases.Select(p => p.Current!.Magnitude.Value));
    }

    [Fact]
    public void A_burst_of_queued_inputs_yields_one_snapshot()
    {
        _inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase()));
        _inbox.Post(SignalSourceStatus.Connected);
        var now = _time.GetUtcNow();
        foreach (var letter in new[] { "a", "b", "c" })
        {
            _inbox.Post(EntityUpdateBatch.Of(BatchOrigin.Change, Update(letter, "4", now)));
        }

        _runner.Step();

        Assert.Equal(1, Count());
        Assert.True(_holder.Current.Snapshot.Protection.Phases.All(p => p.DataValid));
    }

    [Fact]
    public void Configuration_tracks_its_entities_and_retracks_only_when_they_change()
    {
        _inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase()));
        _runner.Step();
        _inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase(currentMaxAge: "60s")));
        _runner.Step();
        _inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase(inverter: false)));
        _runner.Step();

        Assert.Equal(2, _source.Tracks.Count);
        Assert.Contains(_source.Tracks[0], t => t.Entity.Value == "sensor.inverter_power" && t.DeclaredUnit == "W");
        Assert.DoesNotContain(_source.Tracks[1], t => t.Entity.Value == "sensor.inverter_power");
    }

    [Fact]
    public async Task Runner_wakes_exactly_when_a_signal_turns_stale()
    {
        // No input arrives; only the runner's own timer can publish the transition, and it must
        // fire at max_age, not on some later poll.
        _inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase()));
        _inbox.Post(SignalSourceStatus.Connected);
        _inbox.Post(EntityUpdateBatch.Of(BatchOrigin.Change, Update("a", "5", _time.GetUtcNow())));
        _run = _runner.RunAsync(_stop.Token);

        // First deadline: the probe at half of max_age (90 s), then staleness at max_age.
        await _time.WaitForTimerAsync(TimeSpan.FromSeconds(45));
        _time.Advance(TimeSpan.FromSeconds(45));
        await _time.WaitForTimerAsync(TimeSpan.FromSeconds(45));
        Assert.Contains(_source.Probes, p => p.Value == "sensor.meter_current_a");
        Assert.True(_holder.Current.Snapshot.Protection.IsDataValid(Phase.L1));

        _time.Advance(TimeSpan.FromSeconds(45));
        await WaitForAsync(d => !d.Snapshot.Protection.IsDataValid(Phase.L1));

        Assert.Equal(ProtectionInvalidReason.CurrentStale, _holder.Current.Snapshot.Protection.For(Phase.L1)!.InvalidReason);
        Assert.Equal(_time.GetUtcNow(), _holder.Current.Snapshot.TakenAt);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.Soon();
        }

        _stop.Dispose();
    }

    private int Count()
    {
        lock (_published)
        {
            return _published.Count;
        }
    }

    private async Task WaitForAsync(Func<SignalDiagnostics, bool> condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, ProtectionInputs inputs)
        {
            if (condition(_holder.Current))
            {
                reached.TrySetResult();
            }
        }

        ((IProtectionInputSource)_holder).Published += Check;
        Check(null, _holder.Current.Snapshot.Protection);
        await reached.Task.Soon();
        ((IProtectionInputSource)_holder).Published -= Check;
    }

    private static EntityUpdate Update(string letter, string state, DateTimeOffset at) =>
        new(EntityId.Parse($"sensor.meter_current_{letter}"), state, "A", at, at);
}

/// <summary>
/// A wall-clock step backwards (NTP step, restored VM) puts every held report time in the future:
/// the held values would look fresh while every newer report, stamped before them, is dropped as
/// late. The runner must fail safe and read everything again.
/// </summary>
public sealed class ClockStepTests
{
    private readonly SteppableClock _clock = new(StoreHarness.Start);
    private readonly SignalInbox _inbox = new();
    private readonly SignalStore _store = new(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
    private readonly RecordingSignalSource _source = new();
    private readonly SignalSnapshotHolder _holder;
    private readonly SignalStoreRunner _runner;

    public ClockStepTests()
    {
        _holder = new SignalSnapshotHolder(new SignalDiagnostics(_store.Snapshot(_clock.Now), null, [], []));
        _runner = new SignalStoreRunner(_inbox, _store, _source, _holder, new SignalClock(_clock), NullLogger<SignalStoreRunner>.Instance);
        _inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase()));
        _inbox.Post(SignalSourceStatus.Connected);
        _inbox.Post(EntityUpdateBatch.Of(BatchOrigin.Change, Current("5", _clock.Now)));
        _runner.Step();
    }

    [Fact]
    public void Clock_stepping_back_invalidates_held_values_and_reads_every_signal_again()
    {
        Assert.True(_holder.Current.Snapshot.Protection.IsDataValid(Phase.L1));

        _clock.Now -= TimeSpan.FromHours(1);
        _runner.Step();

        Assert.Equal(ProtectionInvalidReason.CurrentAwaiting, _holder.Current.Snapshot.Protection.For(Phase.L1)!.InvalidReason);
        Assert.Contains(_source.Probes, p => p.Value == "sensor.meter_current_a");
        Assert.Equal(_store.ConfigurationResult!.Configuration!.Signals.Length, _source.Probes.Distinct().Count());
    }

    [Fact]
    public void Reports_stamped_after_the_step_are_accepted_not_dropped_as_late()
    {
        _clock.Now -= TimeSpan.FromHours(1);
        _runner.Step();

        _inbox.Post(EntityUpdateBatch.Of(BatchOrigin.Probe, Current("7", _clock.Now)));
        _runner.Step();

        Assert.Equal(7, _holder.Current.Snapshot.Protection.For(Phase.L1)!.Current!.Magnitude.Value);
    }

    [Fact]
    public void Timer_due_time_never_exceeds_what_the_platform_timer_accepts()
    {
        // System.Threading.Timer throws above 2^32 − 2 ms, which would stop the runner and the host.
        Assert.Equal(TimeSpan.FromSeconds(90), SignalStoreRunner.DueTime(TimeSpan.FromSeconds(90)));
        Assert.Equal(SignalStoreRunner.LongestTimerDue, SignalStoreRunner.DueTime(TimeSpan.FromDays(60)));
        Assert.Equal(TimeSpan.FromMilliseconds(4294967294), SignalStoreRunner.LongestTimerDue);
    }

    private static EntityUpdate Current(string state, DateTimeOffset at) =>
        new(EntityId.Parse("sensor.meter_current_a"), state, "A", at, at);

    private sealed class SteppableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
