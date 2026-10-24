namespace Switchboard.Domain.Signals;

public enum FlowDirection
{
    Import,
    Export,

    /// <summary>Cautious mode (SIG-03): the value counts as import.</summary>
    Unknown,
}

/// <summary>Where the direction of a current or power value came from.</summary>
public enum DirectionSource
{
    /// <summary>A signed value: either the signal itself, or the signed power named in direction_from.</summary>
    SignedSignal,

    /// <summary>The direction power was within the deadband around zero; counted as import.</summary>
    Deadband,

    /// <summary>
    /// The current changed and the direction power from the same meter poll has not arrived yet;
    /// counted as import until it does or the pairing wait runs out.
    /// </summary>
    Pending,

    /// <summary>The direction power is not fresh or not from the same moment: cautious mode, counted as import.</summary>
    Unavailable,

    /// <summary>The configuration declares no direction source (<c>direction_from: none</c>): permanent cautious mode.</summary>
    NotConfigured,
}

/// <summary>Direction of one current or power value, with where it came from.</summary>
/// <param name="From">The signal the direction was taken from; null when the value is signed itself or none is configured.</param>
public sealed record DirectionInfo(DirectionSource Source, FlowDirection Direction, SignalName? From);
