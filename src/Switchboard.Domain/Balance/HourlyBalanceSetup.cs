using Switchboard.Domain.Units;

namespace Switchboard.Domain.Balance;

/// <summary>
/// What dispatch needs of the hourly balance beside its own port: the parameters and the nominal
/// voltage that turns a setpoint device's ceiling in amperes into the power its start would add
/// (converted here once, like the hysteresis, because the dispatch port carries watts only).
/// </summary>
public sealed record HourlyBalanceSetup(HourlyBalanceParameters Parameters, Volts Voltage);
