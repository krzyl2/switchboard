using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The pure dispatch rules: allocation with hysteresis (04 §3, DISP-03), phase ranking stability
/// (04 §5, DISP-10), the settling closing rule (DISP-05) and the verification verdicts (DISP-07).
/// </summary>
public sealed class DispatchRulesTests
{
    private static readonly Amperes Start = new(1.3);
    private static readonly Amperes Stop = new(-0.87);

    [Fact]
    public void Start_needs_headroom_above_start_hysteresis()
    {
        // A 6.6 A heater starts only with 6.6 + 1.3 A free; at 7.8 A it stays off.
        Assert.Equal(0, Allocate(7.8, Heater("h", off: true)).Single().Wish);
        Assert.Equal(1, Allocate(7.9, Heater("h", off: true)).Single().Wish);
    }

    [Fact]
    public void Running_device_keeps_its_level_inside_the_stop_hysteresis()
    {
        // Running, the same heater keeps while 6.6 A ≤ A + 0.87 A: at A = 5.8 A it keeps, at 5.7 A
        // it would stop. Start and stop thresholds differ, so it never oscillates on one boundary.
        Assert.Equal(1, Allocate(5.8, Heater("h", off: false)).Single().Wish);
        Assert.Equal(0, Allocate(5.7, Heater("h", off: false)).Single().Wish);
    }

    [Fact]
    public void Higher_value_takes_headroom_first_and_lower_gets_value_rank_below_cutoff()
    {
        // 14 A free: the charger (first in start order) takes 8 A, the heater does not fit after it
        // although it would fit beside the charger's unavoidable 6 A minimum: value_rank_below_cutoff,
        // not headroom_insufficient.
        var result = Allocate(14, Charger(basis: 6, max: 8), Heater("h", off: true));

        Assert.Equal(8, Level(result[0], Charger(basis: 6, max: 8)).Setpoint!.Value.Value);
        Assert.Equal((0, AllocationLimit.Rank), (result[1].Wish, result[1].Limit));
        Assert.Equal(AllocationLimit.Headroom, Allocate(3, Heater("h", off: true)).Single().Limit);
    }

    [Fact]
    public void Three_phase_device_starts_only_with_headroom_on_every_phase()
    {
        var device = new AllocationDevice(
            ThreePhaseHeater,
            [ConstraintKey.Main(Phase.L1), ConstraintKey.Main(Phase.L2), ConstraintKey.Main(Phase.L3)],
            DeviceLevel.Relay(false),
            DeviceLevels.All(ThreePhaseHeater),
            0,
            1);
        var tight = new Dictionary<Phase, double> { [Phase.L1] = 10, [Phase.L2] = 10, [Phase.L3] = 5 };

        Assert.Equal(0, Allocator.Allocate(Request(tight, device)).Devices.Single().Wish);
        tight[Phase.L3] = 10;
        Assert.Equal(1, Allocator.Allocate(Request(tight, device)).Devices.Single().Wish);
    }

    [Fact]
    public void Ceilings_not_draws_are_budgeted_so_an_idle_charger_cannot_lend_headroom()
    {
        // The charger at 16 A draws nothing (its estimate is 0 A, so A = E = 16 A), but its 16 A
        // ceiling is budgeted: all allowed setpoints can be drawn at once and stay under the
        // threshold, so no heater is started into the headroom the car may take any moment.
        var result = Allocate(16, Charger(basis: 16, max: 16), Heater("h", off: true));

        Assert.Equal(16, Level(result[0], Charger(basis: 16, max: 16)).Setpoint!.Value.Value);
        Assert.Equal(0, result[1].Wish);
    }

    [Fact]
    public void A_locked_device_keeps_its_ceiling_reserved_against_devices_worth_more()
    {
        // The heater cannot stop (min_on): its 6.6 A are reserved before the walk, so the charger
        // worth more is not planned into headroom the heater still holds.
        var heater = Heater("h", off: false) with { MinAllowed = 1 };
        var result = Allocate(14, Charger(basis: 6, max: 14), heater);

        Assert.True(Level(result[0], Charger(basis: 6, max: 14)).Setpoint!.Value.Value <= 14 - 6.6 - 1.3 + 1e-9);
        Assert.Equal(1, result[1].Allocated);
    }

    [Fact]
    public void New_ranking_adopted_only_after_stability_cycles()
    {
        // 04 §5: three agreeing boundaries before a new phase order is used.
        var ranking = new PhaseRanking();
        ImmutableArray<Phase> first = [Phase.L1, Phase.L2, Phase.L3];
        ImmutableArray<Phase> second = [Phase.L3, Phase.L1, Phase.L2];
        ranking.Observe(first, 3);

        Assert.Equal(first, ranking.Observe(second, 3));
        Assert.Equal(first, ranking.Observe(second, 3));
        Assert.Equal(second, ranking.Observe(second, 3));
    }

    [Fact]
    public void One_cycle_kettle_does_not_move_the_heater()
    {
        // A kettle on L1 for one boundary flips the proposal once; the adopted order stays.
        var ranking = new PhaseRanking();
        ImmutableArray<Phase> usual = PhaseRanking.Propose(new Dictionary<Phase, double> { [Phase.L1] = 9, [Phase.L2] = 6, [Phase.L3] = 3 });
        ranking.Observe(usual, 3);

        var kettle = PhaseRanking.Propose(new Dictionary<Phase, double> { [Phase.L1] = 1, [Phase.L2] = 6, [Phase.L3] = 3 });
        Assert.Equal(usual, ranking.Observe(kettle, 3));
        Assert.Equal(usual, ranking.Observe(usual, 3));
        Assert.Equal(usual, ranking.Observe(kettle, 3));
        Assert.Equal(usual, ranking.Observe(kettle, 3));
        Assert.Equal([Phase.L1, Phase.L2, Phase.L3], usual);
    }

    [Fact]
    public void Rotation_breaks_ties_by_ranking_and_restore_order_is_the_reverse_of_shed_order()
    {
        // DISP-03 and DISP-10: three identical elements tie on every shed key; the start order puts
        // the element on the best-ranked phase first, and the start order reversed is the shed order.
        var l1 = Heater("heater_l1", off: true).Spec;
        var l2 = l1 with { Id = DeviceName.Parse("heater_l2"), Stages = [new StageSpec(1, [Phase.L2], new Amperes(6.6))] };
        var l3 = l1 with { Id = DeviceName.Parse("heater_l3"), Stages = [new StageSpec(1, [Phase.L3], new Amperes(6.6))] };
        var charger = Charger(basis: 6, max: 20).Spec;
        var group = GroupName.Parse("cwu");
        GroupName? Rotation(DeviceName id) => id == charger.Id ? null : group;

        var order = StartOrder.For([l1, l2, l3, charger], Rotation, [Phase.L3, Phase.L1, Phase.L2]);

        Assert.Equal(["charger", "heater_l3", "heater_l1", "heater_l2"], order.Select(d => d.Id.Value));
        var shed = Switchboard.Domain.Guard.ShedOrder.Rank([l1, l2, l3, charger]);
        Assert.Equal(shed.Last(), order.First().Id);
    }

    [Fact]
    public void Window_closes_only_when_every_balance_input_reported_after_required()
    {
        var t = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var window = new SettlingWindow(t, t.AddSeconds(20), t.AddSeconds(90));
        ClosingSignal[] early = [new(ClosingInput.PhasePower, Phase.L1, t.AddSeconds(25)), new(ClosingInput.PhaseCurrent, Phase.L1, t.AddSeconds(10))];
        ClosingSignal[] late = [new(ClosingInput.PhasePower, Phase.L1, t.AddSeconds(25)), new(ClosingInput.PhaseCurrent, Phase.L1, t.AddSeconds(30))];

        Assert.Equal(SettlingState.Open, SettlingRule.Evaluate(window, early, t.AddSeconds(30)).State);
        Assert.Equal(SettlingState.Closed, SettlingRule.Evaluate(window, late, t.AddSeconds(30)).State);
        var timedOut = SettlingRule.Evaluate(window, early, t.AddSeconds(90));
        Assert.Equal(SettlingState.TimedOut, timedOut.State);
        Assert.Equal(ClosingInput.PhaseCurrent, Assert.Single(timedOut.Silent).Input);
    }

    [Theory]
    [InlineData(1500, 1460, false, Verdict.Match)]
    [InlineData(1500, 0, false, Verdict.Mismatch)]
    [InlineData(1500, 3500, true, Verdict.Inconclusive)]
    [InlineData(1500, 3500, false, Verdict.Mismatch)]
    [InlineData(1500, -500, true, Verdict.Mismatch)]
    [InlineData(-1700, 300, true, Verdict.Inconclusive)]
    [InlineData(-1700, 300, false, Verdict.Mismatch)]
    [InlineData(-1700, -1650, true, Verdict.Match)]
    [InlineData(-1700, -3500, true, Verdict.Mismatch)]
    public void Step_verdicts(double expected, double measured, bool foreign, Verdict verdict)
    {
        // A foreign step is a rise (only rises are flagged), so it explains a measured change above
        // the expected one whatever the commanded direction: more than a switch-on adds, or less
        // than a switch-off removes (a kettle starting while a heater switched off). A missing
        // step (a thermostat that opened) or a drop a rise cannot explain is a mismatch.
        Assert.Equal(verdict, Verification.Judge(new VerificationCase(VerificationKind.Step, expected, measured, 750, foreign)));
    }

    [Fact]
    public void Car_limited_draw_after_setpoint_increase_is_inconclusive_not_mismatch()
    {
        // A setpoint is a ceiling: a car that takes less than the new 16 A (battery full, onboard
        // limit) says nothing about the charger. Only a draw above the ceiling, or a decrease the
        // draw does not follow, is a mismatch.
        Assert.Equal(Verdict.Inconclusive, Verification.Judge(new VerificationCase(VerificationKind.SetpointIncrease, 16, 4, 3, false)));
        Assert.Equal(Verdict.Match, Verification.Judge(new VerificationCase(VerificationKind.SetpointIncrease, 16, 15.5, 3, false)));
        Assert.Equal(Verdict.Mismatch, Verification.Judge(new VerificationCase(VerificationKind.SetpointIncrease, 16, 20, 1, false)));
        Assert.Equal(Verdict.Mismatch, Verification.Judge(new VerificationCase(VerificationKind.SetpointDecrease, 6, 16, 1, false)));
        Assert.Equal(Verdict.Match, Verification.Judge(new VerificationCase(VerificationKind.SetpointDecrease, 6, 6.2, 1, false)));
    }

    private static readonly DeviceSpec ThreePhaseHeater = new(
        DeviceName.Parse("boiler"),
        DeviceType.OnOff,
        [new StageSpec(1, [Phase.L1, Phase.L2, Phase.L3], new Amperes(6.6))],
        null,
        0.3,
        null,
        TimeSpan.FromSeconds(1),
        false,
        TimeSpan.FromMinutes(6),
        TimeSpan.FromSeconds(90));

    private static AllocationDevice Heater(string name, bool off)
    {
        var spec = ThreePhaseHeater with { Id = DeviceName.Parse(name), Stages = [new StageSpec(1, [Phase.L1], new Amperes(6.6))], ValuePerKwh = 0.35 };
        var levels = DeviceLevels.All(spec);
        return new AllocationDevice(spec, [ConstraintKey.Main(Phase.L1)], DeviceLevel.Relay(!off), levels, 0, 1);
    }

    private static AllocationDevice Charger(double basis, double max)
    {
        var spec = new DeviceSpec(
            DeviceName.Parse("charger"),
            DeviceType.Modulating,
            [new StageSpec(1, [Phase.L1], new Amperes(20))],
            new ModulatingRange(new Amperes(6), new Amperes(20), new Amperes(1)),
            2.03,
            null,
            TimeSpan.FromSeconds(20),
            false,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60));
        var levels = DeviceLevels.All(spec);
        return new AllocationDevice(spec, [ConstraintKey.Main(Phase.L1)], DeviceLevel.At(new Amperes(basis)), levels, 0, (int)(max - 6));
    }

    private static DeviceLevel Level(DeviceAllocation allocation, AllocationDevice device) => device.Levels[allocation.Wish];

    private static ImmutableArray<DeviceAllocation> Allocate(double available, params AllocationDevice[] devices) =>
        Allocator.Allocate(Request(new Dictionary<Phase, double> { [Phase.L1] = available }, devices)).Devices;

    private static AllocationRequest Request(Dictionary<Phase, double> available, params AllocationDevice[] devices) =>
        new([.. devices], available.ToImmutableDictionary(a => ConstraintKey.Main(a.Key), a => new Amperes(a.Value)), Start, Stop);
}
