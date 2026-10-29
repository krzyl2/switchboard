using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Configuration;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Goals;
using Switchboard.Application.Guard;
using Switchboard.Application.Planning;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Application.Planning;

/// <summary>
/// PLAN-17: every row of the failure table of 15-PLANNER ("Failure behaviour") has a named test. This file holds the tests the rows lacked and is the index of
/// the rest. Why it matters: the principle of the table is that a failing planner input narrows or falls back and never widens or guesses; a row nobody
/// tests is a reaction that can silently turn into a guess. Row -> test (tests/Switchboard.Tests/...):
/// <code>
/// PV forecast missing / stale        Host/PlanInputsEndpointTests: A_missing_forecast_is_zero_pv_named_in_the_notes_and_in_the_basis, A_stale_forecast_gives_no_pv_even_if_it_still_carries_its_last_series
/// PV unavailable / wrong unit / bad  here: Failure_pv_forecast_in_a_state_that_is_not_usable_is_zero_pv_and_missing (the signal layer's own refusals: Application/Signals/ForecastSignalTests)
/// PV forecast partial                Host/PlanInputsEndpointTests: A_partial_forecast_is_derated_zero_beyond_its_end_and_noted_with_its_age; Domain/PvForecastTests: Nothing_is_invented_beyond_the_coverage...
/// Price curve missing / stale / part Host/PlanInputsEndpointTests: A_curve_prices_the_slots_it_covers_and_the_fallback_tariff_the_rest_with_a_note, A_stale_curve_is_never_used...; here: Failure_price_curve_in_a_state_that_is_not_usable_is_priced_from_the_tariff_and_noted
/// Load profile corrupt / empty bins  Infrastructure/LoadProfileFileTests: A_file_that_is_not_a_profile_document_is_set_aside; Domain/LoadProfileTests: With_no_filled_bin_the_forecast_is_the_configured_fallback_and_says_so, An_empty_bin_takes_the_mean_of_the_filled_ones...
/// Goal input stale / unavailable     Application/Goals/GoalSourceTests (reason per input), Host/PlanEndpointTests: An_inactive_goal_says_which_input_failed_and_why..., A_goal_inactive_for_a_stale_or_unavailable_input_raises_the_goal_input_stale_note, With_all_goal_inputs_fresh_there_is_no_goal_input_stale_note; here: Failure_goal_input_stale_goal_leaves_the_plan_and_the_device_falls_back_to_v2
/// SoC of storage stale               N/A until slice 6 (storage, deferred by the maintainer 2026-10-29); Domain/PlannerTests: A_storage_device_is_not_planned_before_slice_6 is the only reaction there is
/// Hard goal infeasible / at risk     Domain/PlannerTests: A_hard_goal_that_cannot_be_met_even_at_full_power_is_infeasible..., Application/Dispatch/PlanDispatchGateTests: Urgent_opens_every_zone_in_full...
/// Solver failure / timeout / invalid Application/Planning/PlannerServiceTests: A_solver_that_throws..., A_solver_over_max_solve_time..., A_plan_the_verifier_refuses..., Inputs_that_cannot_be_built...
/// Planner stopped or throws          here: Failure_planner_loop_that_throws_is_survived_and_dispatch_sees_no_plan
/// Clock step back                    PlannerServiceTests: A_clock_stepped_back_replans_at_once..., PlanViewTests: A_plan_from_the_future_gives_no_entry, Each_global_reason_for_the_fallback_is_named...
/// Clock step forward / DST day       here: Failure_clock_step_forward_past_the_maximum_age_replans_at_once; Host/PlanInputsEndpointTests: Slots_equal_the_tariff_at_every_boundary_across_a_23_and_a_25_hour_day
/// Restart (plan not persisted)       here: Failure_restart_starts_without_a_plan_and_plans_from_the_inputs_alone, Failure_restart_the_runtime_state_has_no_place_for_a_plan; PlannerServiceTests: No_plan_is_made_before_the_start_delay...
/// HA disconnected / safe state / kill here: Failure_planner_suspension_follows_safe_state_kill_switch_and_home_assistant; PlannerServiceTests: While_suspended_no_plan_is_made..., PlanViewTests: A_suspended_plan_gives_no_entry
/// planner: removed on reload         PlannerServiceTests: Removing_the_planner_section_drops_the_plan; Application/Dispatch/PlannerBackwardCompatibilityTests / PlannerDispatchDigestTests
/// planner: / goal invalid (semantic) goal: Application/Configuration/GoalConfigurationTests: A_goal_that_can_never_be_evaluated_is_dropped_with_a_warning...; planner: here: Failure_planner_section_invalid_invalidates_the_document_and_leaves_the_planner_no_configuration (no warning path: the document is invalid; the planner then plans nothing, as PlannerServiceTests: Removing_the_planner_section_drops_the_plan)
/// Mode shadow                        Application/Dispatch/PlannerDispatchDigestTests (shadow digest), PlanDispatchGateTests
/// Statistics file older schema       Infrastructure/StatisticsPricesFileTests: Row_statistics_file_with_an_older_schema_reads_with_no_price_data...
/// </code>
/// </summary>
public sealed class PlannerFailureTableTests
{
    private static readonly DateTimeOffset Monday1800 = Local(2026, 10, 26, 18).ToUniversalTime();
    private static readonly SignalName PvSignal = SignalName.Parse("pv_forecast_series");
    private static readonly SignalName CurveSignal = SignalName.Parse("price_curve");

    private const string Planner = "\nplanner:\n  dispatch: off\n";

    private const string HourlyTariff =
        "\neconomics:\n  currency: PLN\n  tariff:\n    default_zone: offpeak\n    zones:\n      - id: peak\n        price: 0.40\n        when:\n          - { days: [workday, weekend], hours: [\"07:00-13:00\", \"16:00-21:00\"] }\n      - id: offpeak\n        price: 0.10\n        when: []\n";

    private const string CurveSignals = """
          market_price:
            entity: sensor.market_price
            kind: price
            unit: PLN/MWh
            max_age: 5m
            role: price_import
          price_curve:
            entity: sensor.price_curve
            kind: series
            attribute: raw_today
            unit: PLN/MWh
            max_age: 6h
            role: price_curve_import

        """;

    private const string CurveEconomics = "\neconomics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    curve: price_curve\n    fallback: 0.9\n";

    private static PlanResponse Build(ConfigurationResult config, DateTimeOffset at, ForecastInputs forecast)
    {
        var sources = PlanSourceMapper.Map(config.Configuration!, forecast, LoadProfile.Empty, Warsaw, at);
        return PlanEndpoint.Build(config, GoalBoard.Empty(at), sources, forecast);
    }

    private static ForecastInputs Sample(SignalName name, SignalRole role, SignalState state, SignalDetail detail, DateTimeOffset at, TimeSeries? series) =>
        new(ImmutableDictionary<SignalName, ForecastSample>.Empty.Add(name, new ForecastSample(role, SignalKind.Series, state, detail, state == SignalState.Fresh ? at : null, series, null, null)));

    [Theory]
    [InlineData(SignalState.Unavailable, SignalDetail.StateUnavailable)]
    [InlineData(SignalState.Unavailable, SignalDetail.StateUnknown)]
    [InlineData(SignalState.Rejected, SignalDetail.UnitMismatch)]
    [InlineData(SignalState.Rejected, SignalDetail.SeriesMalformed)]
    public void Failure_pv_forecast_in_a_state_that_is_not_usable_is_zero_pv_and_missing(SignalState state, SignalDetail detail)
    {
        // Why: the table row "missing, stale, unavailable, wrong unit, unparseable": whatever the signal layer refused, the plan is made with no PV at all and
        // says so, never with the last series (the sample here still carries one, against the signal layer's own rule, so a mapper that read it would fail).
        var at = Local(2026, 7, 15, 6, 2);
        var series = new TimeSeries([.. Enumerable.Range(0, 48).Select(h => new SeriesPoint(at.AddHours(h), null, 1000))], SeriesValueKind.PowerW);

        var response = Build(TestConfigurations.Valid(TestConfigurations.ThreePhase() + HourlyTariff + Planner), at, Sample(PvSignal, SignalRole.PvForecast, state, detail, at, series));

        Assert.Equal(["pv_forecast_missing"], response.Notes);
        Assert.Equal("none", response.Inputs.PvForecast!.Basis);
        Assert.All(response.Slots, s => Assert.Equal(0, s.PvKwh));
    }

    [Theory]
    [InlineData(SignalState.Unavailable, SignalDetail.StateUnavailable)]
    [InlineData(SignalState.Rejected, SignalDetail.UnitMismatch)]
    [InlineData(SignalState.Rejected, SignalDetail.SeriesMalformed)]
    public void Failure_price_curve_in_a_state_that_is_not_usable_is_priced_from_the_tariff_and_noted(SignalState state, SignalDetail detail)
    {
        // Why: the table row "price curve missing, stale or partial": a curve the signal layer refused must not price a single slot; the spec's fallback
        // (0.90) prices all of them and the plan says the curve was not used.
        var at = Local(2026, 7, 15, 6, 2);
        var curve = new TimeSeries([new SeriesPoint(at, at.AddHours(48), 0.5)], SeriesValueKind.PricePerKwh);

        var response = Build(TestConfigurations.Valid(TestConfigurations.ThreePhase(CurveSignals) + CurveEconomics + Planner), at, Sample(CurveSignal, SignalRole.PriceCurveImport, state, detail, at, curve));

        Assert.Contains("price_curve_partial", response.Notes);
        Assert.Equal("tariff", response.Inputs.Prices!.Basis);
        Assert.All(response.Slots, s => Assert.Equal(0.9, s.ImportPrice!.Value, 9));
    }

    /// <summary>The worked example, planned fresh at each look; <see cref="Remaining"/> changes what the car still needs.</summary>
    private sealed class Source : IPlannerSource
    {
        public double Remaining { get; set; } = 9.9;

        public bool Inactive { get; set; }

        public PlannerCapture? Capture(DateTimeOffset now)
        {
            var input = WorkedExample(now: now, remaining: Remaining, withHeaters: false);
            ImmutableArray<InactiveGoal> inactive = Inactive ? [new InactiveGoal(Car, GoalInactiveReason.InputStale, GoalInputKind.Soc, GoalOrigin.Entity, GoalKind.Soc, true)] : [];
            return new PlannerCapture(Inactive ? input with { Goals = [] } : input, inactive, "f1", "ev_charger+");
        }
    }

    private sealed class Suspension : IPlannerSuspension
    {
        public bool Throws { get; set; }

        public bool Suspended => Throws ? throw new InvalidOperationException("the guard view broke") : false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private static PlannerService Service(IPlannerSource source, IPlannerSuspension suspension, PlanHolder holder, TimeProvider time, LogCapture logs) =>
        new(source, suspension, holder, time, logs.For<PlannerService>());

    private static (FakeTimeProvider Time, PlanHolder Holder, LogCapture Logs) Rig(DateTimeOffset at)
    {
        var time = new FakeTimeProvider(at);
        time.SetLocalTimeZone(Warsaw);
        return (time, new PlanHolder(), new LogCapture());
    }

    [Fact]
    public async Task Failure_goal_input_stale_goal_leaves_the_plan_and_the_device_falls_back_to_v2()
    {
        // Why: the table row "goal input stale": the goal is inactive and the plan no longer holds it, so dispatch gets no entry for the device (v2 for it) rather
        // than a Run or a Defer made for an input that is gone.
        var (time, holder, logs) = Rig(Monday1800);
        var source = new Source();
        var service = Service(source, new Suspension(), holder, time, logs);
        await service.StepAsync(TestContext.Current.CancellationToken);
        time.Advance(Parameters().StartDelay);
        await service.StepAsync(TestContext.Current.CancellationToken);
        var currency = new StubCurrency { ConfigRevision = holder.Current.Plan!.ConfigRevision };
        var view = new PlanView(holder, currency, logs.For<PlanView>());
        foreach (var goal in holder.Current.Plan.Device(Car)!.Goals)
        {
            currency.Specs[Car] = goal.Ref.SpecIdentity;
            currency.Goals[(Car, goal.Ref.Id, goal.Ref.Revision)] = new PlanGoalState(Offpeak, false);
        }

        Assert.NotNull(view.For(Car, time.GetUtcNow()));

        source.Inactive = true;
        time.Advance(TimeSpan.FromSeconds(61));
        source.Remaining = 9.9;
        await service.StepAsync(TestContext.Current.CancellationToken);

        Assert.Empty(holder.Current.Plan!.Device(Car)!.Goals);
        Assert.Null(view.For(Car, time.GetUtcNow()));
    }

    [Fact]
    public async Task Failure_planner_loop_that_throws_is_survived_and_dispatch_sees_no_plan()
    {
        // Why: the table row "planner stopped or throws": a fault inside the planner's own loop must not reach the host or dispatch. It is logged once, the
        // holder stays empty (status absent, so the view hands out no entry: v2) and the loop is still alive to plan when the fault is gone.
        var (time, holder, logs) = Rig(Monday1800);
        var suspension = new Suspension { Throws = true };
        var service = Service(new Source(), suspension, holder, time, logs);
        var planned = new SemaphoreSlim(0);
        holder.Published += (_, state) =>
        {
            if (state.Plan is not null)
            {
                planned.Release();
            }
        };
        using var cts = new CancellationTokenSource();

        var loop = service.RunAsync(cts.Token);
        for (var i = 0; i < 1000 && !logs.Entries.Any(e => e.Text.Contains("plan_failed", StringComparison.Ordinal)); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        var failure = Assert.Single(logs.Entries, e => e.Text.Contains("plan_failed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.Equal(PlanState.Empty, holder.Current);
        Assert.Equal(PlanStatus.Absent, holder.Current.StatusAt(time.GetUtcNow()));
        var currency = new StubCurrency { ConfigRevision = 1 };
        Assert.Null(new PlanView(holder, currency, logs.For<PlanView>()).For(Car, time.GetUtcNow()));
        Assert.False(loop.IsCompleted);

        suspension.Throws = false;
        time.Advance(Parameters().StartDelay);
        service.Wake();
        await planned.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(holder.Current.Plan);
        await cts.CancelAsync();
        await loop.Soon();
    }

    [Fact]
    public async Task Failure_clock_step_forward_past_the_maximum_age_replans_at_once()
    {
        // Why: the table row "clock step forward": a plan that the clock jumped past (a paused VM, an NTP step of hours) is stale for dispatch the moment the clock
        // reads it, and the planner replans at once with the reason clock_step, not at the next quarter hour and not behind the rate limit.
        var (time, holder, logs) = Rig(Monday1800);
        var service = Service(new Source(), new Suspension(), holder, time, logs);
        await service.StepAsync(TestContext.Current.CancellationToken);
        time.Advance(Parameters().StartDelay);
        await service.StepAsync(TestContext.Current.CancellationToken);
        var first = holder.Current.Plan!;

        time.Advance(Parameters().PlanMaxAge + TimeSpan.FromHours(2));
        Assert.Equal(PlanStatus.Stale, holder.Current.StatusAt(time.GetUtcNow()));
        Assert.Null(holder.Current.UsablePlan(time.GetUtcNow()));
        await service.StepAsync(TestContext.Current.CancellationToken);

        Assert.Equal("clock_step", holder.Current.LastSolve!.Reason);
        Assert.Equal(time.GetUtcNow(), holder.Current.Plan!.ComputedAt);
        Assert.NotEqual(first.Id, holder.Current.Plan.Id);
        Assert.Equal(PlanStatus.Fresh, holder.Current.StatusAt(time.GetUtcNow()));
    }

    [Fact]
    public async Task Failure_restart_starts_without_a_plan_and_plans_from_the_inputs_alone()
    {
        // Why: the table row "restart": the plan is a pure function of its inputs and a stale one is a hazard, so a new process has none, dispatch is v2 until
        // start_delay, and the first plan carries nothing of the old one (adoption memory is empty: it is adopted whole, at once). The inputs changed across the
        // restart (the car needs 3 kWh, was 9.9): a plan restored from before would still say 9.9.
        var (time, holder, logs) = Rig(Monday1800);
        var source = new Source();
        var before = Service(source, new Suspension(), holder, time, logs);
        await before.StepAsync(TestContext.Current.CancellationToken);
        time.Advance(Parameters().StartDelay);
        await before.StepAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(holder.Current.Plan);
        var oldEnergy = holder.Current.Plan.Device(Car)!.Goals[0].RemainingKwh;

        time.Advance(TimeSpan.FromMinutes(10));
        source.Remaining = 3;
        var restartedHolder = new PlanHolder();
        var restarted = Service(source, new Suspension(), restartedHolder, time, logs);
        var wait = await restarted.StepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Parameters().StartDelay, wait);
        Assert.Equal(PlanState.Empty, restartedHolder.Current);
        Assert.Equal(PlanStatus.Absent, restartedHolder.Current.StatusAt(time.GetUtcNow()));
        Assert.Null(new PlanView(restartedHolder, new StubCurrency(), logs.For<PlanView>()).For(Car, time.GetUtcNow()));

        time.Advance(Parameters().StartDelay);
        await restarted.StepAsync(TestContext.Current.CancellationToken);

        var plan = restartedHolder.Current.Plan!;
        Assert.Equal("first", restartedHolder.Current.LastSolve!.Reason);
        Assert.Equal(time.GetUtcNow(), plan.ComputedAt);
        Assert.NotEqual(oldEnergy, plan.Device(Car)!.Goals[0].RemainingKwh);
        Assert.Equal(3, plan.Device(Car)!.Goals[0].RemainingKwh, 6);
        Assert.Equal(Solve(source.Capture(time.GetUtcNow())!.Input).Id, plan.Id);
    }

    [Fact]
    public void Failure_restart_the_runtime_state_has_no_place_for_a_plan()
    {
        // Why: "the plan is not persisted" is a property of what state.json can hold: goal progress is persisted (energy goals), the plan is not. If a plan or its
        // holder state ever became a field of the persisted record, a restart could revive a plan nobody can vouch for.
        var carried = typeof(RuntimeState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.PropertyType.ToString());

        Assert.DoesNotContain(carried, t => t.Contains("PlanSnapshot", StringComparison.Ordinal) || t.Contains("PlanState", StringComparison.Ordinal) || t.Contains("Planning.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, ConnectionState.Connected, false)]
    [InlineData(true, false, ConnectionState.Connected, true)]
    [InlineData(false, true, ConnectionState.Connected, true)]
    [InlineData(false, false, ConnectionState.Disconnected, true)]
    [InlineData(false, false, ConnectionState.Connecting, true)]
    public void Failure_planner_suspension_follows_safe_state_kill_switch_and_home_assistant(bool safeState, bool killSwitch, ConnectionState home, bool expected)
    {
        // Why: the table row "Home Assistant disconnected, guard safe state, kill switch": any one of the three makes the planner stand aside, and only all three
        // clear lets it plan. A suspension that missed one would let the planner fabricate fresh plans from inputs the guard no longer vouches for.
        var at = Monday1800;
        var guard = new Guard(GuardStatus.Initial(at, TestModes.Shadow) with
        {
            SafeState = safeState ? new SafeStateStatus(true, SafeStateScope.All, ReasonCode.SafeState, [], at) : SafeStateStatus.None,
        });
        var suspension = new Switchboard.Host.Composition.PlannerSuspension(guard, new Kill(killSwitch), new Home(home));

        Assert.Equal(expected, suspension.Suspended);
    }

    [Fact]
    public void Failure_planner_suspension_wakes_the_planner_only_when_the_answer_changes()
    {
        // Why: the guard publishes at every step; waking the planner at each would be a trigger storm, while a missed change would leave a safe-state exit
        // unnoticed (the planner must replan at once when it ends).
        var at = Monday1800;
        var guard = new Guard(GuardStatus.Initial(at, TestModes.Shadow));
        var home = new Home(ConnectionState.Connected);
        var suspension = new Switchboard.Host.Composition.PlannerSuspension(guard, new Kill(false), home);
        var changes = 0;
        suspension.Changed += (_, _) => changes++;

        guard.Publish(GuardStatus.Initial(at, TestModes.Shadow));
        Assert.Equal(0, changes);
        guard.Publish(GuardStatus.Initial(at, TestModes.Shadow) with { SafeState = SafeStateStatus.None });
        Assert.Equal(1, changes);
        Assert.False(suspension.Suspended);
        home.Set(ConnectionState.Disconnected);
        Assert.Equal(2, changes);
        Assert.True(suspension.Suspended);
    }

    [Theory]
    [InlineData("planner:\n  dispatch: wide\n", "planner.dispatch")]
    [InlineData("planner:\n  dispach: narrow\n", "planner.dispach")]
    public void Failure_planner_section_invalid_invalidates_the_document_and_leaves_the_planner_no_configuration(string planner, string path)
    {
        // Why: the table row "planner: invalid in a semantic way": the planner is not dropped alone with a warning, the whole document is Invalid (no fallback to
        // an earlier one), so no configuration reaches the planner source (it captures nothing: the planner idles exactly as with the section removed) and the
        // user gets an error naming the key instead of a planner that quietly does something else.
        var result = TestConfigurations.Parse(TestConfigurations.ThreePhase() + "\n" + planner);

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Null(result.Configuration);
        Assert.Contains(result.Errors, e => e.Path == path);
        Assert.DoesNotContain(result.Warnings, w => w.Path.StartsWith("planner", StringComparison.Ordinal));
    }

    private sealed class Guard(GuardStatus current) : IGuardStatusSource
    {
        public GuardStatus Current { get; private set; } = current;

        public event EventHandler<GuardStatus>? Published;

        public void Publish(GuardStatus status)
        {
            Current = status;
            Published?.Invoke(this, status);
        }
    }

    private sealed class Kill(bool engaged) : IKillSwitchView
    {
        public KillSwitchStatus Status => engaged ? new KillSwitchStatus(true, null, KillSwitchSource.Api) : KillSwitchStatus.Off;
    }

    private sealed class Home(ConnectionState state) : IHomeAssistantConnectionState
    {
        public ConnectionState Current { get; private set; } = state;

        public event EventHandler<ConnectionState>? Changed;

        public void Set(ConnectionState value)
        {
            Current = value;
            Changed?.Invoke(this, value);
        }
    }
}
