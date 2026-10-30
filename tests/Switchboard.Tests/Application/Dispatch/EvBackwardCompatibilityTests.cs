using System.Text;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// EV-01 and EV-11 acceptance, slice S1: a file without <c>ev:</c> decides byte for byte as before, and so does one with it. The digests of
/// <see cref="EconomicsBackwardCompatibilityTests"/> and <see cref="PlannerBackwardCompatibilityTests"/> pin the plain file to the 0.9
/// engine; here the same synthetic morning is run with an <c>ev:</c> section on the charger (its SoC entity never reports) and with
/// the section next to <c>planner: dispatch: full</c>, and every digest must equal the plain one. In
/// this slice the section is shown and its parameters are bound, nothing more: a SoC signal that never arrives must not hold a device back,
/// move a decision or enter a safe state.
/// </summary>
public sealed class EvBackwardCompatibilityTests(DispatchSyntheticReplayRun run) : IClassFixture<DispatchSyntheticReplayRun>
{
    private const string ChargerLine = "    value_per_kwh: 2.03\n";

    private const string Ev = "\nev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level, max_age: 30m }\n";

    private static string Digest(DispatchSyntheticReplayRun run, string yaml)
    {
        var harness = new GuardReplayHarness(run.Data, yaml, dispatch: true);
        harness.RunUntil(run.Data.End);
        var text = new StringBuilder();
        foreach (var step in harness.Steps)
        {
            EconomicsBackwardCompatibilityTests.Add(text, step.Decisions, step.Dispatch);
        }

        return EconomicsBackwardCompatibilityTests.Sha(text);
    }

    [Fact]
    public void An_ev_section_with_a_soc_that_never_arrives_decides_the_synthetic_morning_exactly_as_the_plain_file_does()
    {
        // Why: the slice's promise. Each file parses as valid with the new section, and the morning's every decision, wish, device state and
        // balance view is the same as without it. If the section or its parameters reached the guard or dispatch, or the SoC signal that
        // never reports held anything back, this digest would move.
        var plain = Digest(run, DispatchSyntheticReplayRun.HeadroomConfiguration);
        var baseYaml = DispatchSyntheticReplayRun.HeadroomConfiguration;
        Assert.Contains(ChargerLine, baseYaml, StringComparison.Ordinal);

        var withEv = baseYaml.TrimEnd() + "\n" + Ev;
        var withPlanner = baseYaml.TrimEnd() + "\n" + Ev + "\nplanner:\n  dispatch: full\n";
        foreach (var yaml in new[] { withEv, withPlanner })
        {
            var parsed = TestConfigurations.Valid(yaml).Configuration!;
            Assert.NotNull(parsed.Ev);
            Assert.Contains(parsed.Signals, s => s.Name == parsed.Ev!.Soc);
        }

        Assert.Equal(plain, Digest(run, withEv));
        Assert.Equal(plain, Digest(run, withPlanner));
    }

    private static readonly EntityId SocEntity = EntityId.Parse("sensor.car_battery_level");

    /// <summary>
    /// The morning with the SoC entity reporting for real: a fresh value, silence until it is stale, then unavailable, text no number can be
    /// read from, and a value no percentage can be. Returns the digest and the state the SoC signal was in after each phase.
    /// </summary>
    private static (string Digest, List<SignalState> States) DigestWithReportingSoc(DispatchSyntheticReplayRun run, string yaml, bool report)
    {
        var harness = new GuardReplayHarness(run.Data, yaml, dispatch: true);
        var name = TestConfigurations.Valid(yaml).Configuration!.Ev?.Soc;
        var states = new List<SignalState>();
        var t = run.Data.Start;

        void Phase(TimeSpan length, string? report1)
        {
            if (report && report1 is not null)
            {
                harness.Replay.Source.Inject(SocEntity, report1);
            }

            t += length;
            harness.RunUntil(t);
            if (name is { } soc)
            {
                states.Add(harness.Replay.Current.Snapshot.Signals.Single(r => r.Name == soc).State);
            }
        }

        Phase(TimeSpan.FromMinutes(10), "55");
        Phase(TimeSpan.FromMinutes(5), null);
        Phase(TimeSpan.FromMinutes(50), null);
        Phase(TimeSpan.FromMinutes(5), "unavailable");
        Phase(TimeSpan.FromMinutes(5), "not_a_number");
        Phase(TimeSpan.FromMinutes(5), "250");
        Phase(TimeSpan.FromMinutes(5), "60");
        harness.RunUntil(run.Data.End);

        var text = new StringBuilder();
        foreach (var step in harness.Steps)
        {
            EconomicsBackwardCompatibilityTests.Add(text, step.Decisions, step.Dispatch);
        }

        return (EconomicsBackwardCompatibilityTests.Sha(text), states);
    }

    [Fact]
    public void A_soc_entity_that_reports_goes_silent_unavailable_and_unreadable_without_moving_one_decision()
    {
        // Why: the first test has a SoC that never reports. The signal must also be harmless while it lives: fresh, then silent (unavailable in a
        // replay), then refused as unreadable, then fresh again. The same segments run on the plain file (the entity as an ordinary signal) are the baseline; both runs must agree to the byte, and the
        // observed states prove the injected reports reached the signal (otherwise equality would prove nothing).
        var baseYaml = DispatchSyntheticReplayRun.HeadroomConfiguration;
        // The baseline reads the same entity as an ordinary signal: a report wakes the store whoever reads it, which adds a step to the digest;
        // what the ev: section may not add is anything beyond that.
        var probe = baseYaml.Replace("\nsignals:\n", "\nsignals:\n  soc_probe:\n    entity: sensor.car_battery_level\n    kind: percentage\n    unit: \"%\"\n    max_age: 30m\n", StringComparison.Ordinal);
        Assert.NotEqual(baseYaml, probe);
        var plain = DigestWithReportingSoc(run, probe, report: true);
        var withEv = DigestWithReportingSoc(run, baseYaml.TrimEnd() + "\n" + Ev, report: true);

        // The replay source answers a probe for an entity its recording lacks with "unavailable", so the silence after the first report shows
        // as unavailable here rather than as stale; the stale arm of the signal layer has its own tests (FreshnessTests).
        Assert.Empty(plain.States);
        Assert.Equal(7, withEv.States.Count);
        Assert.Equal(SignalState.Fresh, withEv.States[0]);
        Assert.All(withEv.States.Skip(1).Take(3), state => Assert.NotEqual(SignalState.Fresh, state));
        Assert.Equal(SignalState.Rejected, withEv.States[4]);
        Assert.Equal(SignalState.Fresh, withEv.States[6]);
        Assert.Equal(plain.Digest, withEv.Digest);
    }

    [Fact]
    public void A_control_ev_override_without_an_ev_section_decides_the_morning_exactly_as_the_plain_file_does()
    {
        // Why: control.ev is bound for every file but read by nothing; a file that only overrides it must not move a decision (and must load,
        // whatever the overrides say about each other: the cross-key checks apply only with an ev: section).
        var baseYaml = DispatchSyntheticReplayRun.HeadroomConfiguration;
        var plain = Digest(run, baseYaml);
        var overridden = baseYaml.TrimEnd() + "\n  ev:\n    cap: 80\n    floor: 20\n    spread: false\n    kwh_per_km: 0.9\n";

        var parsed = TestConfigurations.Valid(overridden).Configuration!;
        Assert.Null(parsed.Ev);
        Assert.Equal((80.0, 0.9), (parsed.Control.Ev.CapPercent, parsed.Control.Ev.FallbackKwhPerKm));
        Assert.Equal(plain, Digest(run, overridden));
    }

    [Fact]
    public void The_plain_morning_still_matches_the_pinned_09_digest_so_the_comparison_above_is_against_the_real_baseline()
    {
        // Why: equality with a drifted baseline would prove nothing. The plain file's digest is the one pinned to the 0.9 engine.
        EconomicsBackwardCompatibilityTests.Same(
            "37255EAC05B4E55FFF0AB687682A4FB3D1BD60B8D8D7728CB21AC9146B1628CC",
            Digest(run, DispatchSyntheticReplayRun.HeadroomConfiguration));
    }
}
