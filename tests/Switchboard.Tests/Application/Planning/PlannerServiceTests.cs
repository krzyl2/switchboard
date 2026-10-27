using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Application.Planning;

/// <summary>
/// The planner's own loop on a virtual clock (15-PLANNER, "Re-planning" and "Failure behaviour"). Why it matters: the service decides when a plan is made, whether
/// it is adopted and what happens when it fails. A plan that flapped from one cycle to the next, a trigger storm, a solver that could stall the host or a failure
/// that dropped the last good plan would each turn a harmless proposal into a nuisance (or, later, a wrong decision); and nothing here may ever reach the guard.
/// All time is the injected clock; no test waits on the wall clock except to cap a hang.
/// </summary>
public sealed class PlannerServiceTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Monday1800 = Local(2026, 10, 26, 18).ToUniversalTime();
    private static readonly DateTimeOffset Deadline = Local(2026, 10, 28, 7);

    /// <summary>What the planner sees: the worked example (car, hard goal, G13, Tuesday PV), changeable between plannings.</summary>
    private sealed class World : IPlannerSource
    {
        public bool Present { get; set; } = true;

        public double Remaining { get; set; } = 9.9;

        public long GoalRevision { get; set; } = 1;

        public long ConfigRevision { get; set; } = 1;

        public double PvScale { get; set; } = 1;

        public bool WithPv { get; set; } = true;

        public string ForecastDigest { get; set; } = "f1";

        public string DeviceFingerprint { get; set; } = "ev_charger+";

        public bool GoalActive { get; set; } = true;

        /// <summary>planner.progress_tolerance_fraction; the hysteresis tests widen it so that an idle world is not read as a car that failed to charge.</summary>
        public double Tolerance { get; set; } = 0.10;

        public int Captures { get; private set; }

        /// <summary>The source refuses to build inputs (a slot grid or a signal that throws).</summary>
        public bool Fail { get; set; }

        public PlannerCapture? Capture(DateTimeOffset now)
        {
            Captures++;
            if (Fail)
            {
                throw new ArgumentOutOfRangeException(nameof(now), "the inputs cannot be built");
            }

            if (!Present)
            {
                return null;
            }

            var local = now;
            var inputs = Inputs(local, WithPv ? TuesdayPv(PvScale) : PvForecast.None, [Deadline - TimeSpan.FromMinutes(30)]);
            var goals = GoalActive ? new[] { Goal(Car, Remaining, Deadline, margin: TimeSpan.FromMinutes(30), sources: Offpeak, revision: GoalRevision) } : [];
            var input = Input(local, inputs, [Charger(Offpeak)], goals, Capacity(), Parameters() with { ProgressToleranceFraction = Tolerance }, 0.05, ConfigRevision);
            ImmutableArray<InactiveGoal> inactive = GoalActive ? [] : [new InactiveGoal(Car, GoalInactiveReason.WantOff, GoalInputKind.Want, GoalOrigin.Entity, GoalKind.Soc, true)];
            return new PlannerCapture(input, inactive, ForecastDigest, DeviceFingerprint);
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

    private sealed class Rig
    {
        public Rig(Func<PlanInput, CancellationToken, PlanSnapshot>? solver = null)
        {
            Time = new FakeTimeProvider(Monday1800);
            Time.SetLocalTimeZone(Warsaw);
            Logs = new LogCapture();
            Service = new PlannerService(World, Suspension, Holder, Time, Logs.For<PlannerService>(), solver);
        }

        public FakeTimeProvider Time { get; }

        public World World { get; } = new();

        public Suspension Suspension { get; } = new();

        public PlanHolder Holder { get; } = new();

        public LogCapture Logs { get; }

        public PlannerService Service { get; }

        public PlanState State => Holder.Current;

        public Task<TimeSpan> Step() => Service.StepAsync(TestContext.Current.CancellationToken);

        /// <summary>Starts the service's clock (the first step) and moves past the start delay, then plans.</summary>
        public async Task FirstPlan()
        {
            await Step();
            Time.Advance(Parameters().StartDelay);
            await Step();
            Assert.NotNull(State.Plan);
        }

        public bool Logged(string text) => Logs.Entries.Any(e => e.Text.Contains(text, StringComparison.Ordinal));

        public PlanEntry? Entry() => State.Plan!.EntryAt(Car, Time.GetUtcNow());

        public string Show() => $"{Time.GetUtcNow():HH:mm:ss} computed {State.Plan!.ComputedAt:HH:mm:ss} {string.Join(" ", State.Plan.Device(Car)!.Entries.Take(4).Select(e => $"{e.Start:HH:mm}-{e.End:HH:mm} {e.Want}/{e.Class}/{e.EnergyKwh:0.###}"))} reason {State.LastSolve?.Reason}";
    }

    [Fact]
    public async Task No_plan_is_made_before_the_start_delay_and_the_first_one_comes_right_after()
    {
        // Why: after a start the inputs (forecast, SoC, prices) need time to arrive; a plan made from half of them would be wrong for a whole cycle. Until
        // planner.start_delay nothing is published and the status is absent; at the delay the first plan is made at once, not at the next quarter hour.
        var rig = new Rig();

        var wait = await rig.Step();

        Assert.Equal(Parameters().StartDelay, wait);
        Assert.Null(rig.State.Plan);
        Assert.Equal(PlanStatus.Absent, rig.State.StatusAt(rig.Time.GetUtcNow()));
        rig.Time.Advance(Parameters().StartDelay);
        await rig.Step();

        Assert.NotNull(rig.State.Plan);
        Assert.Equal(Monday1800.AddSeconds(60), rig.State.Plan!.ComputedAt);
        Assert.Equal(PlanStatus.Fresh, rig.State.StatusAt(rig.Time.GetUtcNow()));
        Assert.Equal("first", rig.State.LastSolve!.Reason);
        Assert.True(rig.Logged("plan_computed"));
        Assert.True(rig.Logged("plan_adopted"));
    }

    [Fact]
    public async Task Plans_are_made_on_the_quarter_hours_of_the_injected_clock()
    {
        // Why: the cadence is aligned to :00 :15 :30 :45 (the multiples of the cycle since the epoch), not to the moment of the previous plan: otherwise the
        // slot edges, the tariff changes and every other loop would drift against each other. The first plan was at 18:01; the next is due at 18:15 exactly.
        var rig = new Rig();
        await rig.FirstPlan();

        var wait = await rig.Step();

        Assert.Equal(TimeSpan.FromMinutes(14), wait);
        rig.Time.Advance(TimeSpan.FromMinutes(14) - TimeSpan.FromSeconds(1));
        await rig.Step();
        Assert.Equal(Monday1800.AddSeconds(60), rig.State.Plan!.ComputedAt);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Step();
        Assert.Equal(Monday1800.AddMinutes(15), rig.State.Plan.ComputedAt);
        Assert.Equal("cadence", rig.State.LastSolve!.Reason);
        Assert.Equal(TimeSpan.FromMinutes(15), await rig.Step());
    }

    [Fact]
    public async Task Without_a_planner_section_the_service_is_idle_and_touches_nothing()
    {
        // Why: no planner: section means v3 is off and the add-on is exactly 0.10.x: no plan, no holder content, no log line, no timer to wait for.
        var rig = new Rig();
        rig.World.Present = false;

        var wait = await rig.Step();
        rig.Time.Advance(TimeSpan.FromHours(2));
        await rig.Step();

        Assert.Equal(Timeout.InfiniteTimeSpan, wait);
        Assert.Equal(PlanState.Empty, rig.State);
        Assert.Empty(rig.Logs.Entries);
    }

    [Fact]
    public async Task Removing_the_planner_section_drops_the_plan()
    {
        // Why: "planner: removed on reload: v2 exactly". A plan held after the section is gone would be consumed (from slice 5 on) by a configuration that never
        // asked for it, so the holder is emptied at once and the drop is logged.
        var rig = new Rig();
        await rig.FirstPlan();

        rig.World.Present = false;
        await rig.Step();

        Assert.Equal(PlanState.Empty, rig.State);
        Assert.True(rig.Logged("plan_idle"));
    }

    [Fact]
    public async Task A_goal_change_replans_at_once_but_never_more_than_once_per_minimum_interval()
    {
        // Why: triggers are rate limited (planner.min_replan_interval, 60 s): a goal edited ten seconds after a plan waits for its turn rather than starting a
        // second solve, and a burst of changes inside the interval makes one plan, not many.
        var rig = new Rig();
        await rig.FirstPlan();
        var first = rig.State.Plan!.Id;

        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.World.GoalRevision = 2;
        var wait = await rig.Step();

        Assert.Equal(TimeSpan.FromSeconds(50), wait);
        Assert.Equal(first, rig.State.Plan!.Id);
        rig.World.GoalRevision = 3;
        rig.Time.Advance(TimeSpan.FromSeconds(50));
        await rig.Step();

        Assert.NotEqual(first, rig.State.Plan!.Id);
        Assert.Equal("goal", rig.State.LastSolve!.Reason);
        Assert.Equal(3, rig.State.Plan.Device(Car)!.Goals.Single().Ref.Revision);
        Assert.Equal(TimeSpan.FromMinutes(13), await rig.Step());
    }

    [Fact]
    public async Task A_configuration_reload_a_clock_step_and_leaving_safe_state_are_not_rate_limited()
    {
        // Why: the table of triggers: these three replan at once, whatever the last plan's age, because what the plan stands on has changed under it
        // (another configuration, another time, a guard that was out of the picture).
        var rig = new Rig();
        await rig.FirstPlan();

        rig.Time.Advance(TimeSpan.FromSeconds(5));
        rig.World.ConfigRevision = 2;
        await rig.Step();
        Assert.Equal("config", rig.State.LastSolve!.Reason);
        Assert.Equal(2, rig.State.Plan!.ConfigRevision);

        rig.Time.Advance(TimeSpan.FromSeconds(5));
        rig.Suspension.Set(true);
        await rig.Step();
        Assert.Equal(PlanStatus.Suspended, rig.State.StatusAt(rig.Time.GetUtcNow()));
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        rig.Suspension.Set(false);
        await rig.Step();
        Assert.Equal("safe_state_exit", rig.State.LastSolve!.Reason);
        Assert.Equal(PlanStatus.Fresh, rig.State.StatusAt(rig.Time.GetUtcNow()));

    }

    [Fact]
    public async Task A_clock_stepped_back_replans_at_once_and_a_plan_from_the_future_is_never_used()
    {
        // Why: an NTP step or a restored VM puts the plan's computed-at in the future: the plan is discarded as unusable (status stale) and a new one is made at
        // once, whatever the rate limit says, from the clock as it now reads.
        var time = new SteppableTime(Monday1800) { Zone = Warsaw };
        var world = new World();
        var holder = new PlanHolder();
        var logs = new LogCapture();
        var service = new PlannerService(world, new Suspension(), holder, time, logs.For<PlannerService>());
        await service.StepAsync(TestContext.Current.CancellationToken);
        time.Advance(Parameters().StartDelay);
        await service.StepAsync(TestContext.Current.CancellationToken);
        var first = holder.Current.Plan!;

        time.Advance(TimeSpan.FromSeconds(5));
        time.StepBack(TimeSpan.FromHours(3));
        Assert.Equal(PlanStatus.Stale, holder.Current.StatusAt(time.GetUtcNow()));
        await service.StepAsync(TestContext.Current.CancellationToken);

        Assert.Equal("clock_step_back", holder.Current.LastSolve!.Reason);
        Assert.Equal(time.GetUtcNow(), holder.Current.Plan!.ComputedAt);
        Assert.NotEqual(first.Id, holder.Current.Plan.Id);
        Assert.Equal(PlanStatus.Fresh, holder.Current.StatusAt(time.GetUtcNow()));
    }

    [Fact]
    public async Task A_forecast_change_waits_for_the_next_cycle_boundary()
    {
        // Why: forecasts refresh often and move a little; replanning on each would be a storm for nothing. The table says "next cycle boundary, not at once": a changed
        // digest at 18:05 does not plan at 18:06, the 18:15 plan has it.
        var rig = new Rig();
        await rig.FirstPlan();
        var first = rig.State.Plan!.Id;

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        rig.World.ForecastDigest = "f2";
        rig.World.PvScale = 1.2;
        var wait = await rig.Step();

        Assert.Equal(first, rig.State.Plan!.Id);
        Assert.Equal(TimeSpan.FromMinutes(9), wait);
        rig.Time.Advance(wait);
        await rig.Step();
        Assert.NotEqual(first, rig.State.Plan.Id);
        Assert.Equal("cadence", rig.State.LastSolve!.Reason);
    }

    [Fact]
    public async Task A_device_that_stops_answering_or_a_goal_that_becomes_inactive_is_a_trigger()
    {
        // Why: a car plugged out (the goal goes inactive) or a charger that stopped answering changes what can be planned; waiting up to a quarter of an hour would
        // plan for a device that is not there.
        var rig = new Rig();
        await rig.FirstPlan();

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        rig.World.DeviceFingerprint = "ev_charger-";
        await rig.Step();
        Assert.Equal("device_state", rig.State.LastSolve!.Reason);

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        rig.World.GoalActive = false;
        await rig.Step();
        Assert.Equal("goal", rig.State.LastSolve!.Reason);
        Assert.Empty(rig.State.Plan!.Device(Car)!.Goals);
    }

    [Fact]
    public async Task Progress_that_leaves_the_plan_by_more_than_the_tolerance_replans_without_waiting()
    {
        // Why: the cloud case of the design: the plan expects the car to be drawing on the surplus at 12:01 and by 12:11 the remaining need has not moved. Beyond
        // progress_tolerance_fraction (10 percent of the 3 kWh need) the plan is stale in substance and is redone now, not at 12:15.
        var rig = new Rig();
        rig.World.Remaining = 3;
        rig.Time.SetUtcNow(Local(2026, 10, 27, 12).ToUniversalTime());
        await rig.FirstPlan();
        var first = rig.State.Plan!.Id;

        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.Step();

        Assert.NotEqual(first, rig.State.Plan!.Id);
        Assert.Equal("progress", rig.State.LastSolve!.Reason);
    }

    [Fact]
    public async Task Progress_that_follows_the_plan_is_not_a_trigger()
    {
        // Why: the other side: when the remaining need falls as the plan said it would, no extra plan is made between the cycle boundaries.
        var rig = new Rig();
        await rig.FirstPlan();

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        var wait = await rig.Step();

        Assert.Equal(TimeSpan.FromMinutes(9), wait);
        Assert.Equal("first", rig.State.LastSolve!.Reason);
    }

    [Fact]
    public async Task Small_forecast_jitter_never_changes_the_current_slot_and_a_real_change_does_after_two_plans()
    {
        // Why: no flapping. The current slot is a commitment: a 3 percent wobble (below adopt_min_change_fraction, 5 percent) never changes it however many plans
        // propose it, and a real change (+15 percent PV) is adopted only when two consecutive plans agree on it (adopt_cycles = 2). Future slots take every plan.
        var rig = new Rig();
        rig.World.Remaining = 6;
        rig.World.Tolerance = 100;
        rig.Time.SetUtcNow(Local(2026, 10, 27, 12).ToUniversalTime());
        await rig.FirstPlan();
        var adopted = rig.Entry()!;
        output.WriteLine(rig.Show());
        Assert.Equal(PlanWant.Run, adopted.Want);

        // A 3 percent wobble at 12:15 and 12:30.
        rig.World.PvScale = 1.03;
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        await rig.Step();
        output.WriteLine(rig.Show());
        var after = rig.Entry()!;
        Assert.Equal(adopted.Want, after.Want);
        Assert.Equal(adopted.Class, after.Class);
        Assert.Equal(adopted.EnergyKwh / (adopted.End - adopted.Start).TotalHours, after.EnergyKwh / (after.End - after.Start).TotalHours, 6);

        // A real change (+15 percent): held at the first plan that proposes it, adopted at the second.
        rig.World.PvScale = 1.15;
        rig.Time.Advance(TimeSpan.FromMinutes(15));
        await rig.Step();
        var held = rig.Entry()!;
        Assert.Equal(adopted.EnergyKwh / (adopted.End - adopted.Start).TotalHours, held.EnergyKwh / (held.End - held.Start).TotalHours, 6);
        rig.Time.Advance(TimeSpan.FromMinutes(15));
        await rig.Step();
        var changed = rig.Entry()!;
        Assert.True(changed.EnergyKwh / (changed.End - changed.Start).TotalHours > (adopted.EnergyKwh / (adopted.End - adopted.Start).TotalHours) * 1.05, "the real change was never adopted");
    }

    [Fact]
    public async Task A_device_running_under_a_run_entry_keeps_it_to_the_end_of_the_slot_even_when_the_plan_would_defer_it()
    {
        // Why: a forecast wobble must never produce a stop and a start inside an hour. At 12:00 the car runs on surplus; at 12:15 and again at 12:30 the forecast has
        // collapsed and the plan says defer: two agreeing plans would normally adopt it, but a run entry holds to the slot's end. At 13:00, a new slot, the plan
        // is taken as it comes.
        var rig = new Rig();
        rig.World.Remaining = 6;
        rig.World.Tolerance = 100;
        rig.Time.SetUtcNow(Local(2026, 10, 27, 12).ToUniversalTime());
        await rig.FirstPlan();
        Assert.Equal(PlanWant.Run, rig.Entry()!.Want);
        Assert.Equal(PlanClasses.Pv, rig.Entry()!.Class);

        rig.World.PvScale = 0;
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        await rig.Step();
        output.WriteLine(rig.Show());
        Assert.Equal(PlanWant.Run, rig.Entry()!.Want);
        rig.Time.Advance(TimeSpan.FromMinutes(15));
        await rig.Step();
        Assert.Equal(PlanWant.Run, rig.Entry()!.Want);
        rig.Time.Advance(TimeSpan.FromMinutes(15));
        await rig.Step();
        Assert.Equal(PlanWant.Run, rig.Entry()!.Want);
        Assert.Equal(PlanClasses.Pv, rig.Entry()!.Class);
        rig.Time.Advance(TimeSpan.FromMinutes(15));
        await rig.Step();

        Assert.Equal("zone:offpeak", rig.Entry()!.Class);
        Assert.Empty(PlanVerifier.Verify(rig.World.Capture(rig.Time.GetUtcNow())!.Input, rig.State.Plan!));
    }

    [Fact]
    public async Task A_goal_change_is_adopted_at_once_for_that_device_whatever_the_hysteresis()
    {
        // Why: the hysteresis protects against noise, not against the user. A changed goal (a new target, a new deadline) is a trigger and replaces the current
        // slot's entry in the first plan made for it.
        var rig = new Rig();
        rig.World.Remaining = 6;
        rig.Time.SetUtcNow(Local(2026, 10, 27, 12).ToUniversalTime());
        await rig.FirstPlan();
        var before = rig.Entry()!;

        rig.World.Remaining = 1;
        rig.World.GoalRevision = 2;
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        await rig.Step();

        var after = rig.Entry()!;
        Assert.Equal(2, after.Goal!.Revision);
        Assert.True(after.EnergyKwh < before.EnergyKwh);
    }

    [Fact]
    public async Task A_solver_that_throws_keeps_the_last_plan_logs_and_never_stops_the_loop()
    {
        // Why: "solver failure = last plan stays". An exception in the planner must not reach the host, must not blank the plan and must not stop the next cycle:
        // the failure is shown (lastSolve failed, note plan_invalid) and logged as plan_failed.
        var fail = false;
        var rig = new Rig((input, token) => fail ? throw new InvalidOperationException("boom") : Planner.Plan(input, token));
        await rig.FirstPlan();
        var kept = rig.State.Plan!;

        fail = true;
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        await rig.Step();

        Assert.Same(kept, rig.State.Plan);
        Assert.Equal(SolveResult.Failed, rig.State.LastSolve!.Result);
        Assert.Contains(PlanNote.PlanInvalid, rig.State.Notes);
        Assert.True(rig.Logged("plan_failed"));
        fail = false;
        rig.Time.Advance(TimeSpan.FromMinutes(15));
        await rig.Step();
        Assert.NotSame(kept, rig.State.Plan);
        Assert.Equal(SolveResult.Computed, rig.State.LastSolve!.Result);
    }

    [Fact]
    public async Task A_solver_over_max_solve_time_is_a_failure_that_keeps_the_last_plan()
    {
        // Why: max_solve_time (2 s) bounds how long the planner may hold a plan back. A solver that does not return in time is abandoned on the injected clock (it
        // is told to stop and its result is never read); the last plan stays and the note says solver_timeout. The step itself returns, so the loop goes on.
        using var release = new ManualResetEventSlim();
        var block = false;
        var rig = new Rig((input, token) =>
        {
            if (block)
            {
                release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            }

            return Planner.Plan(input, CancellationToken.None);
        });
        await rig.FirstPlan();
        var kept = rig.State.Plan!;

        block = true;
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        var step = rig.Step();
        rig.Time.Advance(Parameters().MaxSolveTime);
        await step.Soon();
        release.Set();

        Assert.Same(kept, rig.State.Plan);
        Assert.Equal(SolveResult.Timeout, rig.State.LastSolve!.Result);
        Assert.Contains(PlanNote.SolverTimeout, rig.State.Notes);
        Assert.True(rig.Logged("plan_failed"));
        Assert.True(rig.Logged("did not finish"));
    }

    [Fact]
    public async Task A_plan_the_verifier_refuses_is_dropped_and_the_last_plan_stays()
    {
        // Why: "a plan with violations is not adopted". A solver (or a bug) that returns a plan over the phase limit is caught by the verifier before anything can
        // read it; the last good plan stays and the violation codes are shown.
        var corrupt = false;
        var rig = new Rig((input, token) =>
        {
            var plan = Planner.Plan(input, token);
            if (!corrupt)
            {
                return plan;
            }

            var device = plan.Devices[0];
            var entries = device.Entries.Select(e => e.Want == PlanWant.Run ? e with { EnergyKwh = e.EnergyKwh + 50 } : e).ToImmutableArray();
            return plan.WithDevices([device with { Entries = entries }]);
        });
        await rig.FirstPlan();
        var kept = rig.State.Plan!;

        corrupt = true;
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        await rig.Step();

        Assert.Same(kept, rig.State.Plan);
        Assert.Equal(SolveResult.Invalid, rig.State.LastSolve!.Result);
        Assert.NotEmpty(rig.State.LastSolve.Violations);
        Assert.Contains(PlanNote.PlanInvalid, rig.State.Notes);
        Assert.True(rig.Logged("verifier refused"));
    }

    [Fact]
    public async Task Inputs_that_cannot_be_built_are_a_failure_logged_once_not_a_crash()
    {
        // Why: a configuration or a source can refuse to give inputs (a horizon beyond the slot bound, a broken forecast). That is a failed plan, not a failed host:
        // the last plan stays, one log line per streak (not one per store cycle), and the next good capture plans again.
        var rig = new Rig();
        await rig.FirstPlan();
        var kept = rig.State.Plan;
        rig.World.Fail = true;
        rig.Time.Advance(TimeSpan.FromMinutes(14));

        await rig.Step();
        await rig.Step();
        await rig.Step();

        Assert.Same(kept, rig.State.Plan);
        Assert.Single(rig.Logs.Entries, e => e.Text.Contains("plan_failed", StringComparison.Ordinal));
        rig.World.Fail = false;
        rig.Time.Advance(TimeSpan.FromMinutes(15));
        await rig.Step();
        Assert.NotSame(kept, rig.State.Plan);
    }

    [Fact]
    public async Task While_suspended_no_plan_is_made_and_the_status_says_so()
    {
        // Why: with the guard in safe state, the kill switch on or Home Assistant gone the plan is ignored and none is made (the planner does not fabricate fresh
        // inputs from stale ones); the last plan is kept for when it is over, and the state is suspended.
        var rig = new Rig();
        await rig.FirstPlan();
        var kept = rig.State.Plan;

        rig.Suspension.Set(true);
        rig.Time.Advance(TimeSpan.FromMinutes(20));
        var wait = await rig.Step();

        Assert.Equal(Timeout.InfiniteTimeSpan, wait);
        Assert.Same(kept, rig.State.Plan);
        Assert.True(rig.State.Suspended);
        Assert.Equal(PlanStatus.Suspended, rig.State.StatusAt(rig.Time.GetUtcNow()));
        Assert.True(rig.Logged("plan_suspended"));
    }

    [Fact]
    public async Task A_plan_older_than_the_maximum_age_is_stale_and_one_from_the_future_is_too()
    {
        // Why: whatever reads the plan must be able to tell it is too old (after plan_max_age the consumer is v2) or impossible (the clock stepped back): the state's
        // status says stale for both and the usable plan is null.
        var rig = new Rig();
        await rig.FirstPlan();
        var now = rig.Time.GetUtcNow();

        Assert.NotNull(rig.State.UsablePlan(now));
        Assert.Equal(PlanStatus.Stale, rig.State.StatusAt(now + Parameters().PlanMaxAge + TimeSpan.FromSeconds(1)));
        Assert.Null(rig.State.UsablePlan(now + Parameters().PlanMaxAge + TimeSpan.FromSeconds(1)));
        Assert.Equal(PlanStatus.Stale, rig.State.StatusAt(now - TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task The_loop_runs_the_cadence_on_the_virtual_clock()
    {
        // Why: the steps above are driven by hand; the real loop must do the same on its own timer: wait out the start delay, plan, wake at the quarter hour,
        // plan again, and stop cleanly when cancelled.
        var rig = new Rig();
        var plans = new List<DateTimeOffset>();
        var signal = new SemaphoreSlim(0);
        rig.Holder.Published += (_, state) =>
        {
            if (state.Plan is { } plan && (plans.Count == 0 || plans[^1] != plan.ComputedAt))
            {
                plans.Add(plan.ComputedAt);
                signal.Release();
            }
        };
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(cts.Token);

        // The first step ran when the loop started (the start delay is pending, the timer is armed). Moving the clock fires the timer; the explicit wake-up
        // makes the test independent of which of the two the loop sees first.
        rig.Time.Advance(Parameters().StartDelay);
        rig.Service.Wake();
        await signal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        rig.Time.Advance(TimeSpan.FromMinutes(14));
        rig.Service.Wake();
        await signal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal([Monday1800.AddSeconds(60), Monday1800.AddMinutes(15)], plans);
        await cts.CancelAsync();
        await loop.Soon();
    }

    [Fact]
    public async Task Store_cycles_nudge_the_loop_at_most_once_per_minimum_interval()
    {
        // Why: the signal layer publishes every few seconds and building a plan's input is not free; a nudge per store cycle must not become a capture per cycle.
        // After a look, nudges inside planner.min_replan_interval are dropped; the first one after it wakes the loop.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var loop = rig.Service.RunAsync(cts.Token);
        Assert.Equal(1, rig.World.Captures);

        for (var i = 0; i < 20; i++)
        {
            rig.Service.Notify();
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Equal(1, rig.World.Captures);

        rig.Time.Advance(Parameters().MinReplanInterval);
        rig.Service.Notify();
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (rig.World.Captures < 2 && TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Yield();
        }

        Assert.True(rig.World.Captures >= 2);
        await cts.CancelAsync();
        await loop.Soon();
    }
}
