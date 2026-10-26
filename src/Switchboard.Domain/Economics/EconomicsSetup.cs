using System.Collections.Immutable;
using Switchboard.Domain.Devices;

namespace Switchboard.Domain.Economics;

/// <summary>
/// What dispatch needs of one device's economics (14-ECONOMICS): its value model and the energy it may take. It travels
/// beside the <see cref="DeviceSpec"/>, never inside it: the spec is in the protection inputs, and the guard must not be able
/// to reach a price type.
/// </summary>
/// <param name="Value">The static value, the price it follows and the factor.</param>
/// <param name="Sources">The sources after narrowing to the zones the tariff has; null when the device declares none (0.9 behaviour).</param>
public sealed record DeviceEconomics(DeviceName Device, ValueSpec Value, SourceSet? Sources);

/// <summary>
/// The economics of a configuration as dispatch reads it (slice 3): the two price specs, the holidays the zones need, the
/// settlement model that decides whether surplus energy is wanted and the algorithm parameters. Null on the dispatch setup
/// without an <c>economics:</c> section, and then dispatch behaves exactly as in 0.9. The local time zone is not part of it:
/// the engine is handed the injected clock's zone, as the hourly balance is.
/// </summary>
/// <param name="Tariff">What a kWh bought costs; null when the section has no tariff.</param>
/// <param name="ExportPrice">What a kWh not exported earns; null when none is configured (the surplus price test is then off).</param>
public sealed record EconomicsSetup(
    PriceSpec? Tariff,
    PriceSpec? ExportPrice,
    HolidayCalendar Holidays,
    ISettlementModel Settlement,
    EconomicsParameters Parameters,
    ImmutableArray<DeviceEconomics> Devices)
{
    public DeviceEconomics? Device(DeviceName name) => Devices.FirstOrDefault(d => d.Device == name);

    /// <summary>At least one device's value follows a price: only then does the start order move with prices.</summary>
    public bool OrderFollowsPrices => Devices.Any(d => d.Value.Follows != ValueFollows.Static);
}
