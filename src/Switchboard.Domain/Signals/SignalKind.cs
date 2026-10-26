namespace Switchboard.Domain.Signals;

/// <summary>What a signal measures; it decides the allowed units and whether a sign applies.</summary>
public enum SignalKind
{
    Current,
    Power,
    Voltage,
    EnergyCounter,
    State,
    Percentage,

    /// <summary>A price in currency per kWh, from a Home Assistant entity (14-ECONOMICS, slice 4); dispatch only, never protection.</summary>
    Price,
}

/// <summary>
/// How the source writes the direction of flow, seen from the house busbar. Internally import
/// (power flowing into the busbar from the grid, or from local generation) is positive.
/// </summary>
public enum SignConvention
{
    /// <summary>Positive means flow into the busbar; taken as is.</summary>
    PositiveImport,

    /// <summary>Positive means flow out of the busbar (export); negated on the way in.</summary>
    PositiveExport,

    /// <summary>A magnitude only; the direction comes from another signal (SIG-03).</summary>
    Unsigned,
}

/// <summary>What a mapped signal is used for. A signal without a role is shown in diagnostics and consumed by nothing.</summary>
public enum SignalRole
{
    None,

    /// <summary>Main-meter current on one phase: the protection input (SIG-02, SIG-04).</summary>
    PhaseCurrent,

    /// <summary>Main-meter active power on one phase: the dispatch input (SIG-04).</summary>
    PhasePower,

    PhaseVoltage,

    /// <summary>Total inverter production (SIG-08).</summary>
    InverterPower,

    /// <summary>Inverter output current on one phase; splits the production across phases.</summary>
    InverterPhaseCurrent,

    /// <summary>Summed current of all phases; the protection input of the summed-only topology (CONN-01).</summary>
    TotalCurrent,

    /// <summary>Summed active power of all phases.</summary>
    TotalPower,

    /// <summary>
    /// The applied state of a declared device stage, created from <c>devices:</c> (never declared
    /// under <c>signals:</c>): a relay's on/off, or a modulating device's setpoint.
    /// </summary>
    DeviceState,

    /// <summary>A device's own measured current on one phase (its declared draw), created from <c>devices:</c>.</summary>
    DeviceCurrent,

    /// <summary>A device's own measured power on one phase (its declared draw), created from <c>devices:</c>.</summary>
    DevicePower,

    /// <summary>A circuit group's sub-meter current on one phase, created from <c>circuit_groups:</c>.</summary>
    GroupCurrent,

    /// <summary>The main meter's total exported energy counter (kWh, summed phases): the hourly balance input (BAL-01).</summary>
    EnergyExport,

    /// <summary>The main meter's total imported energy counter (kWh, summed phases): the hourly balance input (BAL-01).</summary>
    EnergyImport,

    /// <summary>The import price (what a kWh bought costs) from an entity: feeds <c>economics.tariff</c> through <c>signal:</c> (ECO-10).</summary>
    PriceImport,

    /// <summary>The export price (what a kWh not exported earns) from an entity: feeds <c>economics.settlement.export_price</c> through <c>signal:</c> (ECO-10).</summary>
    PriceExport,
}

/// <summary>
/// How a signal's freshness is judged. Every measurement is fresh only within its max_age of the
/// last report (SIG-05). A device state may instead be declared to report only on change (a
/// Modbus relay): it is then fresh while it is available, with no age limit.
/// </summary>
public enum StateFreshness
{
    Age,

    /// <summary>Fresh while the state is neither unavailable nor unknown, the entity exists and the source is connected.</summary>
    OnChange,
}
