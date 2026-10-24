using Switchboard.Domain.Units;

namespace Switchboard.Domain.Signals;

/// <summary>A normalized signal value, typed by kind. Current and power follow the internal convention: import positive.</summary>
public abstract record SignalValue;

public sealed record CurrentValue(Amperes Current) : SignalValue;

public sealed record PowerValue(Watts Power) : SignalValue;

public sealed record VoltageValue(Volts Voltage) : SignalValue;

public sealed record EnergyValue(KilowattHours Energy) : SignalValue;

public sealed record PercentageValue(Percent Percentage) : SignalValue;

public sealed record StateValue(string State) : SignalValue;
