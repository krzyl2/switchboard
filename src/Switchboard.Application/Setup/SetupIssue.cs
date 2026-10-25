using System.Collections.Immutable;

namespace Switchboard.Application.Setup;

public enum IssueSeverity
{
    /// <summary>The wizard refuses to continue or save (UI-05).</summary>
    Error,

    /// <summary>Shown next to the field; the save is allowed.</summary>
    Warning,
}

/// <summary>
/// One finding of the wizard's validation, at the draft field it concerns (UI-05: say what to fix,
/// at the exact field). <see cref="Field"/> is the draft path (<c>meter.phases.L1.current.entity</c>,
/// <c>devices[1].phase</c>); null when the problem lies in a part of the file the wizard does not edit,
/// which <see cref="DocumentPath"/> then names. Messages carry no line numbers: the user edits fields,
/// not the file.
/// </summary>
public sealed record SetupIssue(
    string Code,
    IssueSeverity Severity,
    string? Field,
    string? DocumentPath,
    string Message,
    ImmutableDictionary<string, string> Args)
{
    public static SetupIssue Error(string code, string? field, string message, params (string Key, string Value)[] args) =>
        new(code, IssueSeverity.Error, field, null, message, args.ToImmutableDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));

    public static SetupIssue Warning(string code, string? field, string message, params (string Key, string Value)[] args) =>
        new(code, IssueSeverity.Warning, field, null, message, args.ToImmutableDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));
}

/// <summary>The closed list of setup issue codes; the UI renders a sentence per code (en and pl).</summary>
public static class SetupIssueCodes
{
    public const string EntityNotFound = "entity_not_found";
    public const string EntityCheckUnavailable = "entity_check_unavailable";
    public const string EntityUnavailable = "entity_unavailable";
    public const string UnitMismatch = "unit_mismatch";
    public const string DeviceClassMismatch = "device_class_mismatch";
    public const string ValueImplausible = "value_implausible";
    public const string PhaseSumMismatch = "phase_sum_mismatch";
    public const string PhaseSumSkipped = "phase_sum_skipped";
    public const string MaxAgeBelowRefresh = "max_age_below_refresh";
    public const string RefreshNotDetected = "refresh_not_detected";
    public const string DerivedSensorRefused = "derived_sensor_refused";
    public const string DerivedCheckUnavailable = "derived_check_unavailable";

    /// <summary>A measurement input is in no entity registry (YAML without a unique_id), so it may be a template sensor.</summary>
    public const string SourceNotInRegistry = "source_not_in_registry";
    public const string ActuatorDerived = "actuator_derived";
    public const string ReserveBelowMinimum = "reserve_below_minimum";
    public const string PhaseSelfTestMismatch = "phase_self_test_mismatch";
    public const string PhaseSelfTestMissing = "phase_self_test_missing";
    public const string AcknowledgementRequired = "acknowledgement_required";
    public const string CommentsAcknowledgementRequired = "comments_acknowledgement_required";
    public const string ReplaceUnreadableRequired = "replace_unreadable_required";
    public const string RevisionConflict = "revision_conflict";
    public const string ExistingDeviceImmutable = "existing_device_immutable";

    /// <summary>A new device uses the id of a device removed from the file in the same edit.</summary>
    public const string DeviceIdTaken = "device_id_taken";

    /// <summary>A meter max_age far longer than the detected refresh: a frozen meter would be trusted that long.</summary>
    public const string MaxAgeAboveRefresh = "max_age_above_refresh";
    public const string RequiredField = "required_field";
    public const string PreservedPathUnknown = "preserved_path_unknown";

    /// <summary>The runtime parser refused the document; <c>Args["issue"]</c> carries its own configuration issue code.</summary>
    public const string DocumentInvalid = "document_invalid";

    public static ImmutableArray<string> All { get; } =
    [
        EntityNotFound, EntityCheckUnavailable, EntityUnavailable, UnitMismatch, DeviceClassMismatch, ValueImplausible,
        PhaseSumMismatch, PhaseSumSkipped, MaxAgeBelowRefresh, RefreshNotDetected, DerivedSensorRefused, DerivedCheckUnavailable,
        ActuatorDerived, ReserveBelowMinimum, PhaseSelfTestMismatch, PhaseSelfTestMissing, AcknowledgementRequired,
        CommentsAcknowledgementRequired, ReplaceUnreadableRequired, RevisionConflict, ExistingDeviceImmutable, DeviceIdTaken,
        MaxAgeAboveRefresh, RequiredField, PreservedPathUnknown, DocumentInvalid, SourceNotInRegistry,
    ];
}
