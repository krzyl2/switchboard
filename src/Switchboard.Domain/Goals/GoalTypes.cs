using Switchboard.Domain.Devices;

namespace Switchboard.Domain.Goals;

/// <summary>What a goal asks for: an amount of energy, or a state of charge.</summary>
public enum GoalKind
{
    /// <summary>Deliver <c>target_kwh</c> by the deadline; progress is the energy the device drew since the goal began.</summary>
    Energy,

    /// <summary>Reach a target percent of <c>capacity_kwh</c> by the deadline; progress is the SoC signal itself.</summary>
    Soc,
}

/// <summary>
/// Where a goal came from. The numeric order is the precedence for one device and one deadline: manual over module over entity
/// over config; the losing request is shown, not applied.
/// </summary>
public enum GoalOrigin
{
    /// <summary>The device's goal block with literal values only.</summary>
    Config = 0,

    /// <summary>The device's goal block with at least one target, deadline or want read from a mapped entity.</summary>
    Entity = 1,

    /// <summary>A module that turns something else (the EV module's calendar trips) into requests.</summary>
    Module = 2,

    /// <summary>A goal the user stored in the add-on's own state (slice 7).</summary>
    Manual = 3,
}

/// <summary>What happens after the goal is met.</summary>
public enum GoalOnMet
{
    /// <summary>The device is a v2 opportunist again.</summary>
    Opportunistic,

    /// <summary>The device stays off: the target is also a cap.</summary>
    Stop,
}

/// <summary>Where a goal stands. Slice 1 reports met, expired, missed, on track and inactive; at risk and infeasible need the plan (slice 4).</summary>
public enum GoalStatus
{
    OnTrack,
    AtRisk,
    Infeasible,
    Met,

    /// <summary>A soft goal whose deadline passed with the need unmet: it lapses.</summary>
    Expired,

    /// <summary>A hard goal whose deadline passed with the need unmet: shown, still pursued until met or removed.</summary>
    Missed,

    Inactive,
}

/// <summary>The inputs of a goal, for naming which one failed.</summary>
public enum GoalInputKind
{
    Soc,
    Target,
    Deadline,
    Want,
}

/// <summary>Why a goal is inactive: never guessed, never an old value kept.</summary>
public enum GoalInactiveReason
{
    /// <summary>The <c>want</c> input says the device does not want the goal now.</summary>
    WantOff,

    /// <summary>The input has not arrived yet, or its entity does not exist.</summary>
    InputMissing,

    /// <summary>The input did not report within its max_age.</summary>
    InputStale,

    /// <summary>Home Assistant reports the input unavailable or unknown.</summary>
    InputUnavailable,

    /// <summary>The value cannot be read (not a number, a wrong unit, not a time, not on or off).</summary>
    InputInvalid,

    /// <summary>The value is a number or time but outside what the goal can use (a percentage above 100).</summary>
    InputOutOfRange,
}

/// <summary>A goal that cannot be evaluated now, with the input that failed (the want gate for <see cref="GoalInactiveReason.WantOff"/>).</summary>
public sealed record InactiveGoal(DeviceName Device, GoalInactiveReason Reason, GoalInputKind Input, GoalOrigin Origin, GoalKind Kind, bool Hard);
