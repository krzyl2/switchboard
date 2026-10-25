using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.Modes;

/// <summary>
/// MODE-02: the UI's mode choice takes effect without a restart: it wakes the guard, the next
/// evaluation records it as a mode decision (mode_shadow), and state.json keeps it. In this version
/// the effective mode stays shadow whatever the choice (no command path exists), and active cannot
/// be chosen at all: storing it now would let a later version with a command path start active on
/// an upgrade without a new deliberate act.
/// </summary>
public sealed class ModeChoiceTests
{
    [Fact]
    public void Active_choice_is_refused_while_commanding_is_not_enabled_and_state_json_unchanged()
    {
        var inbox = new GuardInbox();
        var mode = new ModeState(OperatingMode.Active, OperatingMode.Shadow, inbox);
        var changes = 0;
        mode.Changed += (_, _) => changes++;

        Assert.Null(mode.Choose(OperatingMode.Active));

        Assert.Equal((OperatingMode.Shadow, OperatingMode.Shadow), (mode.Current.UiChoice, mode.Current.Effective));
        Assert.Equal(0, changes);
        Assert.False(inbox.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData(OperatingMode.Shadow)]
    [InlineData(OperatingMode.Active)]
    public void Shadow_ceiling_keeps_shadow_whatever_choice(OperatingMode choice)
    {
        var mode = new ModeState(OperatingMode.Shadow, OperatingMode.Shadow);

        var status = mode.ChooseUnchecked(choice);

        Assert.Equal((OperatingMode.Shadow, choice), (status.Effective, status.UiChoice));
    }

    [Fact]
    public void Active_ceiling_with_an_active_choice_stays_shadow_with_its_note()
    {
        // The mechanism (exercised through the internal test path): even both deliberate acts give
        // shadow here, because no command path exists.
        var status = new ModeState(OperatingMode.Active, OperatingMode.Shadow).ChooseUnchecked(OperatingMode.Active);

        Assert.Equal((OperatingMode.Shadow, ModeNote.ActiveNotEnabled), (status.Effective, status.Note));
    }

    [Fact]
    public void Ui_choice_takes_effect_next_step_without_restart()
    {
        var (loop, mode) = Loop(uiChoice: OperatingMode.Active);
        loop.Runner.Step();
        Assert.Equal(OperatingMode.Active, loop.Status.Current.Mode.UiChoice);

        mode.Choose(OperatingMode.Shadow);
        Assert.True(loop.Inbox.Reader.TryPeek(out var trigger));
        Assert.Equal(GuardTrigger.Mode, trigger);
        loop.Runner.Step();

        Assert.Equal(OperatingMode.Shadow, loop.Status.Current.Mode.UiChoice);
    }

    [Fact]
    public void Mode_change_recorded_as_mode_resolved_mode_shadow()
    {
        var (loop, mode) = Loop(uiChoice: OperatingMode.Active);
        loop.Runner.Step();
        loop.Runner.Step();
        Assert.Single(loop.Recorder.Decisions, d => d.Action == DecisionAction.ModeResolved);

        mode.Choose(OperatingMode.Shadow);
        loop.Runner.Step();

        var records = loop.Recorder.Decisions.Where(d => d.Action == DecisionAction.ModeResolved).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal((ReasonCode.ModeShadow, OperatingMode.Shadow, false), (records[1].Reason, records[1].Inputs.Mode!.UiChoice, records[1].Commanded));
    }

    [Fact]
    public async Task Mode_choice_is_recorded_within_one_evaluation_without_new_samples()
    {
        var (loop, mode) = Loop(uiChoice: OperatingMode.Active);
        var recorded = new TaskCompletionSource<Decision>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Runner.Stepped += (_, step) =>
        {
            if (step.Decisions.FirstOrDefault(d => d.Action == DecisionAction.ModeResolved && d.Inputs.Mode!.UiChoice == OperatingMode.Shadow) is { } decision)
            {
                recorded.TrySetResult(decision);
            }
        };
        using var stop = new CancellationTokenSource();
        var run = loop.Runner.RunAsync(stop.Token);

        mode.Choose(OperatingMode.Shadow);

        Assert.Equal(ReasonCode.ModeShadow, (await recorded.Task.Soon()).Reason);
        await stop.CancelAsync();
        await run.Soon();
    }

    [Fact]
    public async Task Choice_persisted_in_state_json_immediately()
    {
        using var data = new TempDataDirectory();
        var file = new RuntimeStateFile(data.Path, TestDecisions.Options(), NullLogger<RuntimeStateFile>.Instance);
        var loaded = new RuntimeStateLoad(RuntimeState.Fresh with { UiMode = OperatingMode.Active }, StateFileStatus.Loaded, null);
        var coordinator = new RuntimeStateCoordinator(file, loaded, () => null, MsOptions.Create(TestDecisions.Options()), new FakeTimeProvider(), NullLogger<RuntimeStateCoordinator>.Instance);
        var mode = new ModeState(OperatingMode.Active, OperatingMode.Active, new GuardInbox());
        coordinator.Attach(mode);

        mode.Choose(OperatingMode.Shadow);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(OperatingMode.Shadow, file.Load().State.UiMode);
    }

    private static (ControlLoop Loop, ModeState Mode) Loop(OperatingMode uiChoice)
    {
        var time = new SteppableTime(GuardScenario.Start);
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        store.ApplyConfiguration(TestConfigurations.Parse(TestConfigurations.ThreePhase()), time.GetUtcNow());
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(time.GetUtcNow()), null, [], []));
        var inbox = new GuardInbox();
        var mode = new ModeState(OperatingMode.Active, uiChoice, inbox);
        return (new ControlLoop(holder, new SignalClock(time), mode, "bootmode", inbox: inbox), mode);
    }
}
