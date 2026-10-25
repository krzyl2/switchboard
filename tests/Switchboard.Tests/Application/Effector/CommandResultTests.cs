using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Effector;

/// <summary>
/// What a command's outcome means (Stage C): "commanded" only after Home Assistant acknowledged it;
/// a device whose reported state does not show an acknowledged command within its reaction time and
/// the confirmation timeout is flagged and counted as one mismatch; a command the link could not
/// carry is never held against the device; after a reconnect the protective target a device does
/// not show is sent again; and a failing protective off keeps being sent at every boundary while
/// its decision is recorded once.
/// </summary>
public sealed class CommandResultTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";

    /// <summary>The heaters with Modbus-like states: reported on change only (confirmation applies).</summary>
    private static readonly string OnChangeHeaters = DispatchDevices.Heaters.ReplaceLineEndings("\n").Replace("    value_per_kwh: 0.35", "    value_per_kwh: 0.35\n    state_reports: on_change", StringComparison.Ordinal);

    [Fact]
    public void An_acknowledged_command_the_device_does_not_show_is_flagged_and_counted_once()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(OnChangeHeaters, ["heater_l1", "heater_l2", "heater_l3"], OneBoundary, mode: DispatchScenario.Active, port: port);

        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);
        var on = s.Decisions.First(d => d.Action == DecisionAction.SwitchOn);
        var device = on.Device!.Value.Value;
        Assert.True(on.Commanded);
        Assert.Null(s.Ledger(device)!.UnconfirmedSince);

        s.PollUntil(on.At + TimeSpan.FromMinutes(3), 3, 3, 3);

        // Neither the state nor the meter followed: one mismatch for the one command, not two.
        var entry = s.Ledger(device)!;
        Assert.NotNull(entry.UnconfirmedSince);
        Assert.Equal(1, entry.Mismatches);
    }

    [Fact]
    public void A_device_that_follows_is_never_flagged()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(OnChangeHeaters, ["heater_l1", "heater_l2", "heater_l3"], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);

        var on = s.Decisions.First(d => d.Action == DecisionAction.SwitchOn && d.Commanded);
        Assert.Null(s.Ledger(on.Device!.Value.Value)!.UnconfirmedSince);
    }

    [Fact]
    public void A_command_the_link_could_not_carry_is_not_commanded_and_not_held_against_the_device()
    {
        var port = new RecordingDeviceCommandPort { Answer = _ => (CommandOutcome.NotSent, CommandNotSentReason.LinkDown) };
        var s = DispatchScenario.Create(OnChangeHeaters, ["heater_l1", "heater_l2", "heater_l3"], OneBoundary, mode: DispatchScenario.Active, port: port);

        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);

        Assert.NotEmpty(port.Submitted);
        Assert.DoesNotContain(s.Decisions, d => d.Commanded);
        Assert.Equal(0, s.Ledger("heater_l1")?.Mismatches ?? 0);
        Assert.Null(s.Ledger("heater_l1")?.UnresponsiveSince);
    }

    [Fact]
    public void A_failed_increase_is_taken_back_and_recorded_as_not_commanded()
    {
        var port = new RecordingDeviceCommandPort { Answer = c => c.On ? (CommandOutcome.Failed, null) : (CommandOutcome.Acknowledged, null) };
        var s = DispatchScenario.Create(OnChangeHeaters, ["heater_l1", "heater_l2", "heater_l3"], OneBoundary, mode: DispatchScenario.Active, port: port);

        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);

        var on = s.Decisions.First(d => d.Action == DecisionAction.SwitchOn);
        Assert.Equal((false, ReasonCode.DeviceUnresponsive), (on.Commanded, on.RefusedBy));
        Assert.False(s.Ledger(on.Device!.Value.Value)!.Target?.Relays[0] ?? false);
    }

    [Fact]
    public void A_failed_dispatch_off_keeps_being_sent_so_a_later_guard_shed_still_reaches_the_device()
    {
        // A dispatch off Home Assistant refused (a Modbus write error) leaves the relay on. Were the
        // ledger to keep "off" as if it had happened, the guard's later shed would equal the target
        // and never leave: protection silently not carried out. A lowering target that was not
        // acknowledged is sent again at every boundary while the device still shows otherwise.
        var failOffs = true;
        var port = new RecordingDeviceCommandPort { Answer = c => !c.On && failOffs ? (CommandOutcome.Failed, null) : (CommandOutcome.Acknowledged, null) };
        var s = DispatchScenario.Create(OnChangeHeaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 13.6, 3, 3);

        // L1 above In − reserve, below the breaker: dispatch (not the guard) switches heater_l1 off.
        s.PollUntil(T0 + TimeSpan.FromMinutes(3), 15.6, 3, 3);
        var off = s.DecisionsFor("heater_l1").First(d => d.Origin == DecisionOrigin.Dispatch && d.Action == DecisionAction.SwitchOff);
        Assert.Equal((false, ReasonCode.DeviceUnresponsive), (off.Commanded, off.RefusedBy));
        var refused = port.Commands.Count(c => c == "heater_l1 relay 1 off");

        // The kettle goes on top: the guard must take heater_l1 off for real, and now it can.
        failOffs = false;
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 33, 3, 3);

        Assert.Contains(s.DecisionsFor("heater_l1"), d => d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff && d.At > off.At);
        Assert.True(port.Commands.Count(c => c == "heater_l1 relay 1 off") > refused, "the off never left again after the first one failed");
    }

    [Fact]
    public void Stale_meter_data_with_control_in_force_switches_the_devices_of_that_phase_off()
    {
        // SAFE-01, strict: the L1 meter current goes stale (the signal layer marks it past its
        // max_age); every device on L1 is switched off for real at once (no grace), the other phases
        // are left alone.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(OnChangeHeaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);
        Assert.Empty(port.Submitted);

        s.Invalidate(Switchboard.Domain.Signals.Phase.L1, Switchboard.Domain.Signals.ProtectionInvalidReason.CurrentStale);
        s.RunFor(TimeSpan.FromMinutes(1));

        Assert.Equal(["heater_l1 relay 1 off"], port.Commands);
        var off = Assert.Single(s.DecisionsFor("heater_l1"), d => d.Action == DecisionAction.SwitchOff);
        Assert.Equal((ReasonCode.GuardStaleSignal, DecisionOrigin.Guard, true), (off.Reason, off.Origin!.Value, off.Commanded));
    }

    [Fact]
    public void When_home_assistant_is_lost_the_safe_states_offs_are_sent_as_soon_as_it_is_back()
    {
        // SAFE-01 on a lost link: every device is held off at once, but nothing can leave while the
        // link is down (recorded as not commanded). The new link's states still show the heaters on,
        // so the held offs go out with it, before anything else.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(OnChangeHeaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);

        port.Answer = _ => (CommandOutcome.NotSent, CommandNotSentReason.LinkDown);
        foreach (var device in new[] { "heater_l1", "heater_l2", "heater_l3" })
        {
            s.SetState(DeviceName.Parse(device), 1, null, DeviceStateInvalidReason.SourceDisconnected);
        }

        foreach (var phase in new[] { Switchboard.Domain.Signals.Phase.L1, Switchboard.Domain.Signals.Phase.L2, Switchboard.Domain.Signals.Phase.L3 })
        {
            s.Invalidate(phase, Switchboard.Domain.Signals.ProtectionInvalidReason.SourceDisconnected, evaluate: false);
        }

        s.Evaluate();
        var held = s.Decisions.Where(d => d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff).ToList();
        Assert.Equal(3, held.Count);
        Assert.All(held, d => Assert.False(d.Commanded));
        var before = port.Commands.Count;

        port.Answer = _ => (CommandOutcome.Acknowledged, null);
        port.LinkGeneration++;
        foreach (var device in new[] { "heater_l1", "heater_l2", "heater_l3" })
        {
            s.SetState(DeviceName.Parse(device), 1, new RelayApplied(true));
        }

        s.PollUntil(s.Now + TimeSpan.FromSeconds(10), 3, 3, 3);

        Assert.Equal(["heater_l1 relay 1 off", "heater_l2 relay 1 off", "heater_l3 relay 1 off"], port.Commands.Skip(before).Order(StringComparer.Ordinal));
        Assert.All(port.Submitted.Skip(before), c => Assert.Equal(CommandPriority.Protective, c.Priority));
    }

    [Fact]
    public void A_command_replaced_before_it_left_is_not_recorded_as_a_change_limit()
    {
        // change_limit (v3) means "an increase deferred because another increase is in flight". A
        // command the port replaced by a newer one for the same actuator, or a device re-pointed at
        // another entity, is not that: the decision must not say so.
        var port = new RecordingDeviceCommandPort { Answer = _ => (CommandOutcome.NotSent, CommandNotSentReason.Superseded) };
        var s = DispatchScenario.Create(OnChangeHeaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);

        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 33, 3, 3);

        var shed = s.DecisionsFor("heater_l1").First(d => d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff);
        Assert.False(shed.Commanded);
        Assert.NotEqual(ReasonCode.ChangeLimit, shed.RefusedBy);
        Assert.Equal(ReasonCode.SafeState, shed.RefusedBy);
    }

    [Fact]
    public void After_a_reconnect_a_protective_off_the_device_does_not_show_is_sent_again()
    {
        // The off was acknowledged, but the link dropped before the relay switched (lost with the
        // link): the new link's states still show it on, so the off is re-asserted once.
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(OnChangeHeaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 33, 3, 3);
        var offs = port.Commands.Count(c => c == "heater_l1 relay 1 off");
        Assert.True(offs >= 1);

        port.LinkGeneration++;
        s.PollUntil(T0 + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(20), 33, 3, 3);

        Assert.Equal(offs + 1, port.Commands.Count(c => c == "heater_l1 relay 1 off"));
        Assert.True(port.Submitted.Last(c => c.Device.Value == "heater_l1").Decrease);
    }

    [Fact]
    public void A_failing_protective_off_is_sent_again_at_every_boundary_and_recorded_once()
    {
        var port = new RecordingDeviceCommandPort { Answer = c => c.On ? (CommandOutcome.Acknowledged, null) : (CommandOutcome.Failed, null) };
        var s = DispatchScenario.Create(OnChangeHeaters, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(1), 3, 3, 3);

        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 33, 3, 3);

        var sent = port.Commands.Count(c => c == "heater_l1 relay 1 off");
        Assert.True(sent >= 3, $"the failing off was sent {sent} times in 3 minutes of overload");
        // The shed itself, and its resends recorded once (marked reasserted), however many there were.
        var sheds = s.DecisionsFor("heater_l1").Where(d => d.Origin == DecisionOrigin.Guard && d.Action == DecisionAction.SwitchOff).ToList();
        Assert.Equal([false, true], sheds.Select(d => d.Inputs.Dispatch?.Reasserted ?? false));
        Assert.All(sheds, d => Assert.Equal((false, ReasonCode.DeviceUnresponsive), (d.Commanded, d.RefusedBy)));
    }
}
