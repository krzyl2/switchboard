namespace Switchboard.Domain.Goals;

/// <summary>
/// What a goal aims at and how far it is, by kind (15-PLANNER, Goal sources): an energy goal wants an amount delivered since it began, a
/// SoC goal wants a percent reached. Each kind carries only the fields that mean something for it and works out its own remaining need.
/// </summary>
public abstract record GoalTarget
{
    private protected GoalTarget()
    {
    }

    public abstract GoalKind Kind { get; }
}

/// <summary>An energy goal's target, its progress from the ledger and when it began.</summary>
/// <param name="TargetKwh">The energy to deliver.</param>
/// <param name="DeliveredKwh">Energy already delivered since <paramref name="Since"/>.</param>
/// <param name="Since">When the goal began, from the ledger.</param>
public sealed record EnergyTarget(double TargetKwh, double DeliveredKwh, DateTimeOffset Since) : GoalTarget
{
    public override GoalKind Kind => GoalKind.Energy;

    /// <summary>Energy still to draw, see <see cref="GoalMath.RemainingEnergyKwh"/>.</summary>
    public double Remaining(double lossFraction) => GoalMath.RemainingEnergyKwh(TargetKwh, DeliveredKwh, lossFraction);
}

/// <summary>A SoC goal's target percent and the state of charge now; its progress is the SoC signal and it has no ledger.</summary>
public sealed record SocTarget(double TargetPercent, double CurrentPercent) : GoalTarget
{
    public override GoalKind Kind => GoalKind.Soc;

    /// <summary>Energy still to draw, see <see cref="GoalMath.RemainingSocKwh"/>.</summary>
    public double Remaining(double capacityKwh, double lossFraction) => GoalMath.RemainingSocKwh(TargetPercent, CurrentPercent, capacityKwh, lossFraction);
}
