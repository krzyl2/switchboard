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
