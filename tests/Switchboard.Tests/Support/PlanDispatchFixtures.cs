using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;

namespace Switchboard.Tests.Support;

/// <summary>A plan view the test scripts: a mode and, per device, the entry dispatch is to see (what a valid plan would hand it).</summary>
internal sealed class ScriptedPlanView(PlannerDispatchMode mode) : IPlanView
{
    public PlannerDispatchMode Mode { get; set; } = mode;

    /// <summary>What <see cref="For"/> answers for a device; a device not in it has no entry.</summary>
    public Dictionary<DeviceName, PlanEntryView> Entries { get; } = [];

    /// <summary>How many times dispatch asked, and for which instants it called <see cref="Observe"/>.</summary>
    public int Asked { get; private set; }

    public int Observed { get; private set; }

    public PlanEntryView? For(DeviceName device, DateTimeOffset now)
    {
        Asked++;
        return Mode == PlannerDispatchMode.Off ? null : Entries.GetValueOrDefault(device);
    }

    public void Observe(DateTimeOffset now) => Observed++;

    /// <summary>An entry around <paramref name="now"/> (a slot of an hour, half of it on each side).</summary>
    public static PlanEntryView Entry(DateTimeOffset now, PlanWant want, string? klass = null, double energyKwh = 5, double maxPowerW = 10_000, bool goalAllowsZone = true) =>
        new("plan-1", now - TimeSpan.FromMinutes(5), now - TimeSpan.FromMinutes(30), now + TimeSpan.FromMinutes(30), want, klass, energyKwh, maxPowerW, goalAllowsZone);
}

/// <summary>What is in force now, as the plan view compares a plan with it; every answer can be changed by the test.</summary>
internal sealed class StubCurrency : IPlanCurrency
{
    public PlannerDispatchMode Mode { get; set; } = PlannerDispatchMode.Full;

    public long? ConfigRevision { get; set; } = 1;

    /// <summary>The spec fingerprint per device; a device not in it is not declared.</summary>
    public Dictionary<DeviceName, long> Specs { get; } = [];

    /// <summary>The goals in force per device, by id and revision; a device whose entry is absent has no such goal.</summary>
    public Dictionary<(DeviceName Device, string Id, long Revision), PlanGoalState> Goals { get; } = [];

    /// <summary>What every goal that is not listed answers; null means none.</summary>
    public PlanGoalState? AnyGoal { get; set; }

    public long? SpecIdentity(DeviceName device) => Specs.TryGetValue(device, out var identity) ? identity : null;

    public PlanGoalState? Goal(DeviceName device, string id, long revision, DateTimeOffset now) =>
        Goals.TryGetValue((device, id, revision), out var goal) ? goal : AnyGoal;
}
