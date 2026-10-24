using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// SIG-05 and SIG-06: a signal is fresh while Home Assistant keeps reporting it, measured from
/// last_reported. last_changed makes a healthy constant signal look stale and a frozen sensor look
/// fresh for as long as its last value repeats (07-PITFALLS). Stale protection data is invalid.
/// </summary>
public sealed class FreshnessTests
{
    private const string Current = "sensor.meter_current_a";
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);

    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase());

    [Fact]
    public void Frozen_sensor_turns_stale_exactly_at_max_age_not_a_tick_earlier()
    {
        _store.Push(Current, "5.4");

        _store.Advance(MaxAge - TimeSpan.FromTicks(1));
        Assert.Equal(SignalState.Fresh, _store.Reading("grid_current_l1").State);

        _store.Advance(TimeSpan.FromTicks(1));
        Assert.Equal((SignalState.Stale, SignalDetail.MaxAgeExceeded), (_store.Reading("grid_current_l1").State, _store.Reading("grid_current_l1").Detail));
    }

    [Fact]
    public void Freshness_follows_last_reported_not_last_changed()
    {
        // The value last changed ten minutes ago, but HA reported it a second ago: fresh.
        var changed = _store.Now - TimeSpan.FromMinutes(10);
        _store.Initial((Current, "5.4", changed));
        Assert.Equal(SignalState.Stale, _store.Reading("grid_current_l1").State);

        _store.ProbeAnswer(Current, "5.4", changed, _store.Now - TimeSpan.FromSeconds(1));

        Assert.Equal(SignalState.Fresh, _store.Reading("grid_current_l1").State);
    }

    [Fact]
    public void Repeated_value_stays_fresh_because_probe_sees_last_reported_advance()
    {
        var changed = _store.Now;
        _store.Push(Current, "5.4");
        for (var step = 0; step < 20; step++)
        {
            _store.Advance(TimeSpan.FromSeconds(15));
            if (_store.DueProbes().Contains(Current))
            {
                _store.ProbeAnswer(Current, "5.4", changed, _store.Now - TimeSpan.FromSeconds(3));
            }

            Assert.Equal(SignalState.Fresh, _store.Reading("grid_current_l1").State);
        }
    }

    [Fact]
    public void Frozen_sensor_goes_stale_because_probe_sees_last_reported_stand_still()
    {
        var reported = _store.Now;
        _store.Push(Current, "5.4");
        while (_store.Now < reported + MaxAge)
        {
            _store.Advance(TimeSpan.FromSeconds(15));
            if (_store.DueProbes().Contains(Current))
            {
                _store.ProbeAnswer(Current, "5.4", reported, reported);
            }
        }

        Assert.Equal(SignalState.Stale, _store.Reading("grid_current_l1").State);
        Assert.False(_store.Protection(Phase.L1).DataValid);
    }

    [Fact]
    public void Probe_is_requested_after_half_max_age_of_silence_not_before()
    {
        _store.Push(Current, "5.4");

        _store.Advance((MaxAge / 2) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(Current, _store.DueProbes());

        _store.Advance(TimeSpan.FromTicks(1));
        Assert.Contains(Current, _store.DueProbes());
        Assert.DoesNotContain(Current, _store.DueProbes());
    }

    [Fact]
    public void No_probe_while_the_source_is_disconnected()
    {
        _store.Push(Current, "5.4");
        _store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        _store.Advance(MaxAge);

        Assert.Empty(_store.DueProbes());
    }

    [Fact]
    public void Late_probe_answer_never_reverts_a_newer_pushed_value()
    {
        var first = _store.Now;
        _store.Push(Current, "5.4");
        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(Current, "7.9");

        _store.ProbeAnswer(Current, "5.4", first, _store.Now);

        Assert.Equal(7.9, _store.Protection(Phase.L1).Current!.Magnitude.Value);
    }

    [Fact]
    public void Probe_answer_with_a_newer_state_is_a_missed_push_and_applies()
    {
        _store.Push(Current, "5.4");
        _store.Advance(TimeSpan.FromSeconds(50));

        _store.ProbeAnswer(Current, "6.1", _store.Now - TimeSpan.FromSeconds(20), _store.Now - TimeSpan.FromSeconds(1));

        Assert.Equal(6.1, _store.Protection(Phase.L1).Current!.Magnitude.Value);
    }

    [Fact]
    public void Future_report_timestamp_is_clamped_to_arrival()
    {
        // A clock running ahead in Home Assistant must not extend freshness.
        _store.Push(Current, "5.4", at: _store.Now + TimeSpan.FromMinutes(10));

        _store.Advance(MaxAge);

        Assert.Equal(SignalState.Stale, _store.Reading("grid_current_l1").State);
    }

    [Fact]
    public void Frozen_sensor_on_a_clock_running_ahead_goes_stale_at_max_age_despite_probes()
    {
        // Standalone install, HA's clock 10 min ahead, meter integration frozen: every probe returns
        // the same future last_reported. Clamping it to each probe's arrival must not count as a new
        // report, or the frozen current would stay valid for the whole skew.
        var arrival = _store.Now;
        var future = arrival + TimeSpan.FromMinutes(10);
        _store.Push(Current, "5.4", at: future);

        while (_store.Now < arrival + MaxAge - TimeSpan.FromSeconds(1))
        {
            _store.Advance(TimeSpan.FromSeconds(1));
            if (_store.DueProbes().Contains(Current))
            {
                _store.ProbeAnswer(Current, "5.4", future, future);
            }
        }

        _store.AdvanceTo(arrival + MaxAge - TimeSpan.FromTicks(1));
        Assert.True(_store.Protection(Phase.L1).DataValid);
        _store.Advance(TimeSpan.FromTicks(1));
        Assert.Equal((false, ProtectionInvalidReason.CurrentStale), (_store.Protection(Phase.L1).DataValid, _store.Protection(Phase.L1).InvalidReason));
    }

    [Fact]
    public void Healthy_sensor_on_a_clock_running_ahead_stays_fresh_through_probes()
    {
        // The fix for a frozen sensor must not make a healthy repeating one stale: its future
        // last_reported still moves on, and each move is a confirmation.
        var future = _store.Now + TimeSpan.FromMinutes(10);
        _store.Push(Current, "5.4", at: future);
        for (var step = 0; step < 30; step++)
        {
            _store.Advance(TimeSpan.FromSeconds(10));
            if (_store.DueProbes().Contains(Current))
            {
                _store.ProbeAnswer(Current, "5.4", future, _store.Now + TimeSpan.FromMinutes(10));
            }

            Assert.Equal(SignalState.Fresh, _store.Reading("grid_current_l1").State);
        }
    }

    [Fact]
    public void Stale_current_invalidates_protection_for_its_phase_only()
    {
        _store.Push("sensor.meter_current_b", "3.0");
        _store.Push("sensor.meter_current_c", "4.0");
        _store.Push(Current, "5.4");
        _store.Advance(TimeSpan.FromSeconds(60));
        _store.Push("sensor.meter_current_b", "3.1");
        _store.Push("sensor.meter_current_c", "4.1");
        _store.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal((false, ProtectionInvalidReason.CurrentStale), (_store.Protection(Phase.L1).DataValid, _store.Protection(Phase.L1).InvalidReason));
        Assert.True(_store.Protection(Phase.L2).DataValid);
        Assert.True(_store.Protection(Phase.L3).DataValid);
    }

    [Fact]
    public void Source_disconnection_invalidates_every_phase_immediately()
    {
        foreach (var letter in new[] { "a", "b", "c" })
        {
            _store.Push($"sensor.meter_current_{letter}", "5.0");
        }

        _store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);

        Assert.All(_store.Snapshot().Protection.Phases, p => Assert.Equal((false, ProtectionInvalidReason.SourceDisconnected), (p.DataValid, p.InvalidReason)));
        Assert.All(_store.Snapshot().Dispatch.Phases, p => Assert.False(p.DataValid));
    }

    [Fact]
    public void Invalid_configuration_means_no_valid_protection_data()
    {
        _store.Push(Current, "5.4");

        _store.Store.ApplyConfiguration(TestConfigurations.Parse("schema_version: 1\nconnection: {}\n"), _store.Now);

        var snapshot = _store.Snapshot();
        Assert.False(snapshot.ConfigurationValid);
        Assert.Empty(snapshot.Signals);
        Assert.Equal([Phase.L1, Phase.L2, Phase.L3], snapshot.Protection.Phases.Select(p => p.Phase));
        Assert.All(snapshot.Protection.Phases, p => Assert.Equal((false, ProtectionInvalidReason.ConfigurationInvalid), (p.DataValid, p.InvalidReason)));
    }

    [Fact]
    public void Missing_configuration_file_means_no_valid_protection_data()
    {
        _store.Push(Current, "5.4");

        _store.Store.ApplyConfiguration(ConfigurationResult.Failed(
            ConfigurationStatus.Missing,
            [new ConfigurationIssue(ConfigurationIssueCodes.FileMissing, string.Empty, null, null, "missing")],
            TestConfigurations.Path,
            _store.Now), _store.Now);

        Assert.All(_store.Snapshot().Protection.Phases, p => Assert.False(p.DataValid));
        Assert.True(_store.Store.TryTakeTrackedChange(out var tracked) && tracked.IsEmpty, "Nothing stays subscribed.");
    }

    [Fact]
    public void A_signal_that_never_reported_is_unavailable_and_invalidates_protection()
    {
        Assert.Equal((SignalState.Unavailable, SignalDetail.AwaitingFirstSample), (_store.Reading("grid_current_l1").State, _store.Reading("grid_current_l1").Detail));
        Assert.Equal(ProtectionInvalidReason.CurrentAwaiting, _store.Protection(Phase.L1).InvalidReason);
    }

    [Fact]
    public void Missing_and_removed_entities_invalidate_protection()
    {
        _store.Store.Apply(new Switchboard.Application.Signals.EntityUpdateBatch(
            Switchboard.Application.Signals.BatchOrigin.Initial, [], [], [Switchboard.Application.Signals.EntityId.Parse(Current)]), _store.Now);
        Assert.Equal(ProtectionInvalidReason.CurrentMissing, _store.Protection(Phase.L1).InvalidReason);

        _store.Push(Current, "5.0");
        Assert.True(_store.Protection(Phase.L1).DataValid);

        _store.Store.Apply(new Switchboard.Application.Signals.EntityUpdateBatch(
            Switchboard.Application.Signals.BatchOrigin.Change, [], [Switchboard.Application.Signals.EntityId.Parse(Current)], []), _store.Now);
        Assert.Equal((SignalState.Unavailable, SignalDetail.EntityRemoved), (_store.Reading("grid_current_l1").State, _store.Reading("grid_current_l1").Detail));
        Assert.Equal(ProtectionInvalidReason.CurrentMissing, _store.Protection(Phase.L1).InvalidReason);
    }

    [Fact]
    public void Unavailable_state_invalidates_protection_at_once_not_after_max_age()
    {
        _store.Push(Current, "5.0");
        _store.Advance(TimeSpan.FromSeconds(1));
        _store.Push(Current, "unavailable");

        Assert.Equal(ProtectionInvalidReason.CurrentUnavailable, _store.Protection(Phase.L1).InvalidReason);
        Assert.Null(_store.Reading("grid_current_l1").Value);
    }

    [Fact]
    public void Signal_with_only_rejected_samples_goes_stale()
    {
        // A meter that keeps sending unusable values must not keep showing its last good one forever.
        _store.Push(Current, "5.0");
        for (var i = 1; i <= 3; i++)
        {
            _store.Advance(TimeSpan.FromSeconds(30));
            _store.Push(Current, "5000", unit: "mA");
        }

        Assert.Equal((SignalState.Stale, SignalDetail.MaxAgeExceeded), (_store.Reading("grid_current_l1").State, _store.Reading("grid_current_l1").Detail));
        Assert.Equal(3, _store.Reading("grid_current_l1").RejectedCount);
    }

    [Fact]
    public void Single_phase_requires_only_L1()
    {
        var store = new StoreHarness("""
            schema_version: 1
            connection: { topology: single_phase, breaker_rating_a: 32, characteristic: C, voltage_v: 230 }
            signals:
              grid_current:
                entity: sensor.meter_current
                kind: current
                unit: A
                sign: positive_import
                max_age: 60s
                role: phase_current
                phase: L1
            """);
        store.Push("sensor.meter_current", "-6.5");

        var protection = store.Snapshot().Protection;
        Assert.Equal([Phase.L1], protection.Phases.Select(p => p.Phase));
        Assert.True(protection.IsDataValid(Phase.L1));
        Assert.Equal(FlowDirection.Export, protection.For(Phase.L1)!.Current!.Direction);
        Assert.False(protection.IsDataValid(Phase.L2));
    }

    [Fact]
    public void Summed_topology_protects_every_phase_from_the_total_current()
    {
        var store = new StoreHarness("""
            schema_version: 1
            connection: { topology: three_phase_summed, breaker_rating_a: 25, characteristic: B, voltage_v: 230 }
            signals:
              total_current:
                entity: sensor.meter_total_current
                kind: current
                unit: A
                sign: positive_import
                max_age: 60s
                role: total_current
            """);
        Assert.All(store.Snapshot().Protection.Phases, p => Assert.Equal(ProtectionInvalidReason.CurrentAwaiting, p.InvalidReason));

        store.Push("sensor.meter_total_current", "21.0");

        var protection = store.Snapshot().Protection;
        Assert.All(protection.Phases, p => Assert.True(p.DataValid));
        Assert.Equal(21.0, protection.Total!.Current!.Signed.Value);
    }
}
