using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// UI-04 and SIG-07 in the wizard: a picked entity shows its value and the age of its last report
/// (last_reported, never last_changed), and its refresh once enough pushed reports came in. An initial
/// state is no interval (its report may be hours old), and the history outlives a lease, so a refresh
/// detected in step 1 is still known at the save.
/// </summary>
public sealed class CandidateReadoutStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 7, 0, 0, TimeSpan.Zero);
    private static readonly EntityId Meter = EntityId.Parse("sensor.solaredge_m1_ac_current_a");

    [Fact]
    public void Readout_age_is_computed_from_last_reported_at_request_time()
    {
        var store = Store();
        store.Touch([Meter], new HashSet<EntityId>());
        store.Deliver(new EntityUpdateBatch(BatchOrigin.Initial, [new EntityUpdate(Meter, "2.61", "A", T0.AddHours(-1), T0.AddSeconds(-4))], [], []));

        var readout = store.Read(Meter, T0)!;

        Assert.Equal((ReadoutStatus.Found, "2.61", "A", TimeSpan.FromSeconds(4)), (readout.Status, readout.State, readout.Unit, readout.Age!.Value));
        Assert.Equal(TimeSpan.FromSeconds(14), store.Read(Meter, T0.AddSeconds(10))!.Age);
    }

    [Fact]
    public void Refresh_detected_after_min_intervals_initial_state_excluded()
    {
        // The initial state reported an hour ago must not count as one long interval.
        var store = Store();
        store.Touch([Meter], new HashSet<EntityId>());
        store.Deliver(new EntityUpdateBatch(BatchOrigin.Initial, [new EntityUpdate(Meter, "2.00", "A", T0.AddHours(-1), T0.AddHours(-1))], [], []));
        for (var i = 0; i < 8; i++)
        {
            Push(store, T0.AddSeconds(10 * i), (2 + (i * 0.01)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var refresh = store.Read(Meter, T0.AddSeconds(10 * i))!.Refresh;
            Assert.Null(refresh);
        }

        Push(store, T0.AddSeconds(80), "2.50");

        var readout = store.Read(Meter, T0.AddSeconds(80))!;
        Assert.Equal((TimeSpan.FromSeconds(10), 8, 8), (readout.Refresh!.Value, readout.Samples, readout.Needed));
    }

    [Fact]
    public void Refresh_detected_in_step_1_is_still_used_at_save_after_the_lease_ended()
    {
        // The store keeps history for the link's lifetime; only eviction beyond the retention bound forgets.
        var store = Store();
        store.Touch([Meter], new HashSet<EntityId>());
        for (var i = 0; i <= 8; i++)
        {
            Push(store, T0.AddSeconds(10 * i), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Many other entities later (step 4), within the retention bound.
        store.Touch([.. Enumerable.Range(0, 10).Select(i => EntityId.Parse($"switch.relay_{i}"))], new HashSet<EntityId>());

        Assert.Equal(TimeSpan.FromSeconds(10), store.Read(Meter, T0.AddMinutes(20))!.Refresh);
    }

    [Fact]
    public void Missing_entities_read_not_found_and_unavailable_states_unavailable()
    {
        var store = Store();
        var gone = EntityId.Parse("sensor.gone");
        var asleep = EntityId.Parse("sensor.solaredge_ac_power");
        store.Touch([gone, asleep], new HashSet<EntityId>());
        store.Deliver(new EntityUpdateBatch(BatchOrigin.Initial, [new EntityUpdate(asleep, "unavailable", null, T0, T0)], [], [gone]));

        Assert.Equal(ReadoutStatus.NotFound, store.Read(gone, T0)!.Status);
        Assert.Equal(ReadoutStatus.Unavailable, store.Read(asleep, T0)!.Status);
        Assert.Null(store.Read(EntityId.Parse("sensor.never_leased"), T0));
    }

    [Fact]
    public void A_probe_advances_the_report_time_without_counting_an_interval()
    {
        var store = Store();
        store.Touch([Meter], new HashSet<EntityId>());
        store.Deliver(new EntityUpdateBatch(BatchOrigin.Initial, [new EntityUpdate(Meter, "0.00", "A", T0, T0)], [], []));

        store.Deliver(new EntityUpdateBatch(BatchOrigin.Probe, [new EntityUpdate(Meter, "0.00", "A", T0, T0.AddMinutes(5))], [], []));

        var readout = store.Read(Meter, T0.AddMinutes(5))!;
        Assert.Equal((TimeSpan.Zero, 0), (readout.Age!.Value, readout.Samples));
    }

    [Fact]
    public void Retention_is_bounded_and_evicts_the_least_recently_asked_unless_held()
    {
        var store = new CandidateReadoutStore(64, 3, SetupFixtures.Control.Signals.Refresh);
        var held = EntityId.Parse("switch.held");
        store.Touch([held], new HashSet<EntityId>());
        store.Touch([EntityId.Parse("sensor.a"), EntityId.Parse("sensor.b")], new HashSet<EntityId>());

        store.Touch([EntityId.Parse("sensor.c")], new HashSet<EntityId> { held });

        Assert.NotNull(store.Read(held, T0));
        Assert.Null(store.Read(EntityId.Parse("sensor.a"), T0));
        Assert.NotNull(store.Read(EntityId.Parse("sensor.c"), T0));
    }

    private static CandidateReadoutStore Store() => new(64, 256, SetupFixtures.Control.Signals.Refresh);

    private static void Push(CandidateReadoutStore store, DateTimeOffset at, string state) =>
        store.Deliver(new EntityUpdateBatch(BatchOrigin.Change, [new EntityUpdate(Meter, state, "A", at, at)], [], []));
}
