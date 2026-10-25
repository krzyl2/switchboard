using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Presentation;

/// <summary>
/// The Limits page shows a real recorded excursion: current over time around a crossing of the
/// reserve threshold, with the shed (or would-shed) annotated. These tests hold the recorder to
/// the guard's own comparison (signed current above In − reserve), to samples taken only on new
/// meter reports, to its bounds, and to keeping the worst excursion when routine ones follow.
/// </summary>
public sealed class ExcursionRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    private static readonly Amperes Threshold = new(14.13);

    [Fact]
    public void Starts_above_reserve_with_preroll_and_ends_after_postroll()
    {
        // Two minutes at 8 A, three minutes at 20 A, then back to 8 A: the excursion starts at the
        // first sample above 14.13 A, carries the two minutes before it, and ends after two calm
        // minutes, its end being the first sample back at or below the threshold.
        var recorder = new ExcursionRecorder(TestStatistics.Options());
        var at = Feed(recorder, Start, TimeSpan.FromMinutes(2), 8);
        var over = at;
        at = Feed(recorder, at, TimeSpan.FromMinutes(3), 20);
        var back = at;
        Feed(recorder, at, TimeSpan.FromMinutes(3), 8);

        var excursion = Last(recorder, Phase.L1)!;
        Assert.Equal(over, excursion.Start);
        Assert.Equal(back, excursion.End);
        Assert.False(excursion.Active);
        Assert.Equal(20, excursion.Peak.Value);
        Assert.True(excursion.Samples[0].At <= over - TimeSpan.FromMinutes(2) + Poll, $"pre-roll starts at {excursion.Samples[0].At}");
        Assert.True(recorder.TakeEnded());
    }

    [Fact]
    public void An_exporting_phase_above_the_reserve_threshold_is_no_excursion_unless_over_the_continuous_current()
    {
        // The guard compares the signed current: 20 A of export is no reserve crossing. 30 A of export
        // is above the continuous 28.25 A, and the breaker heats on export too.
        var recorder = new ExcursionRecorder(TestStatistics.Options());
        var at = Feed(recorder, Start, TimeSpan.FromMinutes(3), -20);
        Assert.Null(Last(recorder, Phase.L1));

        Feed(recorder, at, TimeSpan.FromMinutes(1), -30);
        Assert.NotNull(Last(recorder, Phase.L1));
    }

    [Fact]
    public void Only_new_reports_are_samples()
    {
        // Steps happen on every store cycle (a charger reports every 2.5 s); the chart shows meter
        // samples, so a step without a new phase-current report adds nothing.
        var recorder = new ExcursionRecorder(TestStatistics.Options());
        var at = Start;
        for (var i = 0; i < 12; i++)
        {
            recorder.Observe(PhaseStep(at, 20, reportedAt: Start));
            at += TimeSpan.FromSeconds(2.5);
        }

        Assert.Single(Last(recorder, Phase.L1)!.Samples);
    }

    [Fact]
    public void Attaches_the_phase_decisions_and_those_of_the_preroll()
    {
        // The foreign load event that started it (in the pre-roll), the reserve request and the
        // charger's would-reduction are on the chart; another phase's decision and a raise are not.
        var recorder = new ExcursionRecorder(TestStatistics.Options());
        var kettle = Start + TimeSpan.FromSeconds(50);
        var at = Start;
        for (var i = 0; i < 6; i++)
        {
            ImmutableArray<Decision> events = at == kettle ? [PhaseEvent(at, Phase.L1, DecisionAction.ForeignLoadDetected), PhaseEvent(at, Phase.L2, DecisionAction.ForeignLoadDetected)] : [];
            recorder.Observe(PhaseStep(at, 8, decisions: events));
            at += Poll;
        }

        var request = at;
        recorder.Observe(PhaseStep(at, 20, decisions: [PhaseEvent(at, Phase.L1, DecisionAction.RequestReduction)]));
        at += Poll;
        recorder.Observe(PhaseStep(at, 20, decisions: [
            TestSteps.Device(at, "ev_charger", DecisionAction.SetSetpoint, ReasonCode.ReserveThreshold, DeviceLevel.At(new Amperes(6)), origin: DecisionOrigin.Dispatch),
            TestSteps.Device(at, "heater_l2", DecisionAction.SwitchOff, ReasonCode.GuardTierExceeded, DeviceLevel.Relay(false)),
            TestSteps.Device(at, "heater_l1", DecisionAction.SwitchOn, ReasonCode.HeadroomAvailable, DeviceLevel.Relay(true), origin: DecisionOrigin.Dispatch),
        ]));

        var decisions = Last(recorder, Phase.L1)!.Decisions;
        Assert.Equal(
            [(kettle, DecisionAction.ForeignLoadDetected), (request, DecisionAction.RequestReduction), (at, DecisionAction.SetSetpoint)],
            decisions.Select(d => (d.At, d.Action)));
    }

    [Fact]
    public void Bounded_and_decimated_keeping_the_peak_and_every_decision_instant()
    {
        // A two-hour charging excursion at 10 s polls would hold 720+ samples: it is thinned to the
        // cap, the 31 A peak and the sample at the shed decision are never dropped.
        var recorder = new ExcursionRecorder(TestStatistics.Options(maxSamples: 100));
        var at = Start;
        var shedAt = Start + TimeSpan.FromMinutes(70);
        var peakAt = Start + TimeSpan.FromMinutes(40);
        while (at < Start + TimeSpan.FromHours(2))
        {
            var current = at == peakAt ? 31 : 16 + ((at - Start).TotalSeconds % 60 / 60);
            ImmutableArray<Decision> decisions = at == shedAt ? [TestSteps.Device(at, "heater_l1", DecisionAction.SwitchOff, ReasonCode.GuardTierExceeded, DeviceLevel.Relay(false))] : [];
            recorder.Observe(PhaseStep(at, current, decisions: decisions));
            at += Poll;
        }

        var excursion = Last(recorder, Phase.L1)!;
        Assert.True(excursion.Samples.Length <= 100, $"{excursion.Samples.Length} samples");
        Assert.True(excursion.Decimated);
        Assert.Equal(31, excursion.Peak.Value);
        Assert.Contains(excursion.Samples, s => s.At == peakAt && s.Current.Value == 31);
        Assert.Contains(excursion.Samples, s => s.At == shedAt);
    }

    [Fact]
    public void A_long_excursion_keeps_the_shed_at_its_peak_beyond_the_decision_cap()
    {
        // Live: a charging session at 16 A on L1 is one excursion for hours, and its reserve requests
        // fill the decision cap. A kettle later pushes it to 31 A and the guard sheds: that is the peak
        // the chart shows, so its shed must stay attached, even with newer decisions after it.
        var recorder = new ExcursionRecorder(TestStatistics.Options(maxDecisions: 10));
        var at = Start;
        var peakAt = Start + TimeSpan.FromMinutes(50);
        while (at < Start + TimeSpan.FromHours(1))
        {
            ImmutableArray<Decision> decisions = at == peakAt
                ? [TestSteps.Device(at, "heater_l1", DecisionAction.SwitchOff, ReasonCode.GuardTierExceeded, DeviceLevel.Relay(false))]
                : (at - Start).Seconds == 0 ? [PhaseEvent(at, Phase.L1, DecisionAction.RequestReduction)] : [];
            recorder.Observe(PhaseStep(at, at == peakAt ? 31 : 16, decisions: decisions));
            at += Poll;
        }

        var excursion = Last(recorder, Phase.L1)!;
        Assert.Equal(10, excursion.Decisions.Length);
        Assert.Equal(peakAt, excursion.PeakAt);
        Assert.Contains(excursion.Decisions, d => d.At == peakAt && d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.GuardTierExceeded);
        Assert.Equal(Start, excursion.Decisions[0].At);
        Assert.Equal(Start + TimeSpan.FromMinutes(59), excursion.Decisions[^1].At);
        Assert.Equal(excursion.Decisions.OrderBy(d => d.At).Select(d => d.Id), excursion.Decisions.Select(d => d.Id));
    }

    [Fact]
    public void A_later_long_lower_excursion_does_not_displace_an_earlier_higher_one()
    {
        // The 30 A excursion of the morning stays the one shown, however many routine charging
        // excursions at 16 A follow it.
        var recorder = new ExcursionRecorder(TestStatistics.Options());
        var at = Feed(recorder, Start, TimeSpan.FromMinutes(1), 30);
        at = Feed(recorder, at, TimeSpan.FromMinutes(5), 8);
        at = Feed(recorder, at, TimeSpan.FromHours(2), 16);

        var phase = recorder.Snapshot().Phases.Single(p => p.Phase == Phase.L1);
        Assert.Equal(30, phase.Worst!.Peak.Value);
        Assert.Equal(16, phase.Last!.Peak.Value, 1);
        Assert.True(phase.Last.Active);
    }

    [Fact]
    public void Every_phase_is_listed_before_any_excursion()
    {
        var recorder = new ExcursionRecorder(TestStatistics.Options());
        recorder.Observe(PhaseStep(Start, 3));

        Assert.Equal([Phase.L1], recorder.Snapshot().Phases.Select(p => p.Phase));
        Assert.Null(recorder.Snapshot().Phases[0].Last);
    }

    [Fact]
    public void A_restored_open_excursion_ends_with_the_run_that_recorded_it()
    {
        var recorder = new ExcursionRecorder(TestStatistics.Options());
        var at = Feed(recorder, Start, TimeSpan.FromMinutes(1), 20);
        var state = recorder.Snapshot().ToState(at);

        var restored = new ExcursionRecorder(TestStatistics.Options());
        restored.Restore(state, at + TimeSpan.FromMinutes(1));

        var excursion = restored.Snapshot().Phases.Single().Last!;
        Assert.False(excursion.Active);
        Assert.Equal(excursion.Samples[^1].At, excursion.End);
    }

    private static Excursion? Last(ExcursionRecorder recorder, Phase phase) =>
        recorder.Snapshot().Phases.FirstOrDefault(p => p.Phase == phase)?.Last;

    /// <summary>One meter sample per poll at <paramref name="current"/> for <paramref name="duration"/>; returns the next poll time.</summary>
    private static DateTimeOffset Feed(ExcursionRecorder recorder, DateTimeOffset from, TimeSpan duration, double current)
    {
        var at = from;
        for (; at < from + duration; at += Poll)
        {
            recorder.Observe(PhaseStep(at, current));
        }

        return at;
    }

    private static Decision PhaseEvent(DateTimeOffset at, Phase phase, DecisionAction action) =>
        new($"e-{Guid.NewGuid():N}"[..20], at, DecisionScope.Phase, null, null, phase, action, ReasonCode.ReserveThreshold, null, OperatingMode.Shadow, false, null, DecisionInputs.None);

    /// <summary>A step with L1 at <paramref name="current"/> A (signed), reported at <paramref name="reportedAt"/> (default: the step time).</summary>
    internal static GuardStep PhaseStep(DateTimeOffset at, double current, DateTimeOffset? reportedAt = null, ImmutableArray<Decision> decisions = default)
    {
        var mode = ModeState.Resolve(OperatingMode.Shadow, OperatingMode.Shadow);
        var phase = new PhaseGuardStatus(
            Phase.L1, true, null, new Amperes(current), new Amperes(current), Math.Abs(current) / 25, null, null,
            new Amperes(28.25), Threshold, Threshold - new Amperes(current), null, null, null,
            1, BudgetTrend.Full, TimeSpan.Zero, true, false, PhaseGuardState.Normal,
            current < 0 ? FlowDirection.Export : FlowDirection.Import, reportedAt ?? at);
        var status = new GuardStatus(at, mode, KillSwitchStatus.Off, SafeStateStatus.None, new BreakerStatus(new Amperes(25), BreakerCharacteristic.B, 1.13, new Amperes(28.25), []), [phase], []);
        var evaluation = new GuardEvaluation(at, status, [], [], null, 0) { Setup = TestSteps.Protection, ConfigurationValid = true };
        return new GuardStep(evaluation, decisions.IsDefault ? [] : decisions, GuardMemory.Fresh);
    }
}
