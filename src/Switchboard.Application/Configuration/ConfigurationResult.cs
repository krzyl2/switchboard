using System.Collections.Immutable;

namespace Switchboard.Application.Configuration;

/// <summary>
/// One problem in the configuration document. <see cref="Path"/> is dotted
/// (<c>signals.grid_current_l1.direction_from</c>); line and column are 1-based and present only
/// for positions in the user's own document.
/// </summary>
public sealed record ConfigurationIssue(string Code, string Path, int? Line, int? Column, string Message)
{
    /// <summary>The log form: <c>path:line:col code message</c>.</summary>
    public override string ToString() =>
        Line is { } line ? $"{Path}:{line}:{Column} {Code} {Message}" : $"{Path} {Code} {Message}";
}

/// <summary>The closed list of issue codes; the UI renders a sentence per code.</summary>
public static class ConfigurationIssueCodes
{
    public const string FileMissing = "file_missing";
    public const string FileUnreadable = "file_unreadable";
    public const string FileTooLarge = "file_too_large";
    public const string YamlSyntax = "yaml_syntax";
    public const string YamlAliasNotAllowed = "yaml_alias_not_allowed";
    public const string YamlTagNotAllowed = "yaml_tag_not_allowed";
    public const string YamlMultipleDocuments = "yaml_multiple_documents";
    public const string DuplicateKey = "duplicate_key";
    public const string UnknownKey = "unknown_key";
    public const string MissingKey = "missing_key";
    public const string KeyNotAllowed = "key_not_allowed";
    public const string WrongType = "wrong_type";
    public const string InvalidValue = "invalid_value";
    public const string OutOfRange = "out_of_range";
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string InvalidSignalName = "invalid_signal_name";
    public const string InvalidEntityId = "invalid_entity_id";
    public const string DuplicateEntity = "duplicate_entity";
    public const string UnitNotAllowedForKind = "unit_not_allowed_for_kind";
    public const string SignRequired = "sign_required";
    public const string SignNotAllowedForKind = "sign_not_allowed_for_kind";
    public const string DirectionFromRequired = "direction_from_required";
    public const string DirectionFromNotAllowed = "direction_from_not_allowed";
    public const string DirectionFromUnknownSignal = "direction_from_unknown_signal";
    public const string DirectionFromNotSignedPower = "direction_from_not_signed_power";
    public const string DirectionFromPhaseMismatch = "direction_from_phase_mismatch";
    public const string RoleKindMismatch = "role_kind_mismatch";
    public const string RoleNotInTopology = "role_not_in_topology";
    public const string PhaseRequired = "phase_required";
    public const string PhaseNotAllowed = "phase_not_allowed";
    public const string PhaseNotInTopology = "phase_not_in_topology";
    public const string DuplicateRole = "duplicate_role";
    public const string RequiredSignalMissing = "required_signal_missing";
    public const string InvalidDuration = "invalid_duration";
    public const string ReserveOutOfRange = "reserve_out_of_range";
    public const string TierBandsInvalid = "tier_bands_invalid";
    public const string TierEnvelopeViolated = "tier_envelope_violated";
    public const string InvalidDeviceId = "invalid_device_id";
    public const string DuplicateDeviceId = "duplicate_device_id";
    public const string ActuatorDomainNotAllowed = "actuator_domain_not_allowed";
    public const string StateEntityDomainNotAllowed = "state_entity_domain_not_allowed";
    public const string DuplicateActuator = "duplicate_actuator";
    public const string ModulatingRangeInvalid = "modulating_range_invalid";
    public const string SensorDomainRequired = "sensor_domain_required";
    public const string RotationGroupMismatch = "rotation_group_mismatch";
    public const string UnknownCircuitGroup = "unknown_circuit_group";

    // Warnings: the document is still valid.
    public const string ReservedKeyIgnored = "reserved_key_ignored";
    public const string DirectionUnavailableCautious = "direction_unavailable_cautious";
    public const string GroupRatingAboveMain = "group_rating_above_main";

    /// <summary>Raised at run time, once a refresh interval is detected (see the signal store).</summary>
    public const string MaxAgeShortForProbe = "max_age_short_for_probe";
}

public enum ConfigurationStatus
{
    Valid,
    Invalid,
    Missing,
}

/// <summary>
/// The outcome of reading the configuration document. Anything but <see cref="ConfigurationStatus.Valid"/>
/// means no protection data is valid; there is no fallback to an earlier document.
/// </summary>
public sealed record ConfigurationResult(
    ConfigurationStatus Status,
    SwitchboardConfiguration? Configuration,
    ImmutableArray<ConfigurationIssue> Errors,
    ImmutableArray<ConfigurationIssue> Warnings,
    string Path,
    DateTimeOffset LoadedAt)
{
    public static ConfigurationResult Failed(ConfigurationStatus status, IEnumerable<ConfigurationIssue> errors, string path, DateTimeOffset loadedAt) =>
        new(status, null, [.. errors], [], path, loadedAt);
}
