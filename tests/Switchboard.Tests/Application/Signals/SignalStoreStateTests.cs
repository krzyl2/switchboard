using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// What the store keeps across a reload, an invalid document, a disconnect and a clock step, pinned
/// before its registry, intake, freshness, balance check and snapshot building were separated: the
/// signal set, the derived tables and the tracked set replace together, and the counters outlive them.
/// </summary>
public sealed class SignalStoreStateTests
{
    private const string Current = "sensor.meter_current_a";
    private const string PowerA = "sensor.meter_power_a";
    private const string Inverter = "sensor.inverter_power";
    private const string Invalid = "schema_version: 1\nconnection: {}\n";

    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase());

    [Fact]
    public void An_invalid_document_forgets_every_sample_and_the_next_valid_one_starts_from_nothing()
    {
        // Protection data from before an invalid document must never come back with the fix: the
        // document may have changed what the entity means.
        _store.Push(Current, "5.0");
        _store.Store.TryTakeTrackedChange(out _);

        _store.Store.ApplyConfiguration(TestConfigurations.Parse(Invalid), _store.Now);
        Assert.True(_store.Store.TryTakeTrackedChange(out var none));
        Assert.Empty(none);
        Assert.Empty(_store.Store.RuntimeWarnings());
        Assert.Empty(_store.Store.ReportedOptions());

        _store.Store.ApplyConfiguration(TestConfigurations.Valid(TestConfigurations.ThreePhase()), _store.Now);

        Assert.True(_store.Store.TryTakeTrackedChange(out var again));
        Assert.NotEmpty(again);
        Assert.Equal((SignalState.Unavailable, SignalDetail.AwaitingFirstSample), (_store.Reading("grid_current_l1").State, _store.Reading("grid_current_l1").Detail));
        Assert.Equal(ProtectionInvalidReason.CurrentAwaiting, _store.Protection(Phase.L1).InvalidReason);
    }

    [Fact]
    public void An_invalid_document_leaves_a_snapshot_that_names_the_absence_and_keeps_the_sequence_running()
    {
        var before = _store.Snapshot().Sequence;

        _store.Store.ApplyConfiguration(TestConfigurations.Parse(Invalid), _store.Now);
        var snapshot = _store.Snapshot();

        Assert.Equal(before + 1, snapshot.Sequence);
        Assert.Equal(SetupAbsence.Invalid, snapshot.Protection.SetupAbsence);
        Assert.All(snapshot.Dispatch.Phases, p => Assert.Equal(DispatchInvalidReason.ConfigurationInvalid, p.InvalidReason));
        Assert.Empty(snapshot.Balance);
        Assert.Null(snapshot.Prices);
        Assert.Null(snapshot.Forecast);
    }

    [Fact]
    public void The_rejection_and_balance_counters_outlive_a_reload_and_an_invalid_document()
    {
        // The counters are the process's, not the document's: a reload never makes a refusal vanish.
        _store.Push(Current, "5400", unit: "mA");
        _store.Push(Inverter, "0");
        _store.Push(PowerA, "1400");
        var counted = _store.Snapshot();
        Assert.Equal((1L, 1L), (counted.Rejections.InvalidSample, counted.Rejections.BalanceResidual));
        var balance = counted.Balance.Single(b => b.Phase == Phase.L1);
        Assert.Equal(1, balance.Rejections);

        _store.Store.ApplyConfiguration(TestConfigurations.Parse(Invalid), _store.Now);
        _store.Store.ApplyConfiguration(TestConfigurations.Valid(TestConfigurations.ThreePhase()), _store.Now);

        var after = _store.Snapshot();
        Assert.Equal((1L, 1L), (after.Rejections.InvalidSample, after.Rejections.BalanceResidual));
        Assert.Equal(balance, after.Balance.Single(b => b.Phase == Phase.L1));
    }

    [Fact]
    public void A_reload_that_drops_a_signal_stops_tracking_its_entity_and_ignores_what_it_reports_later()
    {
        _store.Store.TryTakeTrackedChange(out var first);
        Assert.Contains(first, t => t.Entity.Value == Inverter);

        _store.Store.ApplyConfiguration(TestConfigurations.Valid(TestConfigurations.ThreePhase(inverter: false)), _store.Now);

        Assert.True(_store.Store.TryTakeTrackedChange(out var second));
        Assert.DoesNotContain(second, t => t.Entity.Value == Inverter);
        _store.Push(Inverter, "3000");
        Assert.DoesNotContain(_store.Snapshot().Signals, s => s.Name?.Value == "pv_power");
    }

    [Fact]
    public void A_reload_applies_the_new_definition_to_a_kept_sample_at_once()
    {
        // The kept runtime takes the new max_age: a sample 100 s old is stale under 90 s, fresh under 120 s.
        _store.Push(Current, "5.0");
        _store.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(SignalState.Stale, _store.Reading("grid_current_l1").State);

        _store.Store.ApplyConfiguration(TestConfigurations.Valid(TestConfigurations.ThreePhase(currentMaxAge: "120s")), _store.Now);

        Assert.Equal(SignalState.Fresh, _store.Reading("grid_current_l1").State);
        Assert.Equal(TimeSpan.FromSeconds(120), _store.Reading("grid_current_l1").MaxAge);
    }

    [Fact]
    public void Reported_options_are_known_only_while_connected_and_only_for_a_present_entity()
    {
        var store = new StoreHarness(TestDevices.Document(SelectStopRig.Charger));
        var force = EntityId.Parse("select.charger_force");
        store.Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Initial, new EntityUpdate(force, "0", null, store.Now, store.Now) { Options = ["0", "1", "2"] }), store.Now);
        Assert.Equal(["0", "1", "2"], store.Store.ReportedOptions()[force]);

        store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        Assert.Empty(store.Store.ReportedOptions());

        store.Store.ApplySourceStatus(SignalSourceStatus.Connected);
        Assert.Equal(["0", "1", "2"], store.Store.ReportedOptions()[force]);

        store.Store.Apply(new EntityUpdateBatch(BatchOrigin.Change, [], [force], []), store.Now);
        Assert.Empty(store.Store.ReportedOptions());
    }

    [Fact]
    public void A_wrong_unit_is_refused_counted_and_leaves_the_signal_rejected_not_stale()
    {
        _store.Push(Current, "5400", unit: "mA");

        var reading = _store.Reading("grid_current_l1");
        Assert.Equal(SignalState.Rejected, reading.State);
        Assert.Equal(1, reading.RejectedCount);
        Assert.Equal(1, _store.Snapshot().Rejections.InvalidSample);
        Assert.Equal(ProtectionInvalidReason.CurrentRejected, _store.Protection(Phase.L1).InvalidReason);
    }

    [Fact]
    public void A_clock_step_forgets_every_sample_and_asks_for_every_probe_exactly_once()
    {
        _store.Push(Current, "5.0");

        _store.Store.ForgetSamplesAfterClockStep();

        Assert.Equal(ProtectionInvalidReason.CurrentAwaiting, _store.Protection(Phase.L1).InvalidReason);
        var all = _store.DueProbes();
        Assert.Equal(_store.Configuration.Configuration!.Signals.Length, all.Count);
        Assert.Empty(_store.DueProbes());
    }

    [Fact]
    public void Deadlines_are_the_probe_moment_and_nothing_with_no_configuration()
    {
        _store.Push(Current, "5.0");

        Assert.Equal(_store.Now + TimeSpan.FromSeconds(45), _store.Store.NextDeadline(_store.Now));

        var empty = new StoreHarness(Invalid, connect: false);
        Assert.Null(empty.Store.NextDeadline(empty.Now));
        Assert.Empty(empty.DueProbes());
    }

    [Fact]
    public void A_snapshot_a_consumer_holds_does_not_move_with_later_input_or_reload()
    {
        _store.Push(Current, "5.0");
        var held = _store.Snapshot();
        var signals = held.Signals;
        var phase = held.Protection.For(Phase.L1);

        _store.Advance(TimeSpan.FromSeconds(200));
        _store.Push(Current, "9.0");
        _store.Store.ApplyConfiguration(TestConfigurations.Parse(Invalid), _store.Now);
        _ = _store.Snapshot();

        Assert.Equal(signals, held.Signals);
        Assert.Same(phase, held.Protection.For(Phase.L1));
        Assert.Equal(5.0, held.Protection.For(Phase.L1)!.Current!.Magnitude.Value);
        Assert.Equal(SignalState.Fresh, held.Signals.Single(s => s.Name?.Value == "grid_current_l1").State);
    }

    [Fact]
    public void The_inbox_applies_inputs_in_arrival_order_and_a_tick_changes_nothing()
    {
        // One reader, strictly ordered: a state before any document is dropped, then an invalid
        // document, the valid one, a tick and a state of it, all in one drain.
        var time = new ObservableTimeProvider();
        var inbox = new SignalInbox();
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(time.GetUtcNow()), null, [], []));
        var runner = new SignalStoreRunner(inbox, store, new RecordingSignalSource(), holder, new SignalClock(time), NullLogger<SignalStoreRunner>.Instance);
        var current = EntityId.Parse(Current);
        EntityUpdateBatch State(string value) => EntityUpdateBatch.Of(BatchOrigin.Change, new EntityUpdate(current, value, "A", time.GetUtcNow(), time.GetUtcNow()));

        inbox.Post(State("7"));
        inbox.Post(TestConfigurations.Parse(Invalid));
        inbox.Post(TestConfigurations.Valid(TestConfigurations.ThreePhase()));
        inbox.Post(SignalSourceStatus.Connected);
        inbox.PostTick();
        inbox.Post(State("5"));
        runner.Step();

        Assert.Equal(5, holder.Current.Snapshot.Protection.For(Phase.L1)!.Current!.Magnitude.Value);
    }
}
