using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Modes;

/// <summary>
/// MODE-02 (maintainer survey 2026-10-25): commands need two deliberate acts, the add-on option
/// active (the ceiling) and the global control switch on, plus a valid configuration and a confirmed
/// state. Anything missing keeps the effective mode in shadow and is named, so the UI can say
/// exactly what stops control; every change of what runs is a mode decision at once, without a
/// restart and without new samples.
/// </summary>
public sealed class ModeResolutionTests
{
    [Fact]
    public void Effective_active_needs_option_switch_valid_config_and_confirmed_state()
    {
        Assert.Equal(OperatingMode.Active, ModeState.Resolve(OperatingMode.Active, true, true, true).Effective);

        // Each condition alone keeps it in shadow: no single act or circumstance can make it active.
        Assert.Equal(OperatingMode.Shadow, ModeState.Resolve(OperatingMode.Shadow, true, true, true).Effective);
        Assert.Equal(OperatingMode.Shadow, ModeState.Resolve(OperatingMode.Active, false, true, true).Effective);
        Assert.Equal(OperatingMode.Shadow, ModeState.Resolve(OperatingMode.Active, true, false, true).Effective);
        Assert.Equal(OperatingMode.Shadow, ModeState.Resolve(OperatingMode.Active, true, true, false).Effective);
    }

    [Fact]
    public void Missing_conditions_are_named_exactly()
    {
        Assert.Equal(
            [ModeCondition.OptionShadow, ModeCondition.ControlOff, ModeCondition.ConfigurationInvalid, ModeCondition.StateUnconfirmed],
            ModeState.Resolve(OperatingMode.Shadow, false, false, false).Missing);
        Assert.Equal([ModeCondition.ControlOff], ModeState.Resolve(OperatingMode.Active, false, true, true).Missing);
        Assert.Empty(ModeState.Resolve(OperatingMode.Active, true, true, true).Missing);
    }

    [Fact]
    public void Only_a_missing_configuration_alone_leaves_the_safe_states_decreases()
    {
        // 04 §8: an invalid configuration under an active option with control allowed still lets the
        // safe state's offs reach the last valid devices; with the switch off nothing leaves at all.
        Assert.True(ModeState.Resolve(OperatingMode.Active, true, false, true).DecreasesOnly);
        Assert.False(ModeState.Resolve(OperatingMode.Active, false, false, true).Commands);
        Assert.False(ModeState.Resolve(OperatingMode.Shadow, true, false, true).Commands);
    }

    [Fact]
    public void Each_effective_mode_change_records_ModeResolved_in_the_same_step()
    {
        var (loop, _) = Loop(OperatingMode.Active);
        loop.Runner.Step();
        var first = Assert.Single(loop.Recorder.Decisions, d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal((ReasonCode.ModeShadow, OperatingMode.Shadow), (first.Reason, first.Mode));
        Assert.Equal([ModeCondition.ControlOff], first.Inputs.Mode!.Missing);

        Assert.Equal(ControlSetResult.Applied, loop.Control!.Set(true, ControlSwitchSource.Api));
        Assert.True(loop.Inbox.Reader.TryPeek(out var trigger));
        Assert.Equal(GuardTrigger.Mode, trigger);
        loop.Runner.Step();

        var records = loop.Recorder.Decisions.Where(d => d.Action == DecisionAction.ModeResolved).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal((ReasonCode.ModeActive, OperatingMode.Active, true), (records[1].Reason, records[1].Mode, records[1].Inputs.Mode!.ControlAllowed));
        Assert.Empty(records[1].Inputs.Mode!.Missing);
        Assert.Equal(ControlSwitchSource.Api, records[1].Inputs.Mode!.Source);
        Assert.Equal(OperatingMode.Active, loop.Status.Current.Mode.Effective);

        loop.Control.Set(false, ControlSwitchSource.Mqtt);
        loop.Runner.Step();
        var off = loop.Recorder.Decisions.Last(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal((ReasonCode.ModeShadow, OperatingMode.Shadow), (off.Reason, off.Mode));
    }

    [Fact]
    public void Switch_under_a_shadow_option_is_recorded_and_changes_nothing_else()
    {
        // Allowed under a shadow option: persisted and recorded, and missing says option_shadow.
        var (loop, _) = Loop(OperatingMode.Shadow);
        loop.Runner.Step();
        loop.Control!.Set(true, ControlSwitchSource.Api);
        loop.Runner.Step();

        var last = loop.Recorder.Decisions.Last(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal((OperatingMode.Shadow, true), (last.Mode, last.Inputs.Mode!.ControlAllowed));
        Assert.Equal([ModeCondition.OptionShadow], last.Inputs.Mode.Missing);
    }

    [Fact]
    public void A_broken_file_in_shadow_adds_no_mode_records_but_under_control_it_is_safe_state()
    {
        // In shadow the configuration's validity changes nothing that runs: no record per broken
        // edit. With the option active and control allowed it decides whether commands leave.
        var shadow = new TestInputs(valid: true);
        var (quiet, _) = Loop(OperatingMode.Shadow, shadow);
        quiet.Runner.Step();
        shadow.Valid = false;
        quiet.Runner.Step();
        Assert.Single(quiet.Recorder.Decisions, d => d.Action == DecisionAction.ModeResolved);

        var active = new TestInputs(valid: true);
        var (loop, _) = Loop(OperatingMode.Active, active);
        loop.Control!.Set(true, ControlSwitchSource.Api);
        loop.Runner.Step();
        active.Valid = false;
        loop.Runner.Step();

        var last = loop.Recorder.Decisions.Last(d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal((ReasonCode.SafeState, OperatingMode.Shadow), (last.Reason, last.Mode));
        Assert.Equal([ModeCondition.ConfigurationInvalid], last.Inputs.Mode!.Missing);
    }

    [Fact]
    public async Task Control_change_is_recorded_within_one_evaluation_without_new_samples()
    {
        var (loop, _) = Loop(OperatingMode.Active);
        var recorded = new TaskCompletionSource<Decision>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Runner.Stepped += (_, step) =>
        {
            if (step.Decisions.FirstOrDefault(d => d.Action == DecisionAction.ModeResolved && d.Inputs.Mode!.ControlAllowed == true) is { } decision)
            {
                recorded.TrySetResult(decision);
            }
        };
        using var stop = new CancellationTokenSource();
        var run = loop.Runner.RunAsync(stop.Token);

        loop.Control!.Set(true, ControlSwitchSource.Mqtt);

        Assert.Equal(ReasonCode.ModeActive, (await recorded.Task.Soon()).Reason);
        await stop.CancelAsync();
        await run.Soon();
    }

    private static (ControlLoop Loop, SteppableTime Time) Loop(OperatingMode ceiling, IControlInputSource? inputs = null)
    {
        var time = new SteppableTime(GuardScenario.Start);
        return (ControlLoop.WithControl(inputs ?? Holder(time), new SignalClock(time), ceiling, "bootmode"), time);
    }

    private static SignalSnapshotHolder Holder(SteppableTime time)
    {
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        store.ApplyConfiguration(TestConfigurations.Parse(TestConfigurations.ThreePhase()), time.GetUtcNow());
        return new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(time.GetUtcNow()), null, [], []));
    }

    /// <summary>Control inputs whose configuration validity a test flips (a reload that broke the file, and the fix).</summary>
    private sealed class TestInputs(bool valid) : IControlInputSource
    {
        private readonly ControlInputs _valid = Inputs(true);
        private readonly ControlInputs _invalid = Inputs(false);

        public event EventHandler<ControlInputs>? Published
        {
            add { }
            remove { }
        }

        public bool Valid { get; set; } = valid;

        public ControlInputs Current => Valid ? _valid : _invalid;

        private static ControlInputs Inputs(bool valid)
        {
            var scenario = new GuardScenario(TestDevices.Document());
            scenario.ConfigurationValid(valid);
            return new ControlInputs(scenario.Inputs(), scenario.DispatchInputs(), valid ? scenario.DispatchSetup : null);
        }
    }
}
