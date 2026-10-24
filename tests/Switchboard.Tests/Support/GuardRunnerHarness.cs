using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// The guard as production wires it: the real signal store publishes snapshots into a holder, the
/// guard runner reads them through the protection port, the effector gate and a recording decision
/// recorder take the result. Steps are explicit on a steppable virtual clock.
/// </summary>
internal sealed class GuardRunnerHarness
{
    public GuardRunnerHarness(string yaml, OperatingMode ceiling = OperatingMode.Shadow, OperatingMode uiChoice = OperatingMode.Shadow)
    {
        Time = new SteppableTime(GuardScenario.Start);
        var clock = new SignalClock(Time);
        Store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        Configuration = TestConfigurations.Parse(yaml);
        Store.ApplyConfiguration(Configuration, Now);
        Store.ApplySourceStatus(SignalSourceStatus.Connected);
        Holder = new SignalSnapshotHolder(new SignalDiagnostics(Store.Snapshot(Now), null, [], []));
        Mode = new ModeState(ceiling, uiChoice);
        KillSwitch = new KillSwitchState(Inbox, clock, NullLogger<KillSwitchState>.Instance);
        Engine = new GuardEngine(Lockouts, NullLogger<GuardEngine>.Instance);
        Gate = new EffectorGate(Lockouts, new DecisionIdGenerator("boot1"), NullLogger<EffectorGate>.Instance);
        Status = new GuardStatusHolder(GuardStatus.Initial(Now, Mode.Current), NullLogger<GuardStatusHolder>.Instance);
        Runner = new GuardRunner(Inbox, Holder, clock, Engine, Gate, Lockouts, Recorder, Status, Mode, KillSwitch, NullLogger<GuardRunner>.Instance);
    }

    public SteppableTime Time { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public SignalStore Store { get; }

    public ConfigurationResult Configuration { get; }

    public SignalSnapshotHolder Holder { get; }

    public GuardInbox Inbox { get; } = new();

    public ModeState Mode { get; }

    public KillSwitchState KillSwitch { get; }

    public LockoutClocks Lockouts { get; } = new();

    public GuardEngine Engine { get; }

    public EffectorGate Gate { get; }

    public GuardStatusHolder Status { get; }

    public RecordingDecisionRecorder Recorder { get; } = new();

    public GuardRunner Runner { get; }

    /// <summary>Pushes states as Home Assistant would (reported now) and publishes one store cycle.</summary>
    public void Push(params (string Entity, string State, string? Unit)[] states)
    {
        Store.Apply(new EntityUpdateBatch(BatchOrigin.Change, [.. states.Select(s => new EntityUpdate(EntityId.Parse(s.Entity), s.State, s.Unit, Now, Now))], [], []), Now);
        Publish();
    }

    /// <summary>The reference devices on, the charger at 16 A, and the three meter currents.</summary>
    public void PushReference(double l1 = 8, double l2 = 8, double l3 = 8) => Push(
        ("switch.heater_1", "on", null),
        ("switch.heater_2", "on", null),
        ("switch.heater_3", "on", null),
        ("number.charger_current", "16", "A"),
        ("sensor.meter_current_a", Format(l1), "A"),
        ("sensor.meter_current_b", Format(l2), "A"),
        ("sensor.meter_current_c", Format(l3), "A"));

    public void Publish() => Holder.Publish(new SignalDiagnostics(Store.Snapshot(Now), Store.ConfigurationResult, [], []));

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
