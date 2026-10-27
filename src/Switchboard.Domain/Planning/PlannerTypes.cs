namespace Switchboard.Domain.Planning;

/// <summary>
/// How far the plan may reach into dispatch (<c>planner.dispatch</c>, 15-PLANNER). Slice 1 parses and echoes it; nothing reads it
/// before the plan reaches dispatch (slice 5), and until then every value decides exactly as <see cref="Off"/>.
/// </summary>
public enum PlannerDispatchMode
{
    /// <summary>The plan is shown, dispatch ignores it (the default).</summary>
    Off,

    /// <summary>The plan may only narrow what dispatch would do.</summary>
    Narrow,

    /// <summary>Narrow, plus the class <c>plan</c> and urgent goals.</summary>
    Full,
}

/// <summary>How a daily PV forecast total is spread over daylight (the degraded totals shape).</summary>
public enum PvShape
{
    HalfSine,
}

/// <summary>The optional top-level <c>planner:</c> section of the document; absent means the planner is off and nothing changes.</summary>
public sealed record PlannerDefinition(PlannerDispatchMode Dispatch);

/// <summary>
/// The planner's algorithm parameters (15-PLANNER, Configuration; <c>planner:</c> in control-defaults). Every one comes from
/// configuration, none is a literal in code. Slice 1 binds and validates them; the planner that reads them comes in later slices.
/// </summary>
/// <param name="Cycle">Replanning period.</param>
/// <param name="MinReplanInterval">At most one triggered replan per this interval.</param>
/// <param name="PlanMaxAge">After this a plan is not used by dispatch.</param>
/// <param name="MaxSolveTime">A solve longer than this is dropped.</param>
/// <param name="StartDelay">No plan before the inputs had this long to arrive after a start.</param>
/// <param name="Horizon">How far ahead a plan reaches.</param>
/// <param name="Slot">Length of a plan slot (15, 30 or 60 minutes).</param>
/// <param name="PvDerate">Factor on the PV forecast.</param>
/// <param name="PvShape">The shape of a daily total.</param>
/// <param name="RefinePasses">Refinement passes after the greedy fill.</param>
/// <param name="AdoptCycles">Replans that must agree before the current slot changes.</param>
/// <param name="AdoptMinChangeFraction">Smallest relative budget change that is adopted.</param>
/// <param name="ProgressToleranceFraction">Deviation of goal progress from the plan that triggers a replan.</param>
public sealed record PlannerParameters(
    TimeSpan Cycle,
    TimeSpan MinReplanInterval,
    TimeSpan PlanMaxAge,
    TimeSpan MaxSolveTime,
    TimeSpan StartDelay,
    TimeSpan Horizon,
    TimeSpan Slot,
    double PvDerate,
    PvShape PvShape,
    int RefinePasses,
    int AdoptCycles,
    double AdoptMinChangeFraction,
    double ProgressToleranceFraction,
    HardGoalParameters HardGoal,
    LoadForecastParameters Load,
    PlannerStorageParameters Storage,
    GoalParameters Goal)
{
    /// <summary>
    /// The forecast side of these parameters. The binder has checked every range before a <see cref="PlannerParameters"/> reaches a
    /// valid configuration, so this does not throw for one that came from there.
    /// </summary>
    public ForecastParameters Forecast() => new(Slot, PvDerate, TimeSpan.FromDays(Load.HalflifeDays), Load.MinObservedFraction, Load.FallbackW);
}

/// <param name="EscalationMargin">A hard goal turns urgent this long before its latest grid start.</param>
public sealed record HardGoalParameters(TimeSpan EscalationMargin);

/// <param name="HalflifeDays">Half-life of the load profile's exponential weighting.</param>
/// <param name="MinObservedFraction">An hour counts only when observed for at least this fraction of it.</param>
/// <param name="FallbackW">The house load with no profile at all.</param>
public sealed record LoadForecastParameters(double HalflifeDays, double MinObservedFraction, double FallbackW);

/// <param name="SocStepKwh">Resolution of the storage dynamic programme.</param>
/// <param name="MinSpread">A charge and discharge pair earns only above this price spread.</param>
/// <param name="DegradationPerKwh">Cost of a cycled kWh.</param>
/// <param name="TerminalValue">Value of a kWh left in the battery at the horizon.</param>
public sealed record PlannerStorageParameters(double SocStepKwh, double MinSpread, double DegradationPerKwh, double TerminalValue);

/// <summary>Parameters of the goal model (slice 1).</summary>
/// <param name="InputMaxAge">max_age of a goal input entity that declares none.</param>
/// <param name="DefaultLossFraction">loss_fraction of a goal that declares none.</param>
public sealed record GoalParameters(TimeSpan InputMaxAge, double DefaultLossFraction);
