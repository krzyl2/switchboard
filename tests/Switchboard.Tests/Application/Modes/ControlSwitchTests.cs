using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Modes;

/// <summary>
/// The global "control allowed" switch (MODE-02, maintainer survey 2026-10-25): off after install,
/// on only by an explicit act, and switching it off stops the command path before anything else
/// happens. After an untrusted state.json it is locked off with the kill switch engaged until the
/// user confirms; confirming is not allowing.
/// </summary>
public sealed class ControlSwitchTests
{
    private readonly SteppableTime _time = new(GuardScenario.Start);
    private readonly GuardInbox _inbox = new();
    private readonly KillSwitchState _killSwitch;
    private readonly ControlSwitchState _control;

    public ControlSwitchTests()
    {
        var clock = new SignalClock(_time);
        _killSwitch = new KillSwitchState(_inbox, clock, NullLogger<KillSwitchState>.Instance);
        _control = new ControlSwitchState(_inbox, _killSwitch, clock, NullLogger<ControlSwitchState>.Instance);
    }

    [Fact]
    public void A_fresh_install_has_control_off()
    {
        Assert.Equal(ControlSwitchStatus.Default, _control.Status);
        Assert.False(_control.Status.Allowed);
    }

    [Fact]
    public void Turning_it_on_and_off_is_recorded_with_who_and_when_and_wakes_the_guard()
    {
        var changes = new List<ControlSwitchStatus>();
        _control.Changed += (_, status) => changes.Add(status);

        Assert.Equal(ControlSetResult.Applied, _control.Set(true, ControlSwitchSource.Mqtt));
        Assert.Equal(ControlSetResult.Unchanged, _control.Set(true, ControlSwitchSource.Api));

        Assert.Equal([(true, ControlSwitchSource.Mqtt, (DateTimeOffset?)GuardScenario.Start)], changes.Select(c => (c.Allowed, c.Source, c.ChangedAt)));
        Assert.True(_inbox.Reader.TryRead(out var trigger));
        Assert.Equal(GuardTrigger.Mode, trigger);
    }

    [Fact]
    public void Switching_off_closes_the_command_path_before_the_guard_is_woken_or_anyone_is_told()
    {
        _control.Set(true, ControlSwitchSource.Api);
        while (_inbox.Reader.TryRead(out _))
        {
        }

        var order = new List<string>();
        _control.Closing += (_, _) => order.Add(_inbox.Reader.TryPeek(out _) ? "closing after wake" : "closing");
        _control.Changed += (_, _) => order.Add("changed");

        _control.Set(false, ControlSwitchSource.Api);

        Assert.Equal(["closing", "changed"], order);
    }

    [Fact]
    public void Turning_it_on_closes_nothing()
    {
        var closed = 0;
        _control.Closing += (_, _) => closed++;

        _control.Set(true, ControlSwitchSource.Api);

        Assert.Equal(0, closed);
    }

    [Fact]
    public void A_failing_listener_never_undoes_the_switch()
    {
        _control.Changed += (_, _) => throw new IOException("disk full");
        _control.Closing += (_, _) => throw new InvalidOperationException("port gone");

        _control.Set(true, ControlSwitchSource.Api);
        Assert.Equal(ControlSetResult.Applied, _control.Set(false, ControlSwitchSource.Api));

        Assert.False(_control.Status.Allowed);
    }

    [Fact]
    public void An_untrusted_state_file_locks_control_off_and_holds_the_kill_switch_on()
    {
        _control.Lock(ControlLockReason.StateFileCorrupt);

        Assert.Equal((false, true, ControlLockReason.StateFileCorrupt), (_control.Status.Allowed, _control.Status.Locked, _control.Status.LockReason));
        Assert.Equal((true, KillSwitchSource.CorruptState, true), (_killSwitch.Status.Engaged, _killSwitch.Status.Source, _killSwitch.Status.Locked));
        Assert.Equal(ControlSetResult.RefusedLocked, _control.Set(true, ControlSwitchSource.Api));
        Assert.Equal(KillSwitchSetResult.RefusedLocked, _killSwitch.Set(false, KillSwitchSource.Mqtt));
        Assert.False(_control.Status.Allowed);
        Assert.True(_killSwitch.Status.Engaged);
    }

    [Fact]
    public void Confirming_releases_the_lock_and_the_kill_switch_but_does_not_allow_control()
    {
        _control.Lock(ControlLockReason.StateFileCorrupt);

        Assert.True(_control.Confirm());

        Assert.Equal((false, false), (_control.Status.Allowed, _control.Status.Locked));
        Assert.Equal((false, KillSwitchSource.Confirmation, false), (_killSwitch.Status.Engaged, _killSwitch.Status.Source, _killSwitch.Status.Locked));
        Assert.False(_control.Confirm());
        Assert.Equal(ControlSetResult.Applied, _control.Set(true, ControlSwitchSource.Api));
    }

    [Fact]
    public void Nothing_to_confirm_without_a_lock()
    {
        Assert.False(_control.Confirm());
        Assert.False(_killSwitch.Unlock());
    }

    [Fact]
    public void A_restored_switch_keeps_its_value_and_says_it_was_restored()
    {
        _control.Restore(new ControlSwitchStatus(true, GuardScenario.Start.AddDays(-1), ControlSwitchSource.Api, false, null));

        Assert.Equal((true, ControlSwitchSource.Restored), (_control.Status.Allowed, _control.Status.Source));
    }

    [Fact]
    public void A_never_set_switch_restores_as_default_and_off()
    {
        _control.Restore(ControlSwitchStatus.Default);

        Assert.Equal(ControlSwitchStatus.Default, _control.Status);
    }
}
