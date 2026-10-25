using Switchboard.Application.Setup;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// The recorded evidence of the passive self-test: the running add-on watches someone else switch a
/// declared relay (Node-RED in the maintainer's house) and keeps the per-phase step as a raw
/// observation, read from the same control steps the guard evaluates (a read-only listener). Over
/// the committed synthetic morning (synthetic-dispatch-day.csv): switch.grzalka_1, switched by
/// Node-RED at 07:40:05 and 07:46:05, steps L2 by its 1.7 kW; switch.grzalka_3 with its thermostat
/// open (08:10:05 / 08:16:05) steps nothing, which is inconclusive, never a pass.
/// </summary>
public sealed class PhaseEvidenceRecorderTests
{
    private static readonly Lazy<PhaseEvidenceStore> Recorded = new(Record);

    [Fact]
    public void Node_red_switching_of_grzalka_1_confirms_L2()
    {
        var observations = Recorded.Value.For(EntityId.Parse("switch.grzalka_1"));

        Assert.Equal(2, observations.Length);
        Assert.All(observations, o => Assert.Equal((ObservationSource.Recorded, "W", true), (o.Source, o.Unit, o.HouseLoad)));
        var judgement = Judge(observations, Phase.L2);
        Assert.Equal((PhaseStepVerdict.Confirmed, 2), (judgement.Summary.Verdict, judgement.Summary.Confirmed));
        Assert.Equal([1, -1], observations.Select(o => o.Direction));
    }

    [Fact]
    public void The_same_evidence_judged_against_L3_is_a_mismatch_naming_L2()
    {
        var judgement = Judge(Recorded.Value.For(EntityId.Parse("switch.grzalka_1")), Phase.L3);

        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L2), (judgement.Summary.Verdict, judgement.Summary.Observed));
    }

    [Fact]
    public void Grzalka_3_with_open_thermostat_is_inconclusive()
    {
        var judgement = Judge(Recorded.Value.For(EntityId.Parse("switch.grzalka_3")), Phase.L3);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.NoStep), (judgement.Summary.Verdict, judgement.Summary.Reason));
        Assert.Equal(2, judgement.Observations.Length);
    }

    private static PhaseJudgement Judge(IEnumerable<PhaseObservation> observations, Phase declared) =>
        PhaseEvidence.Judge(observations, declared, 1700, 7, SetupValidator.MeterTerms(SetupFixtures.MaintainerDraft(), [Phase.L1, Phase.L2, Phase.L3], power: true), null, SetupFixtures.Defaults.SelfTest.Classifier);

    private static PhaseEvidenceStore Record()
    {
        var data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-dispatch-day.csv"));
        var harness = new GuardReplayHarness(data, TestConfigurations.Example);
        var store = new PhaseEvidenceStore(20);
        var recorder = new PhaseEvidenceRecorder(store, new DiagnosticsRuntimeView(harness.Replay.Holder), SetupFixtures.Defaults, 64);
        recorder.Attach(harness.Runner);
        harness.RunUntil(new DateTimeOffset(2026, 10, 22, 8, 20, 0, TimeSpan.Zero));
        return store;
    }
}
