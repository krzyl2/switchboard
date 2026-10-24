using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Configuration;

/// <summary>
/// One declared signal: the only place where a logical name meets an entity ID (SIG-01).
/// <see cref="Sigma"/> is already normalized to A, W or V.
/// </summary>
/// <param name="DirectionFrom">The signed power an unsigned signal takes its direction from.</param>
/// <param name="DirectionDeclaredUnavailable"><c>direction_from: none</c>: permanent cautious mode (SIG-03).</param>
/// <param name="UnavailableAsZero">Inverter power only: Home Assistant's unavailable means 0 W (an inverter asleep at night).</param>
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
    bool UnavailableAsZero)
{
    /// <summary>The dotted path of this signal in the document, for issues raised at run time.</summary>
    public string Path => $"signals.{Name}";
}

/// <summary>A validated configuration document (schema version 1).</summary>
public sealed record SwitchboardConfiguration(
    ConnectionProfile Connection,
    ImmutableArray<SignalDefinition> Signals,
    ControlParameters Control);
