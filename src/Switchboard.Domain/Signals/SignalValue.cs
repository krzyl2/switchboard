using Switchboard.Domain.Units;

namespace Switchboard.Domain.Signals;

/// <summary>A normalized signal value, typed by kind. Current and power follow the internal convention: import positive.</summary>
public abstract record SignalValue;

public sealed record CurrentValue(Amperes Current) : SignalValue;

public sealed record PowerValue(Watts Power) : SignalValue;

public sealed record VoltageValue(Volts Voltage) : SignalValue;

public sealed record EnergyValue(KilowattHours Energy) : SignalValue;

public sealed record PercentageValue(Percent Percentage) : SignalValue;

/// <summary>A price in currency per kWh (the signal layer converted a <c>/MWh</c> unit); may be negative on a market, the consumer decides what that means.</summary>
public sealed record PriceValue(double PerKwh) : SignalValue;

/// <summary>A forecast energy in kWh (a day total); the signal layer converted Wh and MWh.</summary>
public sealed record EnergyForecastValue(KilowattHours Energy) : SignalValue;

/// <summary>An instant (a state of kind timestamp), always with the offset Home Assistant wrote.</summary>
public sealed record TimestampValue(DateTimeOffset At) : SignalValue;

/// <summary>A normalized time series (forecast or price curve), time-sorted, finite, bounded.</summary>
public sealed record SeriesValue(TimeSeries Series) : SignalValue;

public sealed record StateValue(string State) : SignalValue;

/// <summary>A distance in km (the signal layer converted mi). Never negative.</summary>
public sealed record DistanceValue(double Kilometers) : SignalValue;

/// <summary>
/// One event of a calendar as its entity exposes it. <see cref="Start"/> and <see cref="End"/> are instants: the entity writes naive local
/// times, and the signal layer read them in the add-on's zone. <see cref="Description"/> is never longer than the schema bound
/// (an event beyond it is refused whole).
/// </summary>
public sealed record CalendarEvent(string Message, string Description, DateTimeOffset Start, DateTimeOffset End, bool AllDay);

/// <summary>
/// The current or next event of a calendar entity; <see cref="Event"/> is null when the calendar has none. <see cref="ZoneName"/> is the zone
/// the entity says its times are in, when it says so at all (null otherwise).
/// </summary>
public sealed record CalendarEventValue(CalendarEvent? Event, string? ZoneName = null) : SignalValue;
