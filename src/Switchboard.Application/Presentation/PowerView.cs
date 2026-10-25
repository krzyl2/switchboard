using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Presentation;

/// <summary>Why a power figure is not known. Never shown as 0: the UI says "unknown" with this reason.</summary>
public enum PowerUnknownReason
{
    ConfigurationInvalid,
    SourceDisconnected,
    PowerUnmapped,
    PowerNotFresh,
    GenerationUnknown,

    /// <summary>A device on the phase has no known power (a stale draw, an unknown state, a setpoint without a draw).</summary>
    DevicePowerUnknown,

    /// <summary>Nothing of that kind is declared (no inverter, no storage device): counted as 0 in the balance, shown as "not declared".</summary>
    NotDeclared,
}

/// <summary>A note on a known generation figure.</summary>
public enum GenerationNote
{
    /// <summary>The inverter is asleep (unavailable, or silent after reporting 0 W) and counted as 0 W.</summary>
    AsleepCountedAsZero,
}

/// <summary>One power figure in W (import and consumption positive), or why it is not known; with the report time of its oldest measured input.</summary>
public sealed record Quantity(double? Value, PowerUnknownReason? Unknown, DateTimeOffset? ReportedAt)
{
    public static Quantity Known(double value, DateTimeOffset? reportedAt) => new(value, null, reportedAt);

    public static Quantity Missing(PowerUnknownReason reason) => new(null, reason, null);

    /// <summary>The value the balance identity uses: a known value, or 0 for something not declared; null otherwise.</summary>
    public double? InBalance => Value ?? (Unknown == PowerUnknownReason.NotDeclared ? 0 : null);
}

/// <summary>
/// The power balance of a phase or of the installation (W). Generation + net import = load, and
/// load = controlled + storage + uncontrolled: <see cref="Uncontrolled"/> is the only residual (the
/// house's own consumption, "house load" on the dashboard), so a negative one means the model of
/// the controlled devices claims more than the house draws (<see cref="ModelExceedsLoad"/>).
/// </summary>
/// <param name="Load">Everything consumed behind the meter (net import + generation, SIG-08).</param>
/// <param name="Controlled">Σ of the declared non-storage devices (measured draw, else the rated model).</param>
/// <param name="UnknownDevices">The devices whose unknown power makes <see cref="Controlled"/> or <see cref="Storage"/> unknown.</param>
/// <param name="Storage">Σ of the storage devices' signed draw (charging positive).</param>
public sealed record BalanceParts(
    Quantity Generation,
    GenerationNote? GenerationNote,
    Quantity NetImport,
    Quantity Load,
    Quantity Controlled,
    ImmutableArray<DeviceName> UnknownDevices,
    Quantity Storage,
    Quantity Uncontrolled,
    bool ModelExceedsLoad);

/// <summary>A segment of a phase bar.</summary>
public enum SegmentKind
{
    /// <summary>The house's steady load (uncontrolled minus a foreign step).</summary>
    Base,

    /// <summary>The declared devices.</summary>
    Controlled,

    /// <summary>A foreign load step above the house's baseline (04 §4).</summary>
    Foreign,

    /// <summary>The phase exports: the whole current flows out.</summary>
    Export,

    /// <summary>What cannot be split while a device's power is unknown.</summary>
    Unattributed,
}

/// <summary>A bar segment in amperes (scaled to fit the measured current) and the power it stands for (unscaled).</summary>
public sealed record BarSegment(SegmentKind Kind, Amperes Current, Watts? Power);

/// <summary>One phase for the dashboard and the flow view.</summary>
/// <param name="Current">Measured, import positive (cautious mode: the magnitude counted as import).</param>
/// <param name="Voltage">The fresh phase voltage, else the nominal one (<paramref name="VoltageMeasured"/> says which).</param>
/// <param name="ForeignStep">Uncontrolled load above the foreign-load baseline (≥ 0); null while either is unknown.</param>
/// <param name="ForeignDetectedAt">The last foreign load event on the phase within the current excursion.</param>
/// <param name="BarScaled">Controlled and foreign did not fit under the measured current (local generation covers part of the load) and were scaled.</param>
/// <param name="OverReserve">
/// Over the reserve threshold: the measured import current, or the estimate I_est (signed, the
/// guard's own comparison). Either alone counts: in shadow mode a decided reduction relieves the
/// estimate while the meter still reads the same, and a decided start adds to it before it draws.
/// </param>
public sealed record PhasePower(
    Phase Phase,
    Amperes? Current,
    FlowDirection? Direction,
    DateTimeOffset? CurrentReportedAt,
    Amperes ReserveThreshold,
    Amperes Rating,
    Amperes Continuous,
    Volts Voltage,
    bool VoltageMeasured,
    BalanceParts Balance,
    Watts? ForeignStep,
    Watts? ForeignBaseline,
    DateTimeOffset? ForeignDetectedAt,
    ImmutableArray<BarSegment> Bar,
    bool BarScaled,
    bool OverReserve);

/// <summary>A device's power on one phase with the report time of its measurement (null for the rated model).</summary>
public sealed record DevicePhasePowerView(Phase Phase, Watts? Power, PowerSource Source, DateTimeOffset? ReportedAt);

/// <summary>A device's power: per phase, the total and where it comes from (unknown if any phase is).</summary>
public sealed record DevicePowerView(DeviceName Device, ImmutableArray<DevicePhasePowerView> Phases, Watts? Total, PowerSource Source);

/// <summary>When the planned reaction to an over-reserve phase is due.</summary>
public enum ReactionTiming
{
    /// <summary>At once (shed everything controlled on the phase).</summary>
    Now,

    /// <summary>At the next phase current sample (shed one device).</summary>
    NextSample,

    /// <summary>At the next dispatch cycle boundary.</summary>
    NextBoundary,

    /// <summary>At the latest by the given boundary (the persistence of the reserve request or the ordinary cycle): an upper bound.</summary>
    ByBoundary,

    /// <summary>Nothing can be reduced: the phase exports, and shedding load would only raise the current.</summary>
    CannotReduceExporting,
}

/// <summary>A device change the banner names: a reduction dispatch or protection intends, or one waiting for its lockout.</summary>
public sealed record PlannedChange(DeviceName Device, DeviceLevel? From, DeviceLevel To, DecisionOrigin? Origin, ReasonCode? Reason, DateTimeOffset? DueAt);

/// <summary>A phase above its reserve threshold: what is over, what caused it and what would happen when.</summary>
/// <param name="Current">The measured current (signed, import positive).</param>
/// <param name="EffectiveCurrent">The estimate I_est with the changes already decided; null without valid data.</param>
/// <param name="Reaction">The tier reaction the guard acts on (shed-all from the measured band, the rest from the estimate's).</param>
public sealed record ReserveBanner(
    Phase Phase,
    Amperes Current,
    Amperes? EffectiveCurrent,
    Amperes Threshold,
    Watts? ForeignStep,
    DateTimeOffset? ForeignDetectedAt,
    TierReaction? Reaction,
    bool ReductionRequested,
    ReactionTiming Timing,
    DateTimeOffset? DueAt,
    ImmutableArray<PlannedChange> Planned);

/// <summary>
/// The power picture of one control step, built from the step alone (<see cref="PowerViewBuilder"/>):
/// the balance per phase and in total, the phase bars, every device's power and the banners.
/// </summary>
/// <param name="InvalidReason">The first phase without valid dispatch data; null when all are valid.</param>
/// <param name="Cycle">The dispatch cycle (zero without a valid configuration).</param>
public sealed record PowerView(
    DateTimeOffset EvaluatedAt,
    bool DataValid,
    DispatchInvalidReason? InvalidReason,
    BalanceParts Total,
    ImmutableArray<PhasePower> Phases,
    ImmutableArray<DevicePowerView> Devices,
    ImmutableArray<ReserveBanner> Banners,
    TimeSpan Cycle);
