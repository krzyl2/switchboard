using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Switchboard.Application.Balance;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// ECO-15: without an <c>economics:</c> section dispatch is exactly the 0.9 dispatch, to the byte. The proof is a digest of everything dispatch
/// decided (every decision as the decision log writes it, every wish, every device state with its pending reason and start rank, the balance
/// view) over the same worlds the 0.9 balance tests use, the synthetic replays and, when the recording is there, the 72 h live replay. The
/// expected digests were taken from the 0.9 dispatch engine (the engine files of the 0.9.0 commit, built in a scratch copy of this tree beside
/// everything else in it); a change of any of them means a decision of an unchanged configuration moved.
/// </summary>
public sealed class EconomicsBackwardCompatibilityTests(DispatchSyntheticReplayRun run) : IClassFixture<DispatchSyntheticReplayRun>
{
    // Digests of the 0.9 engine (see the class comment). To renew one deliberately, run the test, read the actual value from its message
    // and say in DECISIONS.md which decision of an unchanged configuration moved and why.
    private const string BalanceWorlds = "B604FE86D71EAF34922B01FACC2909A1DB3EF0DDAF849B48F5EA23D831484135";
    private const string SyntheticShadow = "1A552444141477480FE599C63E4E003FB05E74BB5CF2E6E4476DC641DD9CE446";
    private const string SyntheticActive = "4D43F2F6850768E237934ABEA403D70E7356B5FBBECF27C4DA26DB879BB0E375";

    private static readonly DateTimeOffset T0 = GuardScenario.Start;

    internal static string F(double? value) => value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "-";

    internal static string Level(DeviceLevel? level) =>
        level is null ? "-" : $"{string.Join(',', (level.Relays.IsDefault ? [] : level.Relays).Select(r => r ? 1 : 0))}|{F(level.Setpoint?.Value)}|{level.Enabled}";

    internal static void Add(StringBuilder text, IEnumerable<Switchboard.Application.Decisions.Decision> decisions, DispatchEvaluation? dispatch)
    {
        foreach (var decision in decisions)
        {
            // The digests are of what dispatch decided, not of the list version the log line is stamped with: version 7 (the planner codes) changed that one
            // number and no decision, so a line is read as the version the digests were taken at (6, the economics list) and anything else that moved still shows.
            text.Append(DecisionJson.Serialize(decision).Replace("\"codes\":7", "\"codes\":6", StringComparison.Ordinal)).Append('\n');
        }

        if (dispatch is null)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"@{dispatch.At:O} ranking {string.Join(',', dispatch.Ranking)}\n");
        foreach (var w in dispatch.Wishes)
        {
            text.Append(CultureInfo.InvariantCulture, $"wish {w.Device} {Level(w.Level)} {w.Reason} {w.Origin} {w.Change} {w.BlockedBy} {w.Fresh} {Level(w.PendingLevel)}\n");
            var d = w.Inputs;
            text.Append(CultureInfo.InvariantCulture, $"  inputs {string.Join(';', d.Headroom.Select(h => $"{h.Key}:{F(h.Envelope?.Value)}:{F(h.Remaining?.Value)}"))} {F(d.StartHysteresis.Value)} {F(d.StopHysteresis.Value)} {d.StartRank} {d.Settling} {d.Override} {d.Reasserted}\n");
        }

        foreach (var s in dispatch.Devices)
        {
            text.Append(CultureInfo.InvariantCulture, $"state {s.Device} {s.Reason} {s.Pending} {s.StartRank} {Level(s.Wish)}\n");
        }

        foreach (var e in dispatch.Events)
        {
            text.Append(CultureInfo.InvariantCulture, $"event {e.Scope} {e.Phase} {e.Action} {e.Reason}\n");
        }

        if (dispatch.Balance is { } b)
        {
            text.Append(CultureInfo.InvariantCulture, $"balance {F(b.BalanceWh)} {F(b.Free?.Value)} {F(b.Load?.Value)} {b.UntilNext.TotalSeconds} {F(b.ProjectionWh)} {F(b.Band?.LoWh)} {F(b.Band?.HiWh)}\n");
        }
    }

    /// <summary>The digest must be the 0.9 one; a mismatch says both in full so it can be inspected or, deliberately, renewed.</summary>
    internal static void Same(string expected, string actual) =>
        Assert.True(expected == actual, $"the dispatch of an unchanged configuration moved: expected digest {expected}, got {actual}");

    internal static string Sha(StringBuilder text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

    private static Action Feed(GuardScenario s, double? wh, DateTimeOffset hourStart) => () =>
    {
        s.Energy = new EnergyInputs(new EnergyCounters(0, 0, s.Now), TimeSpan.FromSeconds(90));
        s.Balance = new HourlyBalanceStatus(
            new BalanceReading(wh, hourStart, wh is null ? BalanceUnknown.Stale : BalanceUnknown.None),
            s.Now,
            hourStart.AddHours(1) - s.Now);
    };

    private static void Export(GuardScenario s, double watts)
    {
        foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
        {
            s.SetLoad(phase, -watts / 3);
        }
    }

    /// <summary>The worlds of HourlyBalanceGateTests, systematically: devices x balance x export x hour x counters mapped or not (with a plan view, PlannerDispatchDigestTests runs them beside a plan).</summary>
    internal static string BalanceWorldsDigest(Switchboard.Application.Planning.IPlanView? plan = null)
    {
        var text = new StringBuilder();
        const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n    persistence_down_cycles: 1\n";
        var sets = new (string Name, string Devices, string[] Off)[]
        {
            ("heaters off", DispatchDevices.Heaters, ["heater_l1", "heater_l2", "heater_l3"]),
            ("one heater on", DispatchDevices.Heaters, ["heater_l2", "heater_l3"]),
            ("heater and pool", DispatchDevices.HeaterAndPool, ["heater_l1", "pool"]),
        };
        foreach (var (name, devices, off) in sets)
        {
            foreach (double? wh in new double?[] { null, -50, 100, 300, 600 })
            {
                foreach (var export in new[] { 0.0, 1500, 3000, 6000 })
                {
                    foreach (var hourOffset in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(-50) })
                    {
                        foreach (var counters in new[] { true, false })
                        {
                            var s = DispatchScenario.Create(devices, off, OneBoundary, plan: plan);
                            Export(s, export);
                            var feed = counters ? Feed(s, wh, T0 + hourOffset) : null;
                            feed?.Invoke();
                            s.PollUntil(T0 + TimeSpan.FromMinutes(4), 5, 5, 5, feed);
                            text.Append(CultureInfo.InvariantCulture, $"## {name} wh={F(wh)} export={export} hour={hourOffset} counters={counters}\n");
                            for (var i = 0; i < s.Dispatches.Count; i++)
                            {
                                Add(text, [], s.Dispatches[i]);
                            }

                            Add(text, s.Decisions, null);
                        }
                    }
                }
            }
        }

        // The command path: a heater started for the balance and stopped through its lockout when the balance goes away.
        var port = new RecordingDeviceCommandPort();
        var active = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1", "heater_l2", "heater_l3"], OneBoundary, mode: DispatchScenario.Active, port: port, plan: plan);
        active.Follow(port);
        Export(active, 3000);
        var on = Feed(active, 600, T0);
        on();
        active.PollUntil(T0 + TimeSpan.FromMinutes(2), 5, 5, 5, on);
        var gone = Feed(active, null, T0);
        gone();
        active.PollUntil(T0 + TimeSpan.FromMinutes(12), 5, 5, 5, gone);
        text.Append("## active\n").Append(string.Join('\n', port.Commands)).Append('\n');
        foreach (var dispatch in active.Dispatches)
        {
            Add(text, [], dispatch);
        }

        Add(text, active.Decisions, null);
        return Sha(text);
    }

    private static string RunDigest(DispatchSyntheticReplayRun run, bool active)
    {
        var text = new StringBuilder();
        var harness = active ? run.Active : run.Shadow;
        foreach (var step in harness.Steps)
        {
            Add(text, step.Decisions, step.Dispatch);
        }

        return Sha(text);
    }

    [Fact]
    public void The_balance_worlds_decide_byte_for_byte_as_the_0_9_dispatch_did()
    {
        // Why: ECO-15 and the maintainer's installed base. Every 0.9 file (no economics: section, devices with or without the balance counters)
        // must decide the same things in the same words after this slice, or "shadow, then enable" would not mean what the shadow week showed.
        Same(BalanceWorlds, BalanceWorldsDigest());
    }

    [Fact]
    public void The_synthetic_morning_decides_byte_for_byte_as_the_0_9_dispatch_did_in_shadow_and_active()
    {
        // Why: the same proof on the committed synthetic morning (kettles, foreign switching, a lost sample), where the guard, the effector and
        // dispatch interact; both the shadow pass and the active pass through the recording command port.
        Same(SyntheticShadow, RunDigest(run, active: false));
        Same(SyntheticActive, RunDigest(run, active: true));
    }
}

[Trait("Category", "LiveReplay")]
public sealed class EconomicsLiveBackwardCompatibilityTests
{
    private const string Live = "DB500B213B67B94C712727563568DE19776467F0B785F917747AC5E551F282B6";

    [Fact]
    public void The_live_72h_decides_byte_for_byte_as_the_0_9_dispatch_did()
    {
        // Why: the recording is the maintainer's real household; this is the strongest ECO-15 evidence there is. Fails loudly when the recording
        // is absent, like every live replay test.
        var data = ReplayFixtures.LiveData;
        var harness = new GuardReplayHarness(data, DispatchLiveReplayTests.Stage3Example, dispatch: true);
        harness.RunUntil(data.End);
        var text = new StringBuilder();
        foreach (var step in harness.Steps)
        {
            EconomicsBackwardCompatibilityTests.Add(text, step.Decisions, step.Dispatch);
        }

        EconomicsBackwardCompatibilityTests.Same(Live, EconomicsBackwardCompatibilityTests.Sha(text));
    }
}
