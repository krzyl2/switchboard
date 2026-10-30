using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Configuration;

/// <summary>
/// One declared signal: the only place where a logical name meets an entity ID (SIG-01).
/// <see cref="Sigma"/> is already normalized to A, W or V.
/// </summary>
/// <param name="DirectionFrom">The signed power an unsigned signal takes its direction from.</param>
/// <param name="DirectionDeclaredUnavailable"><c>direction_from: none</c>: permanent cautious mode (SIG-03).</param>
/// <param name="UnavailableAsZero">Inverter power only: Home Assistant's unavailable means 0 W (an inverter asleep at night).</param>
/// <param name="DeclaredAt">
/// Where the signal was declared when not under <c>signals:</c>: the device that created it
/// (<c>devices[0]</c>), for issues raised at run time.
/// </param>
public sealed record SignalDefinition(
    SignalName Name,
    EntityId Entity,
    SignalKind Kind,
    string? Unit,
    SignConvention? Sign,
    SignalName? DirectionFrom,
    bool DirectionDeclaredUnavailable,
    TimeSpan MaxAge,
    double? Sigma,
    SignalRole Role,
    Phase? Phase,
    bool UnavailableAsZero,
    string? DeclaredAt = null)
{
    /// <summary>The dotted path of this signal in the document, for issues raised at run time.</summary>
    public string Path => DeclaredAt ?? $"signals.{Name}";

    private readonly StateFreshness _freshness;

    /// <summary>
    /// Age (the default) or on-change. On-change is refused for anything but a device state and the EV plugged state (a discrete state
    /// that reports only when it changes): a measurement or protection input that stops reporting must go stale (SIG-05).
    /// </summary>
    public StateFreshness Freshness
    {
        get => _freshness;
        init => _freshness = value == StateFreshness.OnChange && Role is not (SignalRole.DeviceState or SignalRole.EvPlugged)
            ? throw new ArgumentException($"Signal {Name} has role {Role}: only a device state or the EV plugged state may report on change.", nameof(value))
            : value;
    }

    /// <summary>
    /// For a price signal: the currency label (<c>economics.currency</c>) its unit must carry, set once the economics section is read;
    /// null when there is none, and then no sample of it is usable (the signal layer refuses it as a unit mismatch).
    /// </summary>
    public string? PriceCurrency { get; init; }

    /// <summary>For a series signal (15-PLANNER, slice 2): the one attribute kept and how it is laid out; null for every other signal.</summary>
    public SeriesSpec? Series { get; init; }

    /// <summary>For a timestamp signal: the one attribute whose text is the instant (<c>next_rising</c> of the sun entity), instead of the entity's state; null otherwise.</summary>
    public string? Attribute { get; init; }

    /// <summary>For a series signal: what its numbers mean (power W, period energy Wh, or a price); null otherwise.</summary>
    public SeriesValueKind? SeriesMeaning { get; init; }

    /// <summary>The dotted path of the key that sets this signal's max_age.</summary>
    public string MaxAgePath => DeclaredAt is null ? $"{Path}.max_age" : MaxAgeKey ?? $"{Path}.state_max_age";

    /// <summary>For a signal created by a device or group: the full path of the key that sets its max_age, when it is not state_max_age.</summary>
    public string? MaxAgeKey { get; init; }
}

/// <summary>
/// A validated configuration document (schema version 1). <see cref="Signals"/> holds the signals
/// declared under <c>signals:</c> followed by the signals devices and groups create (applied
/// states, draws, sub-meters).
/// </summary>
public sealed record SwitchboardConfiguration(
    ConnectionProfile Connection,
    ImmutableArray<SignalDefinition> Signals,
    ControlParameters Control,
    ImmutableArray<DeviceDefinition> Devices)
{
    /// <summary>The circuit groups (CONN-03); empty when none is declared.</summary>
    public ImmutableArray<CircuitGroupDefinition> Groups { get; init; } = [];

    /// <summary>The optional <c>economics:</c> section (ECO-02); null when the file has none, and then nothing changes.</summary>
    public EconomicsDefinition? Economics { get; init; }

    /// <summary>The optional <c>planner:</c> section (PLAN-05); null when the file has none, and then the planner is off and nothing changes.</summary>
    public PlannerDefinition? Planner { get; init; }

    /// <summary>The optional <c>ev:</c> section (EV-01); null when the file has none, or its device has a goal of its own. Read by nothing in this slice: the module comes later.</summary>
    public EvDefinition? Ev { get; init; }

    /// <summary>The phases that have a mapped phase-voltage signal: where a power-only draw can be turned into a current.</summary>
    public ImmutableArray<Phase> VoltagePhases => [.. Signals.Where(s => s.Role == SignalRole.PhaseVoltage && s.Phase is not null).Select(s => s.Phase!.Value).Distinct().Order()];
}
