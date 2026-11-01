using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Application.Planning;

/// <summary>
/// A scripted day for the planner's loop, recorded before the service was split into triggers, solver and adoption: every step names what changed in the world
/// and the transcript line records how long the loop says to wait, why it planned (or that it did not) and the content hash of the plan in force. The
/// script touches every trigger, every field a goal's change is read from and every field it is not (a margin, sources, spread and the remaining energy of a
/// goal that stays unmet are no trigger), the hysteresis, the hold of a running entry, a suspension and the clock steps. Why it matters: a trigger that
/// fired for a field it should ignore is a trigger storm, one that missed a field leaves the user's change unplanned, and a plan adopted differently is a
/// different command downstream.
/// </summary>
public sealed class PlannerServiceTranscriptTests
{
    private static readonly DateTimeOffset Start = Local(2026, 10, 27, 11, 50).ToUniversalTime();
    private static readonly DateTimeOffset Deadline = Local(2026, 10, 28, 7);

    private sealed class World : IPlannerSource
    {
        public double Remaining { get; set; } = 6;

        public long GoalRevision { get; set; } = 1;

        public long ConfigRevision { get; set; } = 1;

        public double PvScale { get; set; } = 1;

        public string Digest { get; set; } = "f1";

        public string Fingerprint { get; set; } = "ev+";

        public bool GoalActive { get; set; } = true;

        public bool Hard { get; set; } = true;

        public GoalOnMet OnMet { get; set; } = GoalOnMet.Opportunistic;

        public TimeSpan Margin { get; set; } = TimeSpan.FromMinutes(30);

        public bool Spread { get; set; }

        public SourceSet Sources { get; set; } = Offpeak;

        public DateTimeOffset GoalDeadline { get; set; } = Deadline;

        public double Tolerance { get; set; } = 100;

        public PlannerCapture? Capture(DateTimeOffset now)
        {
            var inputs = Inputs(now, TuesdayPv(PvScale), [GoalDeadline - Margin]);
            var request = new GoalRequest(Car, new SocTarget(90, 40), Remaining, GoalDeadline, Margin, Hard, Sources, true, OnMet, GoalOrigin.Config, GoalRevision, Spread);
            GoalRequest[] goals = GoalActive ? [request] : [];
            var input = Input(now, inputs, [Charger(Offpeak)], goals, Capacity(), Parameters() with { ProgressToleranceFraction = Tolerance }, 0.05, ConfigRevision);
            ImmutableArray<InactiveGoal> inactive = GoalActive ? [] : [new InactiveGoal(Car, GoalInactiveReason.WantOff, GoalInputKind.Want, GoalOrigin.Entity, GoalKind.Soc, true)];
            return new PlannerCapture(input, inactive, Digest, Fingerprint);
        }
    }

    private sealed class Suspension : IPlannerSuspension
    {
        public bool Suspended { get; private set; }

        public event EventHandler? Changed;

        public void Set(bool value)
        {
            Suspended = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [Fact]
    public async Task The_scripted_day_gives_the_transcript_recorded_before_the_split()
    {
        // Why: see the class. Each line is hashed, so a failure names the step.
        var lines = await Run();

        Assert.Equal(PlannerServiceTranscript.Lines.Length, lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            Assert.True(PlannerServiceTranscript.Lines[i] == lines[i], $"step {i}: {lines[i]} (recorded {PlannerServiceTranscript.Lines[i]})");
        }
    }

    [Fact]
    public async Task A_field_the_signature_does_not_read_is_no_trigger_and_one_it_reads_is()
    {
        // Why: a goal's change is read from its revision, deadline, hardness, on_met and whether it is met, not from every field: the remaining energy of an
        // unmet goal moves every cycle (progress has its own trigger) and must not replan on its own.
        var world = new World();
        var time = new SteppableTime(Start) { Zone = Warsaw };
        var holder = new PlanHolder();
        var service = new PlannerService(world, new Suspension(), holder, time, new LogCapture().For<PlannerService>());
        await service.StepAsync(TestContext.Current.CancellationToken);
        time.Advance(Parameters().StartDelay);
        await service.StepAsync(TestContext.Current.CancellationToken);
        var first = holder.Current.Plan!.Id;

        async Task<bool> Replanned(Action change)
        {
            var before = holder.Current.LastSolve!.At;
            change();
            time.Advance(TimeSpan.FromSeconds(61));
            await service.StepAsync(TestContext.Current.CancellationToken);
            return holder.Current.LastSolve!.At != before;
        }

        Assert.False(await Replanned(() => world.Margin = TimeSpan.FromMinutes(20)));
        Assert.False(await Replanned(() => world.Spread = true));
        Assert.False(await Replanned(() => world.Sources = SourceSet.Any));
        Assert.False(await Replanned(() => world.Remaining = 5.5));
        Assert.True(await Replanned(() => world.GoalRevision = 2));
        Assert.True(await Replanned(() => world.Hard = false));
        Assert.True(await Replanned(() => world.OnMet = GoalOnMet.Stop));
        Assert.True(await Replanned(() => world.GoalDeadline = Deadline.AddHours(1)));
        Assert.True(await Replanned(() => world.Remaining = 0));
        Assert.NotEqual(first, holder.Current.Plan!.Id);
    }

    private static async Task<List<string>> Run()
    {
        var world = new World();
        var suspension = new Suspension();
        var time = new SteppableTime(Start) { Zone = Warsaw };
        var holder = new PlanHolder();
        var service = new PlannerService(world, suspension, holder, time, new LogCapture().For<PlannerService>());
        var lines = new List<string>();

        async Task Step(string name, TimeSpan advance, Action? change = null)
        {
            change?.Invoke();
            if (advance < TimeSpan.Zero)
            {
                time.StepBack(-advance);
            }
            else
            {
                time.Advance(advance);
            }

            var wait = await service.StepAsync(TestContext.Current.CancellationToken);
            var state = holder.Current;
            var plan = state.Plan;
            var entry = plan?.EntryAt(Car, time.GetUtcNow());
            var entryText = entry is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{entry.Want}/{(entry.Class is { } c ? EnergySourceText.Plan(c) : "-")}/{entry.EnergyKwh:R}/{entry.Goal?.Revision}");
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{name} wait={wait.TotalSeconds:0.###} reason={state.LastSolve?.Reason ?? "-"} at={state.LastSolve?.At.UtcTicks} result={state.LastSolve?.Result} susp={state.Suspended} notes={string.Join(',', state.Notes)} plan={plan?.Id ?? "-"} computed={plan?.ComputedAt.UtcTicks} entry={entryText}"));
        }

        var minute = TimeSpan.FromSeconds(61);
        await Step("boot", TimeSpan.Zero);
        await Step("start_delay", Parameters().StartDelay);
        await Step("quiet", TimeSpan.FromSeconds(5));
        await Step("margin", minute, () => world.Margin = TimeSpan.FromMinutes(20));
        await Step("spread", minute, () => world.Spread = true);
        await Step("sources", minute, () => world.Sources = SourceSet.Any);
        await Step("remaining_unmet", minute, () => world.Remaining = 5.5);
        await Step("forecast_digest", minute, () => world.Digest = "f2");
        await Step("cadence", TimeSpan.FromMinutes(15));
        await Step("jitter_3pct", TimeSpan.FromMinutes(15), () => world.PvScale = 1.03);
        await Step("change_15pct_first", TimeSpan.FromMinutes(15), () => world.PvScale = 1.15);
        await Step("change_15pct_second", TimeSpan.FromMinutes(15));
        await Step("pv_collapse_1", TimeSpan.FromMinutes(15), () => world.PvScale = 0);
        await Step("pv_collapse_2", TimeSpan.FromMinutes(15));
        await Step("pv_collapse_3", TimeSpan.FromMinutes(15));
        await Step("pv_back", TimeSpan.FromMinutes(15), () => world.PvScale = 1);
        await Step("revision", minute, () => world.GoalRevision = 2);
        await Step("revision_burst", TimeSpan.FromSeconds(10), () => world.GoalRevision = 3);
        await Step("revision_burst_due", TimeSpan.FromSeconds(51));
        await Step("hard_off", minute, () => world.Hard = false);
        await Step("on_met_stop", minute, () => world.OnMet = GoalOnMet.Stop);
        await Step("deadline", minute, () => world.GoalDeadline = Deadline.AddHours(2));
        await Step("fingerprint", minute, () => world.Fingerprint = "ev-");
        await Step("progress", minute, () =>
        {
            world.Tolerance = 0.10;
            world.Remaining = 2;
        });
        await Step("progress_settled", minute, () => world.Tolerance = 100);
        await Step("device_inactive", minute, () => world.GoalActive = false);
        await Step("device_active", minute, () => world.GoalActive = true);
        await Step("met", minute, () => world.Remaining = 0);
        await Step("unmet_again", minute, () => world.Remaining = 4);
        await Step("config", TimeSpan.FromSeconds(5), () => world.ConfigRevision = 2);
        await Step("suspend", TimeSpan.FromSeconds(5), () => suspension.Set(true));
        await Step("suspended_goal", minute, () => world.GoalRevision = 4);
        await Step("resume", TimeSpan.FromSeconds(5), () => suspension.Set(false));
        await Step("clock_step_forward", TimeSpan.FromHours(2));
        await Step("clock_step_back", TimeSpan.FromHours(-1));
        await Step("cadence_after_back", TimeSpan.FromMinutes(16));
        await Step("next_slot", TimeSpan.FromMinutes(16));
        return lines;
    }
}

internal static class PlannerServiceTranscript
{
    public static readonly string[] Lines =
    [
        "boot wait=60 reason=- at= result= susp=False notes= plan=- computed= entry=-",
        "start_delay wait=540 reason=first at=639286950600000000 result=Computed susp=False notes= plan=508d6fd2730cda40 computed=639286950600000000 entry=Run/pv/0.35325/1",
        "quiet wait=535 reason=first at=639286950600000000 result=Computed susp=False notes= plan=508d6fd2730cda40 computed=639286950600000000 entry=Run/pv/0.35325/1",
        "margin wait=474 reason=first at=639286950600000000 result=Computed susp=False notes= plan=508d6fd2730cda40 computed=639286950600000000 entry=Run/pv/0.35325/1",
        "spread wait=413 reason=first at=639286950600000000 result=Computed susp=False notes= plan=508d6fd2730cda40 computed=639286950600000000 entry=Run/pv/0.35325/1",
        "sources wait=352 reason=first at=639286950600000000 result=Computed susp=False notes= plan=508d6fd2730cda40 computed=639286950600000000 entry=Run/pv/0.35325/1",
        "remaining_unmet wait=291 reason=first at=639286950600000000 result=Computed susp=False notes= plan=508d6fd2730cda40 computed=639286950600000000 entry=Run/pv/0.35325/1",
        "forecast_digest wait=230 reason=first at=639286950600000000 result=Computed susp=False notes= plan=508d6fd2730cda40 computed=639286950600000000 entry=Run/pv/0.35325/1",
        "cadence wait=230 reason=cadence at=639286962700000000 result=Computed susp=False notes= plan=4d0a0292314db6b4 computed=639286962700000000 entry=Run/pv/2.12425/1",
        "jitter_3pct wait=230 reason=cadence at=639286971700000000 result=Computed susp=False notes= plan=f6f5d073f0507ff9 computed=639286971700000000 entry=Run/pv/1.47175/1",
        "change_15pct_first wait=230 reason=cadence at=639286980700000000 result=Computed susp=False notes= plan=54e69227097c9cdc computed=639286980700000000 entry=Run/pv/0.8192499999999999/1",
        "change_15pct_second wait=230 reason=cadence at=639286989700000000 result=Computed susp=False notes= plan=b8b5af481246462d computed=639286989700000000 entry=Run/pv/0.16674999999999998/1",
        "pv_collapse_1 wait=230 reason=cadence at=639286998700000000 result=Computed susp=False notes= plan=b16d75d54d73ae9e computed=639286998700000000 entry=Run/pv/2.179594444444444/1",
        "pv_collapse_2 wait=230 reason=cadence at=639287007700000000 result=Computed susp=False notes= plan=c7cee1d299746f31 computed=639287007700000000 entry=Run/pv/1.5100944444444442/1",
        "pv_collapse_3 wait=230 reason=cadence at=639287016700000000 result=Computed susp=False notes= plan=c1970d911537289c computed=639287016700000000 entry=Run/pv/0.8405944444444443/1",
        "pv_back wait=230 reason=cadence at=639287025700000000 result=Computed susp=False notes= plan=a91b66c34f205bff computed=639287025700000000 entry=Run/pv/0.17109444444444444/1",
        "revision wait=169 reason=goal at=639287026310000000 result=Computed susp=False notes= plan=010d5a54899670cf computed=639287026310000000 entry=Run/pv/0.10656388888888887/2",
        "revision_burst wait=50 reason=goal at=639287026310000000 result=Computed susp=False notes= plan=010d5a54899670cf computed=639287026310000000 entry=Run/pv/0.10656388888888887/2",
        "revision_burst_due wait=108 reason=goal at=639287026920000000 result=Computed susp=False notes= plan=8d565a0ed39f979b computed=639287026920000000 entry=Run/pv/0.06810000000000001/3",
        "hard_off wait=47 reason=goal at=639287027530000000 result=Computed susp=False notes= plan=f52de3372a767cf4 computed=639287027530000000 entry=Run/pv/0.02963611111111111/3",
        "on_met_stop wait=886 reason=goal at=639287028140000000 result=Computed susp=False notes= plan=8a871b465af3bd2d computed=639287028140000000 entry=Run/pv/1.4991472222222224/3",
        "deadline wait=825 reason=goal at=639287028750000000 result=Computed susp=False notes= plan=c09872c09c283ef0 computed=639287028750000000 entry=Run/pv/1.473645833333333/3",
        "fingerprint wait=764 reason=device_state at=639287029360000000 result=Computed susp=False notes= plan=83dbae903321d9fc computed=639287029360000000 entry=Run/pv/1.4481444444444442/3",
        "progress wait=703 reason=progress at=639287029970000000 result=Computed susp=False notes= plan=ac88764c8a029ece computed=639287029970000000 entry=Run/pv/1.4226430555555554/3",
        "progress_settled wait=642 reason=progress at=639287029970000000 result=Computed susp=False notes= plan=ac88764c8a029ece computed=639287029970000000 entry=Run/pv/1.4226430555555554/3",
        "device_inactive wait=581 reason=goal at=639287031190000000 result=Computed susp=False notes= plan=16b30d228ae4f5ca computed=639287031190000000 entry=None/-/0/",
        "device_active wait=520 reason=goal at=639287031800000000 result=Computed susp=False notes= plan=3fd086e95394e22a computed=639287031800000000 entry=Run/pv/1.3461388888888888/3",
        "met wait=459 reason=goal at=639287032410000000 result=Computed susp=False notes= plan=7cbf002dadca1d48 computed=639287032410000000 entry=Done/-/0/3",
        "unmet_again wait=398 reason=goal at=639287033020000000 result=Computed susp=False notes= plan=e758a0cf05d63285 computed=639287033020000000 entry=Run/pv/1.2951361111111108/3",
        "config wait=393 reason=config at=639287033070000000 result=Computed susp=False notes= plan=33014fa4a4cc69fb computed=639287033070000000 entry=Run/pv/1.2930458333333332/3",
        "suspend wait=-0.001 reason=config at=639287033070000000 result=Computed susp=True notes= plan=33014fa4a4cc69fb computed=639287033070000000 entry=Run/pv/1.2930458333333332/3",
        "suspended_goal wait=-0.001 reason=config at=639287033070000000 result=Computed susp=True notes= plan=33014fa4a4cc69fb computed=639287033070000000 entry=Run/pv/1.2930458333333332/3",
        "resume wait=322 reason=safe_state_exit at=639287033780000000 result=Computed susp=False notes= plan=7948b6aa805613c5 computed=639287033780000000 entry=Run/pv/1.2633638888888887/4",
        "clock_step_forward wait=322 reason=clock_step at=639287105780000000 result=Computed susp=False notes= plan=b143f442162d0d0c computed=639287105780000000 entry=Defer/-/0/4",
        "clock_step_back wait=322 reason=clock_step_back at=639287069780000000 result=Computed susp=False notes= plan=5e5853e6d70f8b72 computed=639287069780000000 entry=Run/pv/0.47848333333333337/4",
        "cadence_after_back wait=262 reason=cadence at=639287079380000000 result=Computed susp=False notes= plan=95a73226547089dd computed=639287079380000000 entry=Run/pv/0.32648333333333335/4",
        "next_slot wait=202 reason=cadence at=639287088980000000 result=Computed susp=False notes= plan=78ed021619d76c8d computed=639287088980000000 entry=Run/pv/0.17448333333333332/4",
    ];
}
