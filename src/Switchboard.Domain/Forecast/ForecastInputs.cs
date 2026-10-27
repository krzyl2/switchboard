using System.Collections.Immutable;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Forecast;

/// <summary>
/// One forecast or curve signal as the planner may read it (15-PLANNER, "Inputs and their signal roles"): its state and, only
/// while it is usable (fresh by <c>last_reported</c> within its max_age), its content. A stale, unavailable or refused signal
/// carries no content: the last known curve is never used (the planner then plans zero PV, or the tariff schedule).
/// Coverage is separate from freshness (<see cref="CoversUntil"/>).
/// </summary>
/// <param name="Series">The series of a kind-series signal (a PV forecast or a price curve) while fresh.</param>
/// <param name="EnergyKwh">The day total of a kind-energy_forecast signal, in kWh, while fresh.</param>
/// <param name="At">The instant of a kind-timestamp signal (sunrise, sunset) while fresh.</param>
public sealed record ForecastSample(
    SignalRole Role,
    SignalKind Kind,
    SignalState State,
    SignalDetail Detail,
    DateTimeOffset? ReportedAt,
    TimeSeries? Series,
    double? EnergyKwh,
    DateTimeOffset? At)
{
    /// <summary>Fresh and carrying content; nothing else may be planned on.</summary>
    public bool Usable => State == SignalState.Fresh && (Series is not null || EnergyKwh is not null || At is not null);

    /// <summary>Where the series ends; null for a scalar, and for a series that is not usable.</summary>
    public DateTimeOffset? CoversUntil => Series?.CoversUntil;
}

/// <summary>
/// The forecast and price-curve signals of one store cycle, beside <c>PriceInputs</c> and never inside <c>ProtectionInputs</c> or
/// <c>DispatchInputs</c>: a forecast must not be able to reach the guard or move a dispatch decision (SIG-04 by type; an
/// architecture test keeps the guard assembly free of these types). Keyed by the logical signal name; no entity is known here.
/// </summary>
public sealed record ForecastInputs(ImmutableDictionary<SignalName, ForecastSample> Samples)
{
    public static ForecastInputs Empty { get; } = new(ImmutableDictionary<SignalName, ForecastSample>.Empty);

    public ForecastSample? Sample(SignalName signal) => Samples.TryGetValue(signal, out var sample) ? sample : null;

    /// <summary>The first signal with the role, in name order (a role is unique in a valid document).</summary>
    public ForecastSample? ForRole(SignalRole role) =>
        Samples.OrderBy(s => s.Key.Value, StringComparer.Ordinal).Select(s => s.Value).FirstOrDefault(s => s.Role == role);

    /// <summary>The usable sample of a role, or null (never a stale one).</summary>
    public ForecastSample? Usable(SignalRole role) => ForRole(role) is { Usable: true } sample ? sample : null;
}
