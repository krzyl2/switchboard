namespace Switchboard.Application.Setup;

/// <summary>
/// The configuration as the setup wizard edits it: the parts of switchboard.yaml (schema version 1)
/// the wizard owns, in the shape of its five steps. It is mapped onto the parsed document, so every
/// key it does not own is kept as written. A null field removes its key, so the shipped default
/// applies again. Text stays text here (a topology, a duration): the runtime parser judges it after
/// mapping, and its issues are reported at the draft field they came from.
/// </summary>
public sealed record SetupDraft
{
    public ConnectionDraft Connection { get; init; } = new();

    public MeterDraft Meter { get; init; } = new();

    public SourcesDraft Sources { get; init; } = new();

    /// <summary>Loads and storage, in document order (storage devices are devices of type storage).</summary>
    public IReadOnlyList<DeviceDraft> Devices { get; init; } = [];

    public AcknowledgementsDraft Acknowledgements { get; init; } = new();

    /// <summary>Paths of preserved sections (from GET api/setup/draft) the user confirmed to remove, e.g. an invalid <c>control</c>.</summary>
    public IReadOnlyList<string> DropPreserved { get; init; } = [];
}

/// <summary>Step 2 (CONN-01, CONN-04). Topology and breaker rating are also asked at the top of step 1.</summary>
public sealed record ConnectionDraft
{
    /// <summary>three_phase, three_phase_summed or single_phase.</summary>
    public string? Topology { get; init; }

    public double? BreakerRatingA { get; init; }

    /// <summary>B, C or unknown; never prefilled.</summary>
    public string? Characteristic { get; init; }

    public double? VoltageV { get; init; }

    public PhaseValues? ReserveW { get; init; }
}

public sealed record PhaseValues
{
    public double? L1 { get; init; }

    public double? L2 { get; init; }

    public double? L3 { get; init; }
}

/// <summary>Step 1: the main meter (SIG-02 … SIG-04, SIG-07).</summary>
public sealed record MeterDraft
{
    /// <summary>positive_import, positive_export or unsigned, for every phase current and the total current.</summary>
    public string? CurrentSign { get; init; }

    /// <summary>For an unsigned current: phase_power (direction from the power of the same phase) or none (cautious mode).</summary>
    public string? CurrentDirection { get; init; }

    /// <summary>positive_import or positive_export, for every phase power and the total power.</summary>
    public string? PowerSign { get; init; }

    public MeterPhases Phases { get; init; } = new();

    public MeterTotal Total { get; init; } = new();
}

public sealed record MeterPhases
{
    public MeterPhaseDraft? L1 { get; init; }

    public MeterPhaseDraft? L2 { get; init; }

    public MeterPhaseDraft? L3 { get; init; }
}

public sealed record MeterPhaseDraft
{
    public SignalDraft? Current { get; init; }

    public SignalDraft? Power { get; init; }

    public SignalDraft? Voltage { get; init; }
}

public sealed record MeterTotal
{
    public SignalDraft? Power { get; init; }

    /// <summary>Required by the three_phase_summed topology (its protection input).</summary>
    public SignalDraft? Current { get; init; }
}

/// <summary>One mapped entity: the owned keys entity, unit and max_age of its signal.</summary>
public sealed record SignalDraft
{
    public string? Entity { get; init; }

    public string? Unit { get; init; }

    /// <summary>A duration as switchboard.yaml writes it (90s).</summary>
    public string? MaxAge { get; init; }
}

/// <summary>Step 3. Storage is a device of type storage in <see cref="SetupDraft.Devices"/>.</summary>
public sealed record SourcesDraft
{
    public InverterDraft? Inverter { get; init; }
}

public sealed record InverterDraft
{
    public SignalDraft? Power { get; init; }

    /// <summary>positive_import (production flows into the busbar) or positive_export.</summary>
    public string? Sign { get; init; }

    /// <summary>Home Assistant's unavailable (and silence after 0 W) counts as 0 W: an inverter asleep at night.</summary>
    public bool? UnavailableAsZero { get; init; }

    /// <summary>Null for a three-phase inverter; L1, L2 or L3 for a single-phase one.</summary>
    public string? Phase { get; init; }

    /// <summary>The inverter's output current per phase (all or none); splits production across phases.</summary>
    public InverterPhaseCurrents? PhaseCurrents { get; init; }
}

public sealed record InverterPhaseCurrents
{
    public SignalDraft? L1 { get; init; }

    public SignalDraft? L2 { get; init; }

    public SignalDraft? L3 { get; init; }
}

/// <summary>
/// Step 4 (UI-02, CONN-02): one device. <see cref="Existing"/> and <see cref="Editable"/> are what
/// the server reported; on a save the server decides both again from the file by id, and uses
/// <see cref="Existing"/> only to refuse a new device that reuses an id of the file. An existing
/// device keeps its id and type; a device the wizard cannot represent (stepped, a per-phase draw) is
/// kept as written and can only be removed.
/// </summary>
public sealed record DeviceDraft
{
    public string? Id { get; init; }

    public bool Existing { get; init; }

    public bool Editable { get; init; } = true;

    /// <summary>The template card it was created from (ev_charger, water_heater_element, heat_pump, generic_onoff); display only.</summary>
    public string? Template { get; init; }

    public string? Name { get; init; }

    /// <summary>onoff, modulating or storage (stepped devices are kept as written).</summary>
    public string? Type { get; init; }

    public string? Actuator { get; init; }

    public string? StateEntity { get; init; }

    /// <summary>age or on_change.</summary>
    public string? StateReports { get; init; }

    /// <summary>L1, L2, L3 or three_phase.</summary>
    public string? Phase { get; init; }

    public double? RatedCurrentA { get; init; }

    public double? RatedPowerW { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Step { get; init; }

    public string? ReactionTime { get; init; }

    public string? MinOn { get; init; }

    public string? MinOff { get; init; }

    public double? ValuePerKwh { get; init; }

    public string? CircuitGroup { get; init; }

    public string? RotationGroup { get; init; }

    public DrawDraft? Draw { get; init; }

    /// <summary>A modulating device's stop actuator (a switch or a select that disables it); suggested by discovery, never preselected: only the user declares it.</summary>
    public string? StopActuator { get; init; }

    /// <summary>A select stop actuator's option that stops the device (<c>stop_option</c>); written only for a select stop.</summary>
    public string? StopOption { get; init; }

    /// <summary>A select stop actuator's option that lets the device run again (<c>resume_option</c>); written only for a select stop.</summary>
    public string? ResumeOption { get; init; }

    /// <summary>restore_only or headroom (<c>start:</c>); written only when it differs from the default or the file already had it.</summary>
    public string? Start { get; init; }
}

/// <summary>A single-phase device's own draw sensors.</summary>
public sealed record DrawDraft
{
    public string? Current { get; init; }

    public string? Power { get; init; }

    public string? MaxAge { get; init; }

    /// <summary>The unit the current entity reports in (A or mA); null for A, the parser's default.</summary>
    public string? CurrentUnit { get; init; }

    /// <summary>The unit the power entity reports in (W or kW); null for W, the parser's default.</summary>
    public string? PowerUnit { get; init; }
}

/// <summary>Step 5 and the save: what the user confirmed.</summary>
public sealed record AcknowledgementsDraft
{
    /// <summary>SAFE-06: the user read that Switchboard is not a protective device.</summary>
    public bool NotProtective { get; init; }

    /// <summary>The hand-written file has comments, which a structured save cannot keep (the backup keeps them).</summary>
    public bool CommentsDropped { get; init; }

    /// <summary>The current file cannot be read as YAML at all; saving replaces it (the original is kept as a backup).</summary>
    public bool ReplaceUnreadable { get; init; }
}
