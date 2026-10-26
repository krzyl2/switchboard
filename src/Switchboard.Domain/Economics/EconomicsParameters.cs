namespace Switchboard.Domain.Economics;

/// <summary>
/// The economic module's algorithm parameters (14-ECONOMICS, <c>economics:</c> in control-defaults); every
/// one comes from configuration, none is a literal in code. Read by dispatch (slice 3): the price test, the value order and the zone-end margin.
/// </summary>
/// <param name="MinMargin">Currency per kWh by which a device's value must exceed the cost of the energy.</param>
/// <param name="OrderStabilityCycles">Boundaries a new value order must be proposed before it is adopted.</param>
/// <param name="OrderMinGapFraction">Relative key difference below which two devices keep their order.</param>
/// <param name="ZoneEndMargin">No zone-class start this close to the end of the zone.</param>
/// <param name="NetBillingWindow">Smoothing window of the instantaneous surplus under net billing.</param>
public sealed record EconomicsParameters(
    double MinMargin,
    int OrderStabilityCycles,
    double OrderMinGapFraction,
    TimeSpan ZoneEndMargin,
    TimeSpan NetBillingWindow);

/// <summary>How a period is settled (D6); parsed in slice 1 and echoed, not used before slice 2.</summary>
public enum SettlementModelId
{
    HourlyNetting,
    NetBilling,
}
