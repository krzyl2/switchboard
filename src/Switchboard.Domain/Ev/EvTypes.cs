using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Ev;

/// <summary>
/// The optional top-level <c>ev:</c> section (EV-01, docs/EV.md): which device is the car's charger and what it needs to know about
/// the battery. Slice S1 only parses, validates and echoes it; nothing reads it yet, so a file with this section decides exactly as
/// one without. The SoC arrives as a signal (the entity ID stays in the signal layer); range, plugged state and calendar come with S2.
/// </summary>
/// <param name="Device">The controllable device that charges the car; it must exist, and must not carry a <c>goal:</c> of its own.</param>
/// <param name="CapacityKwh">Gross battery capacity.</param>
/// <param name="Soc">The logical name of the state-of-charge signal the section created.</param>
/// <param name="CheapZone">Optional tariff zone id that is "the cheap zone"; null means the lowest price in the price book.</param>
/// <param name="Range">The logical name of the electric-range signal (km); null when the section declares none (EV-02).</param>
/// <param name="Plugged">The logical name of the plugged-state signal; null when the section declares none (EV-02).</param>
/// <param name="PluggedStates">The states of the plugged signal that mean the car is plugged in (compared without regard to case); empty without a plugged signal.</param>
/// <param name="Calendar">The logical name of the calendar signal whose current or next event is the next trip; null when the section declares none (EV-02).</param>
/// <param name="HybridThresholdPercent">The SoC at which the car's electric range reaches zero (a PHEV runs as a hybrid below it); 0 for a car whose whole battery drives it. Consumption is learned from the energy above it.</param>
public sealed record EvDefinition(
    DeviceName Device,
    double CapacityKwh,
    SignalName Soc,
    string? CheapZone,
    SignalName? Range = null,
    SignalName? Plugged = null,
    ImmutableArray<string> PluggedStates = default,
    SignalName? Calendar = null,
    double HybridThresholdPercent = 0);

/// <summary>
/// The EV module's parameters (<c>ev:</c> in control-defaults, docs/EV.md section 6 S1). Every value comes from configuration; none is a
/// literal in code. Bound and range-checked in S1, read by nothing before the module exists (S3 onwards).
/// </summary>
/// <param name="FloorPercent">The reserve: the SoC the car is brought to by the next local <paramref name="FloorBy"/> from PV and the cheapest zone (the planner takes the window's forecast PV first and the rest in the earliest cheap hours); with a trip ready earlier, due then. It stays in the car after a trip. Not the hybrid threshold.</param>
/// <param name="FloorBy">The local time of day the floor is due at; after the day's PV, so a sunny forecast leaves the night empty.</param>
/// <param name="TripMarginPoints">Percentage points on top of the floor and the trip's SoC (target = floor + trip SoC + margin).</param>
/// <param name="CapPercent">Ceiling of surplus-only charging.</param>
/// <param name="WeeklyFull">The weekly full charge.</param>
/// <param name="ReadyMargin">The car is ready this long before a trip starts.</param>
/// <param name="LossFraction">Charging losses: energy drawn = energy stored x (1 + loss).</param>
/// <param name="FallbackKwhPerKm">Consumption used while nothing has been learned.</param>
/// <param name="Learn">How consumption is learned from SoC and range pairs.</param>
/// <param name="MaxTripKm">A trip longer than this is a typo and is ignored.</param>
/// <param name="SocMaxAge">Beyond this the SoC estimate is not trusted and the goals are inactive.</param>
/// <param name="FloorSocMaxAge">The floor alone may plan with an estimate up to this old once it is too old for <paramref name="SocMaxAge"/> (only the age is relaxed).</param>
/// <param name="DriftLimitKwh">Energy that may flow into the car, unseen by the SoC, before the estimate is dropped.</param>
/// <param name="TargetChangeThresholdPoints">A frozen trip target is recomputed only when it moves by more than this many points.</param>
/// <param name="ChargeNow">The manual "charge now" action.</param>
/// <param name="Hold">The manual "don't charge now" action.</param>
/// <param name="Spread">Spread a charge evenly over a long window instead of running at the ceiling.</param>
/// <param name="SnapshotMaxAge">The module's offer is computed beside the control loop and published as a snapshot; one older than this is no offer at all (its goals are reported as failed, never the old ones).</param>
/// <param name="PowerBands">Ceilings of grid charging by local hours, used only while the charge still fits its window under them; empty: none.</param>
public sealed record EvParameters(
    double FloorPercent,
    TimeOnly FloorBy,
    double TripMarginPoints,
    double CapPercent,
    EvWeeklyFullParameters WeeklyFull,
    TimeSpan ReadyMargin,
    double LossFraction,
    double FallbackKwhPerKm,
    EvLearnParameters Learn,
    double MaxTripKm,
    TimeSpan SocMaxAge,
    TimeSpan FloorSocMaxAge,
    double DriftLimitKwh,
    double TargetChangeThresholdPoints,
    EvChargeNowParameters ChargeNow,
    EvHoldParameters Hold,
    bool Spread,
    TimeSpan SnapshotMaxAge,
    ImmutableArray<PowerBand> PowerBands = default);

/// <param name="Days">Local weekdays on which one full charge a week is allowed (a holiday counts as a weekend day in a later slice); empty disables it.</param>
/// <param name="TargetPercent">The SoC of the full charge.</param>
public sealed record EvWeeklyFullParameters(ImmutableArray<DayOfWeek> Days, double TargetPercent);

/// <param name="Smoothing">Weight of a new sample in the smoothed consumption (0 to 1).</param>
/// <param name="MinSoc">A pair is used only at or above this SoC, and only above the car's hybrid threshold (the buffer at the bottom is not representative).</param>
/// <param name="MinKwhPerKm">Plausibility bound: a sample below this is rejected.</param>
/// <param name="MaxKwhPerKm">Plausibility bound: a sample above this is rejected.</param>
/// <param name="MaxSamples">How many samples are kept (bounds the size of the state file); at most <see cref="MaxSamplesLimit"/>, what ev.json can hold.</param>
/// <param name="PairTolerance">A SoC and a range count as one report when their report times differ by at most this (Home Assistant stamps two entities of one car update a moment apart).</param>
public sealed record EvLearnParameters(double Smoothing, double MinSoc, double MinKwhPerKm, double MaxKwhPerKm, int MaxSamples, TimeSpan PairTolerance)
{
    /// <summary>Schema bound, not a control parameter: the most samples ev.json holds. Configuration may not ask for more than the file keeps.</summary>
    public const int MaxSamplesLimit = 200;

    /// <summary>Schema bound, not a control parameter: no car uses more kWh per km than this; ev.json refuses a value above it, so configuration may not allow one.</summary>
    public const double MaxKwhPerKmLimit = 10;
}

/// <param name="TargetPercent">The SoC "charge now" runs to when the request names none.</param>
/// <param name="Duration">A "charge now" that has not reached its target ends after this long.</param>
public sealed record EvChargeNowParameters(double TargetPercent, TimeSpan Duration);

/// <param name="Duration">A "don't charge now" ends after this long unless the car is unplugged or it is cancelled first.</param>
public sealed record EvHoldParameters(TimeSpan Duration);
