using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// Development replay stands in for Home Assistant: recorded rows arrive when the signal clock
/// reaches them, a constant signal stays fresh because later rows prove it was alive, and after the
/// data ends everything goes stale.
/// </summary>
public sealed class ReplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly SignalInbox _inbox = new();

    [Fact]
    public void Unsorted_input_emitted_in_time_order()
    {
        var data = Csv(
            "sensor.b,2026-10-21T10:00:20Z,2",
            "sensor.a,2026-10-21T10:00:10Z,1",
            "sensor.a,2026-10-21T10:00:00Z,0");

        Assert.Equal([T0, T0.AddSeconds(10), T0.AddSeconds(20)], data.Rows.Select(r => r.Timestamp));
    }

    [Fact]
    public void Initial_is_latest_row_at_or_before_now()
    {
        var source = Source(Csv("sensor.a,2026-10-21T09:59:00Z,old", "sensor.a,2026-10-21T10:00:00Z,current", "sensor.a,2026-10-21T10:00:30Z,later"));
        source.Start();
        Drain();

        source.Track([new TrackedEntity(EntityId.Parse("sensor.a"), "W"), new TrackedEntity(EntityId.Parse("sensor.none"), "A")]);

        var initial = Batches().Single();
        Assert.Equal(BatchOrigin.Initial, initial.Origin);
        Assert.Equal(("current", "W"), (initial.Updates.Single().State, initial.Updates.Single().Unit));
        Assert.Equal([EntityId.Parse("sensor.none")], initial.Missing);
    }

    [Fact]
    public void Emits_rows_when_fake_clock_reaches_them()
    {
        var source = Source(Csv("sensor.a,2026-10-21T10:00:00Z,0", "sensor.a,2026-10-21T10:00:10Z,1", "sensor.a,2026-10-21T10:00:20Z,2"));
        source.Start();
        source.Track([new TrackedEntity(EntityId.Parse("sensor.a"), "A")]);
        Drain();

        _time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.Empty(Batches());

        _time.Advance(TimeSpan.FromTicks(1));
        var change = Batches().Single();
        Assert.Equal((BatchOrigin.Change, "1", T0.AddSeconds(10)), (change.Origin, change.Updates.Single().State, change.Updates.Single().Reported));
    }

    [Fact]
    public void Emits_only_tracked_entities()
    {
        var source = Source(Csv("sensor.a,2026-10-21T10:00:05Z,1", "sensor.b,2026-10-21T10:00:05Z,2"));
        source.Start();
        source.Track([new TrackedEntity(EntityId.Parse("sensor.b"), "W")]);
        Drain();

        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(["sensor.b"], Batches().SelectMany(b => b.Updates).Select(u => u.Entity.Value));
    }

    [Fact]
    public void Probe_answers_latest_row_reported_until_now_while_later_rows_prove_it_alive()
    {
        var source = Source(Csv("sensor.a,2026-10-21T10:00:00Z,7", "sensor.a,2026-10-21T10:05:00Z,8"));
        source.Start();
        source.Track([new TrackedEntity(EntityId.Parse("sensor.a"), "A")]);
        Drain();
        _time.Advance(TimeSpan.FromMinutes(2));

        source.RequestProbe(EntityId.Parse("sensor.a"));

        var update = Batches().Single(b => b.Origin == BatchOrigin.Probe).Updates.Single();
        Assert.Equal(("7", T0, T0.AddMinutes(2)), (update.State, update.LastUpdated, update.Reported));
    }

    [Fact]
    public void A_row_marked_last_reported_models_a_frozen_sensor()
    {
        var data = ReplayData.Parse(new StringReader(
            "entity_id,ts_utc,state,last_reported\nsensor.a,2026-10-21T10:00:00Z,7,2026-10-21T10:00:00Z\nsensor.a,2026-10-21T10:05:00Z,8,\n"), "test");
        var source = Source(data);
        source.Start();
        source.Track([new TrackedEntity(EntityId.Parse("sensor.a"), "A")]);
        Drain();
        _time.Advance(TimeSpan.FromMinutes(2));

        source.RequestProbe(EntityId.Parse("sensor.a"));

        Assert.Equal(T0, Batches().Single(b => b.Origin == BatchOrigin.Probe).Updates.Single().Reported);
    }

    [Fact]
    public void End_of_data_lets_signals_go_stale()
    {
        var harness = new ReplayHarness(ReplayFixtures.SyntheticData, TestConfigurations.Example);
        var end = ReplayFixtures.SyntheticData.End;

        harness.RunUntil(end + TimeSpan.FromSeconds(89));
        Assert.True(harness.Current.Snapshot.Protection.IsDataValid(Phase.L1));

        harness.RunUntil(end + TimeSpan.FromSeconds(91));
        Assert.All(harness.Current.Snapshot.Protection.Phases, p => Assert.Equal(ProtectionInvalidReason.CurrentStale, p.InvalidReason));
    }

    [Fact]
    public void Speed_scales_now_and_timers()
    {
        var replay = new ReplayTimeProvider(_time, T0.AddDays(-3), speed: 10);
        var fired = 0;
        using var timer = replay.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);

        _time.Advance(TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1));
        Assert.Equal(0, fired);
        _time.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(1, fired);
        Assert.Equal(T0.AddDays(-3).AddSeconds(30), replay.GetUtcNow());
    }

    [Theory]
    [InlineData("entity,ts,state\n")]
    [InlineData("entity_id,ts_utc,state\nnot an entity,2026-10-21T10:00:00Z,1\n")]
    [InlineData("entity_id,ts_utc,state\nsensor.a,yesterday,1\n")]
    [InlineData("entity_id,ts_utc,state\n")]
    public void Malformed_recordings_fail_loudly(string text)
    {
        Assert.Throws<FormatException>(() => ReplayData.Parse(new StringReader(text), "test"));
    }

    private ReplaySignalSource Source(ReplayData data) => new(data, _inbox, new SignalClock(_time), NullLogger<ReplaySignalSource>.Instance);

    private static ReplayData Csv(params string[] lines) =>
        ReplayData.Parse(new StringReader("entity_id,ts_utc,state\n" + string.Join('\n', lines) + "\n"), "test");

    private void Drain()
    {
        while (_inbox.Reader.TryRead(out _))
        {
        }
    }

    private List<EntityUpdateBatch> Batches()
    {
        var batches = new List<EntityUpdateBatch>();
        while (_inbox.Reader.TryRead(out var input))
        {
            if (input is BatchInput batch)
            {
                batches.Add(batch.Batch);
            }
        }

        return batches;
    }
}
