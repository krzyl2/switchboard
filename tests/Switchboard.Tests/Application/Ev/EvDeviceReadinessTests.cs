using Switchboard.Application.Ev;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Ev;

/// <summary>
/// The EV module's one answer to dispatch (2026-11-09): the charger is not ready while the car is known to be unplugged. Only a known "not plugged" counts:
/// an unknown plugged state (never reported, a lost link) must never take the charger away, or a car that is there would not charge.
/// </summary>
public sealed class EvDeviceReadinessTests
{
    private static readonly DeviceName Charger = DeviceName.Parse("ev_charger");

    [Fact]
    public void The_charger_is_not_ready_only_while_the_car_is_known_to_be_unplugged()
    {
        var rig = new EvRig();
        var readiness = new EvDeviceReadiness(rig);
        Assert.Empty(readiness.NotReady);

        rig.PushPlugged("Idle");
        Assert.Equal([Charger], readiness.NotReady);

        rig.PushPlugged("Wait for car");
        Assert.Empty(readiness.NotReady);

        // The go-e says Complete for a full car: it is plugged in, so it stays ready (its draw, not its plug, tells dispatch it takes nothing).
        rig.PushPlugged("complete");
        Assert.Empty(readiness.NotReady);

        rig.PushPlugged("unavailable");
        Assert.Empty(readiness.NotReady);
    }

    [Fact]
    public void Without_an_ev_section_or_a_valid_configuration_nothing_is_ever_not_ready()
    {
        // Why: the readiness belongs to the EV module's charger; a file without an ev: section names no such device, whatever a plugged reading says.
        var unplugged = EvInputs.Empty with { Plugged = new EvRead(true, SignalState.Fresh, SignalDetail.None, null, null), IsPlugged = false, PluggedState = "Idle" };

        Assert.Empty(new EvDeviceReadiness(new Fixed(new EvInputView(TestConfigurations.Valid(TestDevices.Document(EvRig.Charger)), unplugged))).NotReady);
        Assert.Empty(new EvDeviceReadiness(new Fixed(new EvInputView(null, unplugged))).NotReady);
    }

    private sealed class Fixed(EvInputView view) : IEvInputSource
    {
        public EvInputView Current => view;
    }
}
