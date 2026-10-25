using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// CONN-05, passive and guided: the user switches a declared device in Home Assistant or by hand
/// while the wizard watches the meter (Switchboard switches nothing). Over the committed
/// tests/fixtures/replay/synthetic-phase-self-test.csv (make-synthetic-self-test.py): the swapped
/// L2/L3 case is caught naming the phase that moved, once a switch on and a switch off agree; one
/// switching alone is unconfirmed; a concurrent kettle, an open thermostat, another declared relay
/// switching in the window, a load under the noise and an unsigned current under export are
/// inconclusive, never a pass. The watch holds its own subscription for its whole timeout and judges
/// on its own timer, whether the page polls or not.
/// </summary>
public sealed class PhaseSelfTestFixtureTests : IDisposable
{
    private static readonly ReplayData Data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-phase-self-test.csv"));
    private static readonly DateTimeOffset Noon = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Noon);
    private readonly ReplayReadouts _readouts;
    private readonly PhaseEvidenceStore _evidence = new(20);
    private readonly PhaseSelfTestService _service;

    public PhaseSelfTestFixtureTests()
    {
        var units = new[] { "a", "b", "c" }
            .SelectMany(l => new[] { ($"sensor.selftest_power_{l}", "W"), ($"sensor.selftest_current_{l}", "A") })
            .ToDictionary(u => u.Item1, u => u.Item2);
        _readouts = new ReplayReadouts(Data, _time, units);
        _service = new PhaseSelfTestService(_readouts, _evidence, SetupFixtures.Defaults, () => 1, new SignalClock(_time), NullLogger<PhaseSelfTestService>.Instance);
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public void Swapped_L2_L3_is_caught()
    {
        // Declared on L2, the heater steps meter phase C (L3) by its 1.7 kW, up when switched on
        // (12:05:05) and down when switched off (12:10:05).
        At(12, 4);
        var started = _service.Start(Power("switch.selftest_heater", Phase.L2, 1700));

        At(12, 6, 10);
        var afterOn = _service.Get(started.Id)!;

        // One switching points at L3 but proves nothing yet: a coincident load looks the same.
        Assert.Equal(SelfTestStatus.Watching, afterOn.Status);
        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.Unconfirmed, Phase.L3), (afterOn.Summary.Verdict, afterOn.Summary.Reason, afterOn.Summary.Observed));
        Assert.InRange(Assert.Single(afterOn.Observations).Result.Deltas[Phase.L3], 1650, 1750);

        At(12, 11, 10);
        var view = _service.Get(started.Id)!;

        Assert.Equal(SelfTestStatus.Finished, view.Status);
        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L3), (view.Summary.Verdict, view.Summary.Observed));
        Assert.Equal([1, -1], view.Observations.Select(o => o.Observation.Direction));
        Assert.InRange(view.Observations[1].Result.Deltas[Phase.L3], -1750, -1650);
        Assert.Equal(2, _evidence.For(EntityId.Parse("switch.selftest_heater")).Length);
        Assert.Equal(0, _readouts.Holds);
    }

    [Fact]
    public void The_correct_declaration_is_confirmed()
    {
        At(12, 4);
        var started = _service.Start(Power("switch.selftest_heater", Phase.L3, 1700));
        At(12, 11, 10);

        var view = _service.Get(started.Id)!;

        Assert.Equal((SelfTestStatus.Finished, PhaseStepVerdict.Confirmed, 2), (view.Status, view.Summary.Verdict, view.Summary.Confirmed));
    }

    [Fact]
    public void Another_declared_relay_switching_in_the_window_is_not_isolated()
    {
        // cold2's thermostat is open (it draws nothing) while the declared neighbour relay starts
        // 1.7 kW on phase C 5 s later: without isolation that step would confirm cold2 on L3.
        var watch = Power("switch.selftest_cold2", Phase.L3, 1700) with { OtherSwitches = [EntityId.Parse("switch.selftest_neighbour")] };
        At(12, 34);
        var started = _service.Start(watch);
        At(12, 38, 20);

        var view = _service.Get(started.Id)!;

        Assert.Equal(SelfTestStatus.Watching, view.Status);
        Assert.Equal(2, view.Observations.Length);
        Assert.All(view.Observations, o => Assert.Equal(InconclusiveReason.NotIsolated, o.Result.Reason));
        Assert.Equal(PhaseStepVerdict.Inconclusive, view.Summary.Verdict);
    }

    [Fact]
    public void Another_relay_is_held_for_the_whole_watch()
    {
        // Its reports must keep arriving while the user is away switching: the session holds it.
        var readouts = new FakeReadouts(_time);
        var holds = new List<IReadOnlyCollection<EntityId>>();
        var tracking = new HoldRecorder(readouts, holds);
        using var tracked = new PhaseSelfTestService(tracking, new PhaseEvidenceStore(20), SetupFixtures.Defaults, () => 1, new SignalClock(_time), NullLogger<PhaseSelfTestService>.Instance);

        tracked.Start(Power("switch.selftest_cold2", Phase.L3, 1700) with { OtherSwitches = [EntityId.Parse("switch.selftest_neighbour")], OtherDraws = [new DrawTerm(EntityId.Parse("sensor.charger_power"), "W", 425)] });

        Assert.Contains(EntityId.Parse("switch.selftest_neighbour"), Assert.Single(holds));
        Assert.Contains(EntityId.Parse("sensor.charger_power"), holds[0]);
    }

    [Fact]
    public void Switchings_are_judged_on_the_session_timer_without_polling()
    {
        // The user leaves the panel to switch the heater in Home Assistant and nobody polls until the
        // watch is over. The bounded report history (64 meter reports here) has moved past the
        // switch-on by then; the session judged each switching itself as soon as its window passed.
        At(12, 4);
        var started = _service.Start(Power("switch.selftest_heater", Phase.L2, 1700));

        while (_time.GetUtcNow() < started.ExpiresAt + TimeSpan.FromMinutes(1))
        {
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        var view = _service.Get(started.Id)!;

        Assert.Equal((SelfTestStatus.Finished, PhaseStepVerdict.Mismatch, Phase.L3), (view.Status, view.Summary.Verdict, view.Summary.Observed));
        Assert.All(view.Observations, o => Assert.NotEqual(InconclusiveReason.MeterSilent, o.Result.Reason));
    }

    [Fact]
    public void A_switching_just_before_the_timeout_is_still_judged()
    {
        // Watch from 12:05:30 to 12:20:30; the heater is switched off at 12:20:05 and its window only
        // passes at 12:21:05. The session waits for it instead of expiring with it unjudged.
        At(12, 5, 30);
        var started = _service.Start(Power("switch.selftest_heater", Phase.L2, 1700));

        At(12, 21, 10);
        var view = _service.Get(started.Id)!;

        Assert.Equal(SelfTestStatus.Expired, view.Status);
        var last = view.Observations[^1];
        Assert.Equal((new DateTimeOffset(2026, 10, 22, 12, 20, 5, TimeSpan.Zero), PhaseStepVerdict.Mismatch), (last.Observation.At, last.Result.Verdict));
        Assert.Equal(0, _readouts.Holds);
    }

    [Fact]
    public void Concurrent_kettle_inconclusive()
    {
        At(12, 14);
        var started = _service.Start(Power("switch.selftest_heater", Phase.L3, 1700));
        At(12, 16, 10);

        var view = _service.Get(started.Id)!;

        Assert.Equal((SelfTestStatus.Watching, InconclusiveReason.ForeignStep), (view.Status, Assert.Single(view.Observations).Result.Reason));
        Assert.Equal(PhaseStepVerdict.Inconclusive, view.Summary.Verdict);
    }

    [Fact]
    public void Open_thermostat_inconclusive()
    {
        At(12, 24);
        var started = _service.Start(Power("switch.selftest_cold", Phase.L3, 1700));
        At(12, 28, 10);

        var view = _service.Get(started.Id)!;

        Assert.Equal(2, view.Observations.Length);
        Assert.All(view.Observations, o => Assert.Equal(InconclusiveReason.NoStep, o.Result.Reason));
        Assert.Equal(PhaseStepVerdict.Inconclusive, view.Summary.Verdict);
    }

    [Fact]
    public void A_load_under_the_noise_is_inconclusive()
    {
        At(12, 29);
        var started = _service.Start(Power("switch.selftest_small", Phase.L2, 150));
        At(12, 31, 10);

        Assert.Equal(InconclusiveReason.StepBelowNoise, Assert.Single(_service.Get(started.Id)!.Observations).Result.Reason);
    }

    [Fact]
    public void Unsigned_current_under_export_is_inconclusive()
    {
        // Only unsigned currents mapped: while phase C exports, the heater makes the magnitude fall.
        At(12, 44);
        var terms = Terms("current", "A", "unsigned");
        var started = _service.Start(new SelfTestRequest(EntityId.Parse("switch.selftest_heater"), Phase.L3, 7.4, "A", true, terms, 0.1));
        At(12, 46, 10);

        Assert.Equal(InconclusiveReason.WrongDirection, Assert.Single(_service.Get(started.Id)!.Observations).Result.Reason);
    }

    [Fact]
    public void Watch_expires_inconclusive()
    {
        At(12, 33);
        var started = _service.Start(Power("switch.selftest_cold", Phase.L3, 1700));

        _time.Advance(SetupFixtures.Defaults.SelfTest.WatchTimeout);
        var view = _service.Get(started.Id)!;

        Assert.Equal((SelfTestStatus.Expired, PhaseStepVerdict.Inconclusive, InconclusiveReason.TimedOut), (view.Status, view.Summary.Verdict, view.Summary.Reason));
    }

    [Fact]
    public void Watch_survives_without_spa_polling_for_its_whole_timeout()
    {
        // The user is in Home Assistant switching the device; nobody polls. The session's own hold
        // keeps the subscription for the whole timeout and is released by the session itself.
        At(12, 33);
        _service.Start(Power("switch.selftest_cold", Phase.L3, 1700));

        _time.Advance(SetupFixtures.Defaults.SelfTest.WatchTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _readouts.Holds);
        Assert.Equal(0, _readouts.Leases);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, _readouts.Holds);
    }

    [Fact]
    public void Only_one_session_at_a_time()
    {
        At(12, 4);
        var first = _service.Start(Power("switch.selftest_heater", Phase.L2, 1700));

        Assert.Throws<SelfTestBusyException>(() => _service.Start(Power("switch.selftest_small", Phase.L2, 150)));
        Assert.True(_service.Cancel(first.Id));
        Assert.Equal(SelfTestStatus.Cancelled, _service.Get(first.Id)!.Status);
        Assert.Equal(SelfTestStatus.Watching, _service.Start(Power("switch.selftest_small", Phase.L2, 150)).Status);
    }

    /// <summary>Readouts that record what each session holds.</summary>
    private sealed class HoldRecorder(FakeReadouts inner, List<IReadOnlyCollection<EntityId>> holds) : ICandidateReadouts
    {
        public ImmutableArray<CandidateReadout> Lease(IReadOnlyCollection<EntityId> entities) => inner.Lease(entities);

        public IDisposable Hold(IReadOnlyCollection<EntityId> entities)
        {
            holds.Add(entities);
            return inner.Hold(entities);
        }

        public CandidateReadout? Peek(EntityId entity) => inner.Peek(entity);

        public ImmutableArray<EntityUpdate> History(EntityId entity) => inner.History(entity);
    }

    private void At(int hour, int minute, int second = 0) =>
        _time.Advance(new DateTimeOffset(2026, 10, 22, hour, minute, second, TimeSpan.Zero) - _time.GetUtcNow());

    private static SelfTestRequest Power(string entity, Phase declared, double watts) =>
        new(EntityId.Parse(entity), declared, watts, "W", false, Terms("power", "W", "positive_import"), 25);

    private static ImmutableDictionary<Phase, MeterTerm> Terms(string kind, string unit, string sign) => new Dictionary<Phase, MeterTerm>
    {
        [Phase.L1] = new(EntityId.Parse($"sensor.selftest_{kind}_a"), unit, sign),
        [Phase.L2] = new(EntityId.Parse($"sensor.selftest_{kind}_b"), unit, sign),
        [Phase.L3] = new(EntityId.Parse($"sensor.selftest_{kind}_c"), unit, sign),
    }.ToImmutableDictionary();
}
