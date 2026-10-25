using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.TestSteps;

namespace Switchboard.Tests.Application.Presentation;

/// <summary>
/// The statistics behind "where PV went" and the daily summary integrate the control steps on the
/// signal clock, never the Recorder and never the wall clock. These tests drive the accumulator on
/// explicit step times: energy must keep accumulating on a constant value (the Riemann pitfall of
/// 07-PITFALLS), unknown inputs and gaps are unobserved instead of zero, clock steps neither count
/// twice nor allocate unbounded history, and sheds and breaker trips are counted honestly.
/// </summary>
public sealed class EnergyAccumulatorTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    [Fact]
    public void Constant_1700_W_for_an_hour_is_1_7_kWh_over_12_buckets()
    {
        // A heater at its rating for one hour on the 5-minute grid: 12 full buckets of 0.1417 kWh.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        Run(accumulator, Start, TimeSpan.FromHours(1), at => View(at, 1700, 0, ("heater_l1", 1700, PowerSource.RatedModel)));

        var buckets = accumulator.Snapshot().Buckets.Values.Where(b => b.Observed > TimeSpan.Zero).ToList();
        Assert.Equal(12, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(TimeSpan.FromMinutes(5), b.Observed));
        Assert.Equal(1.7, buckets.Sum(b => b.Load.Value), 9);
        Assert.Equal(1.7, buckets.Sum(b => b.Devices[DeviceName.Parse("heater_l1")].Model.Value), 9);
        Assert.Equal(0, buckets.Sum(b => b.Devices[DeviceName.Parse("heater_l1")].Measured.Value), 9);
    }

    [Fact]
    public void Constant_fresh_value_keeps_accumulating()
    {
        // 07-PITFALLS: an integration that waits for a new value stops on a constant source. The
        // same 1000 W held for 30 minutes and then for 30 more must double, not stall.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var end = Run(accumulator, Start, TimeSpan.FromMinutes(30), at => View(at, 1000));
        var half = Total(accumulator, b => b.GridImport.Value);
        Run(accumulator, end, TimeSpan.FromMinutes(30), at => View(at, 1000));

        Assert.Equal(0.5, half, 9);
        Assert.Equal(1.0, Total(accumulator, b => b.GridImport.Value), 9);
    }

    [Fact]
    public void Stale_inputs_are_unobserved_not_zero()
    {
        // A meter that went stale says nothing about the energy: the time is unobserved, and no
        // 0 kWh stands in for what nobody measured.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var end = Run(accumulator, Start, TimeSpan.FromMinutes(10), at => View(at, 1200));
        Run(accumulator, end, TimeSpan.FromMinutes(10), at => View(at, netImport: null));

        var snapshot = accumulator.Snapshot();
        Assert.Equal(TimeSpan.FromMinutes(10), Sum(snapshot, b => b.Observed));
        Assert.Equal(TimeSpan.FromMinutes(10), Sum(snapshot, b => b.Unobserved));
        Assert.Equal(0.2, Total(accumulator, b => b.GridImport.Value), 9);
    }

    [Fact]
    public void Gap_beyond_limit_is_unobserved()
    {
        // Two steps 10 minutes apart (MaxIntegrationGap 2 min): the value held across the gap is not
        // spread over it; the gap counts as unobserved.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        accumulator.Observe(Step(Start), View(Start, 3000));
        var later = Start + TimeSpan.FromMinutes(10);
        accumulator.Observe(Step(later), View(later, 3000));

        Assert.Equal(TimeSpan.FromMinutes(10), Sum(accumulator.Snapshot(), b => b.Unobserved));
        Assert.Equal(0, Total(accumulator, b => b.GridImport.Value), 9);
    }

    [Fact]
    public void Clock_step_back_integrates_nothing_and_every_bucket_stays_whole()
    {
        // An NTP step back of an hour: nothing before the high-water mark is integrated twice, so no
        // bucket ever holds more than its length (the file loader would set such a file aside).
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var end = Run(accumulator, Start, TimeSpan.FromHours(1), at => View(at, 600));
        var before = Total(accumulator, b => b.GridImport.Value);
        Run(accumulator, end - TimeSpan.FromHours(1), TimeSpan.FromHours(1), at => View(at, 600));

        var snapshot = accumulator.Snapshot();
        Assert.Equal(before, Total(accumulator, b => b.GridImport.Value), 9);
        Assert.All(snapshot.Buckets.Values, b => Assert.True(b.Observed + b.Unobserved <= TimeSpan.FromMinutes(5), $"{b.Start}: {b.Observed} + {b.Unobserved}"));
        Assert.Equal(end, snapshot.IntegratedTo);
    }

    [Fact]
    public void A_forward_jump_of_ten_years_allocates_at_most_one_retention_of_buckets()
    {
        // A board without a real-time clock boots in 1970 or jumps years on its NTP sync: unobserved
        // time is allocated only inside the retention window, never for the whole jump.
        var options = TestStatistics.Options();
        var accumulator = new EnergyAccumulator(options);
        accumulator.Observe(Step(Start), View(Start, 500));
        var jump = Start.AddYears(10);
        accumulator.Observe(Step(jump), View(jump, 500));

        var snapshot = accumulator.Snapshot();
        Assert.True(snapshot.Buckets.Count <= EnergyAccumulator.MaxBuckets(options), $"{snapshot.Buckets.Count} buckets");
        Assert.All(snapshot.Buckets.Keys, k => Assert.True(k >= jump - options.Retention - options.BucketLength));
    }

    [Fact]
    public void Pv_parts_sum_to_generation_and_export_comes_from_the_net_total()
    {
        // 3 kW of PV, the charger draws 1.5 kW, the house 0.5 kW, 1 kW exported: every kWh produced
        // is the house's, a device's, storage's, exported or unattributed, and nothing more.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        Run(accumulator, Start, TimeSpan.FromHours(1), at => View(at, netImport: -1000, generation: 3000, ("ev_charger", 1500, PowerSource.Measured)));

        var generation = Total(accumulator, b => b.Generation.Value);
        var pv = accumulator.Snapshot().Buckets.Values.Aggregate(PvAttribution.Zero, (sum, b) => sum.Add(b.Pv));
        var devices = Total(accumulator, b => b.Devices.Values.Sum(d => d.Pv.Value));
        Assert.Equal(3.0, generation, 9);
        Assert.Equal(1.0, pv.Export.Value, 9);
        Assert.Equal(1.5, devices, 9);
        Assert.Equal(0.5, pv.House.Value, 9);
        Assert.Equal(generation, pv.House.Value + pv.Storage.Value + pv.Export.Value + pv.Unattributed.Value + devices, 9);
        Assert.Equal(1.0, Total(accumulator, b => b.GridExport.Value), 9);
    }

    [Fact]
    public void An_unknown_device_sends_only_its_phase_split_to_unattributed()
    {
        // The idle go-e's draw is not fresh most of the day: only the part of its phase that cannot be
        // split is unattributed; the known heater still gets its share of the PV.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        Run(accumulator, Start, TimeSpan.FromHours(1), at => View(at, netImport: 0, generation: 2000, ("heater_l1", 1500, PowerSource.RatedModel), ("ev_charger", null, PowerSource.Unknown)));

        var pv = accumulator.Snapshot().Buckets.Values.Aggregate(PvAttribution.Zero, (sum, b) => sum.Add(b.Pv));
        Assert.Equal(1.5, Total(accumulator, b => b.Devices[DeviceName.Parse("heater_l1")].Pv.Value), 9);
        Assert.Equal(0.5, pv.Unattributed.Value, 9);
        Assert.Equal(TimeSpan.FromHours(1), Sum(accumulator.Snapshot(), b => b.Devices[DeviceName.Parse("ev_charger")].Unknown));
    }

    [Fact]
    public void Utc_buckets_stay_contiguous_across_the_25_hour_day_and_retention_holds_it()
    {
        // 2026-10-25 is a 25-hour day in Warsaw (CEST → CET at 01:00 UTC). The server is timezone-free:
        // the UTC buckets run on without a duplicate or a hole, and the retention keeps 25 h + one
        // bucket, so the browser can always build the whole local day.
        var options = TestStatistics.Options();
        var accumulator = new EnergyAccumulator(options);
        var from = new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero);
        Run(accumulator, from, TimeSpan.FromHours(26), at => View(at, 400));

        var starts = accumulator.Snapshot().Buckets.Keys.ToList();
        Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.Equal(options.BucketLength, pair.Second - pair.First));
        Assert.Contains(new DateTimeOffset(2026, 10, 25, 0, 55, 0, TimeSpan.Zero), starts);
        Assert.Contains(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero), starts);
        Assert.True(starts.Count * options.BucketLength >= TimeSpan.FromHours(25) + options.BucketLength, $"{starts.Count} buckets");
        Assert.True(options.Retention >= TimeSpan.FromHours(25) + options.BucketLength);
    }

    [Fact]
    public void Overload_sheds_are_counted_commanded_or_would()
    {
        // In shadow mode a tier shed is a would-shed; a commanded one (active semantics) is a shed.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var on = Intended(Start, ("heater_l1", DeviceLevel.Relay(true)), ("heater_l2", DeviceLevel.Relay(true)));
        accumulator.Observe(Step(Start, status: on), View(Start, 1000));
        var at = Start + Poll;
        accumulator.Observe(
            Step(at, [Device(at, "heater_l1", DecisionAction.SwitchOff, ReasonCode.GuardTierExceeded, DeviceLevel.Relay(false)), Device(at, "heater_l2", DecisionAction.SwitchOff, ReasonCode.GuardBudgetLow, DeviceLevel.Relay(false), commanded: true)], status: on),
            View(at, 1000));

        var snapshot = accumulator.Snapshot();
        Assert.Equal((1, 1), (snapshot.Lifetime.WouldSheds, snapshot.Lifetime.Sheds));
        Assert.Equal((1, 1), (Count(snapshot, b => b.WouldSheds), Count(snapshot, b => b.Sheds)));
    }

    [Fact]
    public void A_charger_setpoint_reduction_for_the_reserve_is_one_would_shed()
    {
        // The charger is the main relief on the maintainer's L1, and its reductions are recorded as
        // set_setpoint: a 16 A → 6 A step for the reserve threshold is a would-shed too.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var charging = Intended(Start, ("ev_charger", DeviceLevel.At(new Switchboard.Domain.Units.Amperes(16))));
        accumulator.Observe(Step(Start, status: charging), View(Start, 4000));
        var at = Start + Poll;
        accumulator.Observe(
            Step(at, [Device(at, "ev_charger", DecisionAction.SetSetpoint, ReasonCode.ReserveThreshold, DeviceLevel.At(new Switchboard.Domain.Units.Amperes(6)), origin: DecisionOrigin.Dispatch)], status: charging),
            View(at, 4000));

        Assert.Equal(1, accumulator.Snapshot().Lifetime.WouldSheds);
    }

    [Fact]
    public void A_reduction_deferred_by_min_on_is_one_would_shed_counted_when_it_takes_effect()
    {
        // Live shadow: heater_l2 would-starts at 23:57 (min_on 6 min) and a kettle pushes L2 over the
        // reserve at 23:59. Dispatch's would-stop waits for the lockout (recorded as a deferral) and
        // takes effect at 00:03: one reduction, one would-shed. While it waits nothing would have
        // switched, so a deferral dispatch drops is no shed at all.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var on = Intended(Start, ("heater_l2", DeviceLevel.Relay(true)));
        accumulator.Observe(Step(Start, status: on), View(Start, 3000));
        var deferredAt = Start + Poll;
        accumulator.Observe(
            Step(deferredAt, [Device(deferredAt, "heater_l2", DecisionAction.Deferred, ReasonCode.ReserveThreshold, DeviceLevel.Relay(true), origin: DecisionOrigin.Dispatch, inputs: DeferredSince(deferredAt))], status: on),
            View(deferredAt, 3000));
        Assert.Equal(0, accumulator.Snapshot().Lifetime.WouldSheds);

        var appliedAt = deferredAt + Poll;
        accumulator.Observe(
            Step(appliedAt, [Device(appliedAt, "heater_l2", DecisionAction.SwitchOff, ReasonCode.ReserveThreshold, DeviceLevel.Relay(false), origin: DecisionOrigin.Dispatch, inputs: DeferredSince(deferredAt))], status: on),
            View(appliedAt, 3000));

        Assert.Equal(1, accumulator.Snapshot().Lifetime.WouldSheds);
        Assert.Equal(1, Count(accumulator.Snapshot(), b => b.WouldSheds));
    }

    [Fact]
    public void A_raise_for_the_same_reason_is_no_shed()
    {
        // Only a target below what the device was intended at is a shed.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var low = Intended(Start, ("ev_charger", DeviceLevel.At(new Switchboard.Domain.Units.Amperes(6))));
        accumulator.Observe(Step(Start, status: low), View(Start, 1000));
        var at = Start + Poll;
        accumulator.Observe(
            Step(at, [Device(at, "ev_charger", DecisionAction.SetSetpoint, ReasonCode.ReserveThreshold, DeviceLevel.At(new Switchboard.Domain.Units.Amperes(10)), origin: DecisionOrigin.Dispatch)], status: low),
            View(at, 1000));

        Assert.Equal(0, accumulator.Snapshot().Lifetime.WouldSheds);
    }

    [Fact]
    public void A_three_stage_shed_counts_once()
    {
        // A stepped device gets one decision per stage; the user sees one shed of one device.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var on = Intended(Start, ("heater_l1", DeviceLevel.Relay(true)));
        accumulator.Observe(Step(Start, status: on), View(Start, 1000));
        var at = Start + Poll;
        var stages = Enumerable.Range(1, 3).Select(s => Device(at, "heater_l1", DecisionAction.SwitchOff, ReasonCode.GuardTierExceeded, DeviceLevel.Relay(false), stage: s)).ToImmutableArray();
        accumulator.Observe(Step(at, stages, status: on), View(at, 1000));

        Assert.Equal(1, accumulator.Snapshot().Lifetime.WouldSheds);
    }

    [Fact]
    public void Safe_state_and_kill_switch_holds_are_not_sheds()
    {
        // Holding everything off for missing data or the kill switch is not load management.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var on = Intended(Start, ("heater_l1", DeviceLevel.Relay(true)), ("heater_l2", DeviceLevel.Relay(true)), ("heater_l3", DeviceLevel.Relay(true)));
        accumulator.Observe(Step(Start, status: on), View(Start, 1000));
        var at = Start + Poll;
        accumulator.Observe(
            Step(at, [
                Device(at, "heater_l1", DecisionAction.SwitchOff, ReasonCode.SafeState, DeviceLevel.Relay(false)),
                Device(at, "heater_l2", DecisionAction.SwitchOff, ReasonCode.KillSwitch, DeviceLevel.Relay(false)),
                Device(at, "heater_l3", DecisionAction.SwitchOff, ReasonCode.GuardStaleSignal, DeviceLevel.Relay(false)),
            ], status: on),
            View(at, 1000));

        var lifetime = accumulator.Snapshot().Lifetime;
        Assert.Equal((0, 0), (lifetime.Sheds, lifetime.WouldSheds));
    }

    [Fact]
    public void Voltage_below_fraction_counts_one_possible_trip_per_event()
    {
        // L1's voltage collapses (its breaker opened) while L2 and L3 stay up: one possible trip,
        // however long it lasts; a second collapse later is a second event.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var at = Start;
        foreach (var l1 in new double?[] { 230, 0, 0, 0, 231, 230, 3, 229 })
        {
            accumulator.Observe(Step(at, voltages: Volts(l1, 230, 230)), View(at, 500));
            at += Poll;
        }

        var lifetime = accumulator.Snapshot().Lifetime;
        Assert.Equal((2, 0), (lifetime.VoltageLosses, lifetime.SupplyOutages));
    }

    [Fact]
    public void A_loss_on_every_phase_is_a_supply_outage_not_a_trip()
    {
        // All three phases gone at once (a grid outage while Home Assistant runs on a UPS) is not a
        // breaker trip; even when the phases fall one sample apart it is counted once, as an outage.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var at = Start;
        foreach (var (l1, l2, l3) in new (double?, double?, double?)[] { (230, 230, 230), (0, 230, 230), (0, 0, 0), (0, 0, 0), (230, 230, 230) })
        {
            accumulator.Observe(Step(at, voltages: Volts(l1, l2, l3)), View(at, 500));
            at += Poll;
        }

        var snapshot = accumulator.Snapshot();
        Assert.Equal((0, 1), (snapshot.Lifetime.VoltageLosses, snapshot.Lifetime.SupplyOutages));
        Assert.Equal((0, 1), (Count(snapshot, b => b.VoltageLosses), Count(snapshot, b => b.SupplyOutages)));
    }

    [Fact]
    public void Stale_voltage_is_not_measured_and_not_a_loss()
    {
        // A voltage that stopped reporting is not a voltage of 0 V: no loss, and the time is not
        // counted as voltage-observed.
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        var at = Start;
        for (var i = 0; i < 6; i++)
        {
            accumulator.Observe(Step(at, voltages: Volts(null, 230, 230)), View(at, 500));
            at += Poll;
        }

        var snapshot = accumulator.Snapshot();
        Assert.Equal(0, snapshot.Lifetime.VoltageLosses);
        Assert.Equal(TimeSpan.Zero, snapshot.Lifetime.VoltageObserved);
        Assert.True(snapshot.VoltageMeasured);
    }

    [Fact]
    public void Without_a_voltage_signal_breaker_trips_are_not_measured()
    {
        // No phase_voltage mapped: the summary must say "not measured", never "0 trips".
        var accumulator = new EnergyAccumulator(TestStatistics.Options());
        accumulator.Observe(Step(Start, voltageMapped: false), View(Start, 500));

        Assert.False(accumulator.Snapshot().VoltageMeasured);
    }

    [Fact]
    public void Restore_keeps_buckets_within_retention_and_the_high_water_mark()
    {
        // After a restart the history continues; buckets older than the retention window are dropped.
        var options = TestStatistics.Options();
        var first = new EnergyAccumulator(options);
        var end = Run(first, Start, TimeSpan.FromHours(2), at => View(at, 800));
        var state = first.Snapshot().ToState(end);

        var second = new EnergyAccumulator(options);
        second.Restore(state, end + TimeSpan.FromMinutes(5));
        var late = new EnergyAccumulator(options);
        late.Restore(state, end + options.Retention + TimeSpan.FromHours(1));

        Assert.Equal(first.Snapshot().Buckets.Count, second.Snapshot().Buckets.Count);
        Assert.Equal(end, second.Snapshot().IntegratedTo);
        Assert.Empty(late.Snapshot().Buckets);
    }

    private static Dictionary<Phase, double?> Volts(double? l1, double? l2, double? l3) => new() { [Phase.L1] = l1, [Phase.L2] = l2, [Phase.L3] = l3 };

    /// <summary>Steps every 10 s from <paramref name="from"/> for <paramref name="duration"/> (the last step at the end); returns the end.</summary>
    private static DateTimeOffset Run(EnergyAccumulator accumulator, DateTimeOffset from, TimeSpan duration, Func<DateTimeOffset, PowerView> view)
    {
        var end = from + duration;
        for (var at = from; at <= end; at += Poll)
        {
            accumulator.Observe(Step(at), view(at));
        }

        return end;
    }

    private static double Total(EnergyAccumulator accumulator, Func<EnergyBucket, double> value) => accumulator.Snapshot().Buckets.Values.Sum(value);

    private static TimeSpan Sum(StatisticsSnapshot snapshot, Func<EnergyBucket, TimeSpan> value) =>
        snapshot.Buckets.Values.Aggregate(TimeSpan.Zero, (sum, b) => sum + value(b));

    private static int Count(StatisticsSnapshot snapshot, Func<EnergyBucket, int> value) => snapshot.Buckets.Values.Sum(value);
}
