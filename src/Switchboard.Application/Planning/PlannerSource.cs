using System.Collections.Immutable;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>
/// Everything one planning needs and the facts the service compares between plannings to decide whether to replan (15-PLANNER, "Re-planning").
/// </summary>
/// <param name="Input">The pure planner's input.</param>
/// <param name="Inactive">Goals that cannot be evaluated now: a goal becoming active or inactive (a car plugged in or out) is a trigger.</param>
/// <param name="ForecastDigest">A fingerprint of the forecast and price-curve content (not of when it was reported): a change is picked up at the next cycle boundary.</param>
/// <param name="DeviceFingerprint">Which devices answer (a device turning unresponsive or recovering is a trigger).</param>
public sealed record PlannerCapture(PlanInput Input, ImmutableArray<InactiveGoal> Inactive, string ForecastDigest, string DeviceFingerprint);

/// <summary>
/// The planner service's view of the world: a consistent <see cref="PlannerCapture"/> of the latest inputs, or null when there is nothing to plan
/// (no <c>planner:</c> section, no valid configuration). It reads; it never commands.
/// </summary>
public interface IPlannerSource
{
    PlannerCapture? Capture(DateTimeOffset now);
}

/// <summary>
/// Whether the planner must stand aside: the guard is in safe state, the kill switch is engaged, Home Assistant is disconnected. Implemented at the host's edge
/// (the planner reads a yes or no, never the guard's types); a change wakes the service so that leaving the state replans at once.
/// </summary>
public interface IPlannerSuspension
{
    bool Suspended { get; }

    event EventHandler? Changed;
}

/// <summary>A suspension that is never on (no host to ask).</summary>
public sealed class NoSuspension : IPlannerSuspension
{
    public bool Suspended => false;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
/// What the API calls when a goal changed (a manual goal begun or cancelled): ask the planner to look again. The planner service rate limits and decides;
/// the caller commands nothing and waits for nothing. <see cref="PlannerService"/> is the one implementation.
/// </summary>
public interface IPlanTrigger
{
    /// <summary>Asks the loop to look again; cheap, never blocks, safe from any thread.</summary>
    void Wake();
}
