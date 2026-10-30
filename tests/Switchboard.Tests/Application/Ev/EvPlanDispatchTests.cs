using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Ev;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Application.Ev;

/// <summary>
/// The module's goals through a real plan and the real plan view (EV-11, docs/EV.md 6 S5). Why it matters: the module may only ever reach the car through the
/// plan, and the plan reaches dispatch only behind <c>planner.dispatch</c>. With the switch off the plan view answers nothing whatever the module offers, so the car
/// decides as in v2 (the digests of the synthetic morning pin that for the rest of dispatch); at <c>narrow</c> the same plan narrows the car, never widens it.
/// And a module that stalls or breaks must not be able to stop the host.
/// </summary>
public sealed class EvPlanDispatchTests
{
    private static PlanView ViewOver(EvRig rig, PlannerDispatchMode mode)
    {
        var offer = rig.TickAndOffer();
        Assert.NotEmpty(offer.Requests);
        var now = rig.Now;
        var inputs = Custom(now, 24, _ => ("offpeak", 0.6, 0.1, 0));
        var plan = Planner.Plan(Input(now, inputs, [Charger()], offer.Requests, Capacity(5000)), TestContext.Current.CancellationToken);
        var holder = new PlanHolder();
        holder.Publish(new PlanState(plan, false, null, [], TimeSpan.FromMinutes(45)));
        var currency = new StubCurrency { Mode = mode };
        currency.Specs[Car] = 7;
        foreach (var request in offer.Requests)
        {
            currency.Goals[(Car, Planner.GoalId(request), request.Revision)] = new PlanGoalState(request.Sources, request.OnMet == GoalOnMet.Stop);
        }

        return new PlanView(holder, currency, NullLogger<PlanView>.Instance);
    }

    [Fact]
    public void With_the_planner_dispatch_off_the_modules_goals_never_reach_the_car()
    {
        // Why: EV.md 2.2 and EV-11. The module computes a preview at off; the plan view must answer nothing for the car, so dispatch takes its v2 path untouched.
        var rig = new EvRig();
        rig.PlugInCar(40);

        var view = ViewOver(rig, PlannerDispatchMode.Off);

        Assert.Null(view.For(Car, rig.Now));
    }

    [Fact]
    public void At_narrow_the_modules_cap_holds_the_car_in_the_slots_with_no_surplus()
    {
        // Why: the same offer at narrow. A PV-only cap with no surplus in the forecast must read as Defer for the car (never "no entry", which would be the v2
        // opportunist charging off-peak to full), and an entry only ever narrows.
        var rig = new EvRig();
        rig.PlugInCar(40);

        var entry = ViewOver(rig, PlannerDispatchMode.Narrow).For(Car, rig.Now);

        Assert.NotNull(entry);
        Assert.Equal(PlanWant.Defer, entry.Want);
    }

    [Fact]
    public async Task A_keeper_loop_survives_an_input_source_that_throws_and_ends_only_on_cancellation()
    {
        // Why: an exception that escapes a BackgroundService stops the host, and with it the guard. Both the computation and the read of the cycle go through the
        // input source here; neither may end the loop, and cancelling ends it without an exception.
        var rig = new EvRig(inputs: () => new Broken());
        using var cts = new CancellationTokenSource();

        var loop = rig.Keeper.RunAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.False(loop.IsCompleted);
        Assert.True(rig.Keeper.Current.Failed);

        await cts.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Single(rig.Log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void An_offer_that_ran_out_before_the_next_computation_is_logged_once_per_streak()
    {
        // Why: a stale snapshot is a failed offer for the guard and the plan falls back to v2 for the car without a word; a late loop (a slow computation, a cycle
        // longer than snapshot_max_age) must leave a trace, once, not at every tick.
        var rig = new EvRig();
        rig.PlugInCar(40);
        var snapshot = rig.Tick();

        rig.Advance(snapshot.ValidUntil - rig.Now + TimeSpan.FromSeconds(1));
        rig.Keeper.Tick();
        rig.Advance(TimeSpan.FromSeconds(1));
        rig.Keeper.Tick();

        Assert.Single(rig.Log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("went stale", StringComparison.Ordinal));
    }

    private sealed class Broken : IEvInputSource
    {
        public EvInputView Current => throw new InvalidOperationException("the input source broke");
    }
}
