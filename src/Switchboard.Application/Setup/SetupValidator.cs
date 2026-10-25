using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Setup;

/// <summary>
/// What the wizard's validation reads besides the draft. <see cref="Discovery"/> is null when Home
/// Assistant cannot be reached. <see cref="RuntimeRefresh"/> is the refresh the running signal store
/// detected for entities it already maps; <see cref="Control"/> are the control parameters of the
/// document being edited (defaults merged with its control:).
/// </summary>
public sealed record SetupContext(
    DiscoverySnapshot? Discovery,
    ICandidateReadouts Readouts,
    IReadOnlyDictionary<EntityId, TimeSpan> RuntimeRefresh,
    SetupDefaults Defaults,
    ControlParameters Control,
    PhaseEvidenceStore Evidence,
    DateTimeOffset Now)
{
    /// <summary>
    /// The sigma of each mapped signal declared in the document being saved, normalized to A, W or V;
    /// a signal without sigma uses signals.default_sigma, as the runtime parser does.
    /// </summary>
    public IReadOnlyDictionary<EntityId, double> DeclaredSigma { get; init; } = ImmutableDictionary<EntityId, double>.Empty;
}

/// <param name="State">passed, failed or skipped (never passed without a total).</param>
/// <param name="Unit">W (phase powers against the total power) or A (signed currents against the total current).</param>
public sealed record PhaseSumCheck(string State, double? Residual, double? Tolerance, int Samples, string? Unit, string? Reason);

/// <param name="State">ok, below_refresh, above_refresh (a meter row trusted far longer than it reports) or not_detected.</param>
public sealed record RefreshCheck(string Field, double? DetectedS, double? MaxAgeS, string State, double? SuggestedMaxAgeS);

/// <summary>The self-test verdict of one declared device against the draft (recorded and watched evidence re-judged).</summary>
public sealed record DeviceSelfTest(string Field, string? Device, EntityId StateEntity, PhaseJudgement Judgement);

public sealed record SetupValidation(
    ImmutableArray<SetupIssue> Issues,
    PhaseSumCheck PhaseSum,
    ImmutableArray<RefreshCheck> Refresh,
    ReserveReport? Reserve,
    ImmutableArray<DeviceSelfTest> SelfTest)
{
    public bool Valid => Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>
/// CONN-06 and the wizard's own rules, on top of the runtime parser (which judges the written
/// document separately): entities exist, are primary measurements (never a derived sensor, 07),
/// have the declared unit and device class, report plausible values for this connection, the
/// phases sum to the total, the detected refresh fits each max_age, the reserve is compared with the
/// minimum safe reserve, and the passive self-test evidence is judged against the declared phases.
/// Pure: no I/O, no clock beyond <see cref="SetupContext.Now"/>.
/// </summary>
public static class SetupValidator
{
    private const string Unsigned = "unsigned";
    private const string OnChange = "on_change";

    private enum InputKind
    {
        PhaseCurrent,
        PhasePower,
        PhaseVoltage,
        TotalCurrent,
        TotalPower,
        InverterPower,
        InverterCurrent,
        DeviceState,
        DeviceActuator,
        DrawCurrent,
        DrawPower,
    }

    public static SetupValidation Validate(SetupDraft draft, SetupContext context)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<SetupIssue>();
        var refresh = new List<RefreshCheck>();
        Required(draft, issues);

        var profile = Profile(draft.Connection, context.Control.Connection.DefaultReserve);
        var inputs = Inputs(draft).ToList();
        var registryUnchecked = new List<string>();
        foreach (var input in inputs)
        {
            if (input.Entity is null || !EntityId.TryParse(input.Entity, out var entity))
            {
                // A missing or malformed entity is reported by the required checks and the parser.
                continue;
            }

            CheckInput(input, entity.Value, draft, profile, context, issues, refresh, registryUnchecked);
        }

        if (registryUnchecked.Count > 0)
        {
            var unreachable = context.Discovery is null;
            issues.Add(SetupIssue.Warning(
                SetupIssueCodes.DerivedCheckUnavailable,
                null,
                unreachable
                    ? $"Home Assistant's entity list cannot be read right now, so these fields are not checked for derived sensors: {string.Join(", ", registryUnchecked)}. Make sure each is a primary measurement."
                    : $"Home Assistant's entity registry could not be read, so these fields are not checked for derived sensors: {string.Join(", ", registryUnchecked)}.",
                ("fields", string.Join(",", registryUnchecked)),
                ("reason", unreachable ? "home_assistant_unavailable" : "registry_unavailable")));
        }

        DeviceStateAges(draft, context, issues);
        var phaseSum = PhaseSum(draft, context, issues);
        var reserve = Reserve(draft, profile, context, issues);
        var selfTest = SelfTests(draft, profile, context, issues);
        return new SetupValidation([.. issues], phaseSum, [.. refresh], reserve, selfTest);
    }

    /// <summary>
    /// The connection as a profile when every part of it is usable; null otherwise. A phase without a
    /// reserve gets <paramref name="defaultReserve"/> (control.connection.default_reserve_w), exactly as
    /// the runtime parser does.
    /// </summary>
    public static ConnectionProfile? Profile(ConnectionDraft connection, Watts defaultReserve)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Topology? topology = connection.Topology switch
        {
            "three_phase" => Topology.ThreePhase,
            "three_phase_summed" => Topology.ThreePhaseSummed,
            "single_phase" => Topology.SinglePhase,
            _ => null,
        };
        BreakerCharacteristic? characteristic = connection.Characteristic switch
        {
            "B" => BreakerCharacteristic.B,
            "C" => BreakerCharacteristic.C,
            "unknown" => BreakerCharacteristic.Unknown,
            _ => null,
        };
        if (topology is not { } t || characteristic is not { } c || connection.BreakerRatingA is not > 0 || connection.VoltageV is not > 0)
        {
            return null;
        }

        var phases = TopologyPhases.For(t);
        var reserve = phases.ToImmutableDictionary(p => p, p => Reserve(connection.ReserveW, p) is { } watts ? new Watts(watts) : defaultReserve);
        return new ConnectionProfile(t, new Amperes(connection.BreakerRatingA.Value), c, new Volts(connection.VoltageV.Value), reserve);
    }

    /// <summary>A duration as switchboard.yaml writes it (90s), read with the parser's own rule.</summary>
    public static TimeSpan? Duration(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var issues = new ConfigIssues();
        return ConfigRead.TryDuration(ConfigTree.Plain(text), string.Empty, issues, allowUnlimited: false, out var duration) ? duration : null;
    }

    private static double? Reserve(PhaseValues? values, Phase phase) => phase switch
    {
        Phase.L1 => values?.L1,
        Phase.L2 => values?.L2,
        _ => values?.L3,
    };

    private static MeterPhaseDraft? Row(MeterPhases phases, Phase phase) => phase switch
    {
        Phase.L1 => phases.L1,
        Phase.L2 => phases.L2,
        _ => phases.L3,
    };

    private static void Required(SetupDraft draft, List<SetupIssue> issues)
    {
        void Need(bool present, string field, string what)
        {
            if (!present)
            {
                issues.Add(SetupIssue.Error(SetupIssueCodes.RequiredField, field, $"{what} is required.", ("field", field)));
            }
        }

        var connection = draft.Connection;
        Need(connection.Topology is not null, "connection.topology", "The installation's topology");
        Need(connection.BreakerRatingA is not null, "connection.breakerRatingA", "The main breaker rating");
        Need(connection.Characteristic is not null, "connection.characteristic", "The breaker characteristic");
        Need(connection.VoltageV is not null, "connection.voltageV", "The phase voltage");

        var meter = draft.Meter;
        switch (connection.Topology)
        {
            case "three_phase":
                foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
                {
                    Need(Row(meter.Phases, phase)?.Current?.Entity is not null, $"meter.phases.{phase}.current.entity", $"The current entity of {phase}");
                }

                break;
            case "single_phase":
                Need(meter.Phases.L1?.Current?.Entity is not null, "meter.phases.L1.current.entity", "The current entity of L1");
                break;
            case "three_phase_summed":
                // The summed topology limits the total current, not total power: a total current signal is needed.
                Need(meter.Total.Current?.Entity is not null, "meter.total.current.entity", "A total current entity (the summed topology limits the total current)");
                break;
        }

        var phases = SetupDocumentMapper.TopologyPhasesOf(connection.Topology);
        var currents = phases.Where(p => Row(meter.Phases, p)?.Current?.Entity is not null).ToList();
        var anyCurrent = currents.Count > 0 || meter.Total.Current?.Entity is not null;
        var anyPower = phases.Any(p => Row(meter.Phases, p)?.Power?.Entity is not null) || meter.Total.Power?.Entity is not null;
        if (anyCurrent)
        {
            Need(meter.CurrentSign is not null, "meter.currentSign", "The current's sign convention");
        }

        if (anyPower)
        {
            Need(meter.PowerSign is not null, "meter.powerSign", "The power's sign convention");
        }

        if (meter.CurrentSign == Unsigned)
        {
            Need(meter.CurrentDirection is not null, "meter.currentDirection", "The direction source of an unsigned current");
            if (meter.CurrentDirection == "phase_power")
            {
                var withoutPower = currents.Where(p => Row(meter.Phases, p)?.Power?.Entity is null).ToList();
                if (withoutPower.Count > 0 || (meter.Total.Current?.Entity is not null && meter.Total.Power?.Entity is null))
                {
                    issues.Add(SetupIssue.Error(
                        SetupIssueCodes.RequiredField,
                        "meter.currentDirection",
                        $"Direction from the phase power needs the power of every phase with a current ({string.Join(", ", withoutPower.Select(p => p.ToString()).DefaultIfEmpty("total"))}); map it, or choose cautious mode.",
                        ("field", "meter.currentDirection"),
                        ("phases", string.Join(",", withoutPower.Select(p => p.ToString()).DefaultIfEmpty("total")))));
                }
            }
        }

        if (draft.Sources.Inverter is { } inverter)
        {
            Need(inverter.Power?.Entity is not null, "sources.inverter.power.entity", "The inverter's power entity");
            Need(inverter.Sign is not null, "sources.inverter.sign", "The inverter power's sign convention");
        }
    }

    private sealed record Input(string Field, string EntityField, string? Entity, string? Unit, string? MaxAge, InputKind Kind, int? Device);

    private static IEnumerable<Input> Inputs(SetupDraft draft)
    {
        Input? Signal(SignalDraft? signal, string field, InputKind kind) =>
            signal is null ? null : new Input(field, $"{field}.entity", signal.Entity, signal.Unit, signal.MaxAge, kind, null);

        // Only the topology's phases are written (SetupDocumentMapper), so only they are checked.
        var shown = SetupDocumentMapper.TopologyPhasesOf(draft.Connection.Topology);
        var meter = draft.Meter;
        var all = new List<Input?>();
        foreach (var phase in shown)
        {
            var row = Row(meter.Phases, phase);
            all.Add(Signal(row?.Current, $"meter.phases.{phase}.current", InputKind.PhaseCurrent));
            all.Add(Signal(row?.Power, $"meter.phases.{phase}.power", InputKind.PhasePower));
            all.Add(Signal(row?.Voltage, $"meter.phases.{phase}.voltage", InputKind.PhaseVoltage));
        }

        all.Add(Signal(meter.Total.Current, "meter.total.current", InputKind.TotalCurrent));
        all.Add(Signal(meter.Total.Power, "meter.total.power", InputKind.TotalPower));
        if (draft.Sources.Inverter is { } inverter)
        {
            all.Add(Signal(inverter.Power, "sources.inverter.power", InputKind.InverterPower));
            foreach (var phase in shown)
            {
                var current = phase switch { Phase.L1 => inverter.PhaseCurrents?.L1, Phase.L2 => inverter.PhaseCurrents?.L2, _ => inverter.PhaseCurrents?.L3 };
                all.Add(Signal(current, $"sources.inverter.phaseCurrents.{phase}", InputKind.InverterCurrent));
            }
        }

        for (var i = 0; i < draft.Devices.Count; i++)
        {
            var device = draft.Devices[i];
            if (device.Existing && !device.Editable)
            {
                continue;
            }

            var path = $"devices[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (device.StateEntity is not null)
            {
                all.Add(new Input($"{path}.stateEntity", $"{path}.stateEntity", device.StateEntity, null, null, InputKind.DeviceState, i));
                all.Add(new Input($"{path}.actuator", $"{path}.actuator", device.Actuator, null, null, InputKind.DeviceActuator, i));
            }
            else
            {
                // Without a state entity the actuator reports the applied state: it is an input too.
                all.Add(new Input($"{path}.actuator", $"{path}.actuator", device.Actuator, null, null, InputKind.DeviceState, i));
            }

            if (device.StopActuator is not null)
            {
                // A stop actuator reports its own state (device_<id>_stop): an input like a state entity.
                all.Add(new Input($"{path}.stopActuator", $"{path}.stopActuator", device.StopActuator, null, null, InputKind.DeviceState, i));
            }

            if (device.Draw is { } draw)
            {
                // The unit the runtime will read the draw in (current_unit / power_unit, else A / W) must be Home Assistant's own.
                all.Add(new Input($"{path}.draw.current", $"{path}.draw.current", draw.Current, draw.CurrentUnit ?? DeviceSectionParser.DefaultUnit, draw.MaxAge, InputKind.DrawCurrent, i));
                all.Add(new Input($"{path}.draw.power", $"{path}.draw.power", draw.Power, draw.PowerUnit ?? DeviceSectionParser.DefaultPowerUnit, draw.MaxAge, InputKind.DrawPower, i));
            }
        }

        return all.OfType<Input>();
    }

    private static void CheckInput(
        Input input,
        EntityId entity,
        SetupDraft draft,
        ConnectionProfile? profile,
        SetupContext context,
        List<SetupIssue> issues,
        List<RefreshCheck> refresh,
        List<string> registryUnchecked)
    {
        var snapshot = context.Discovery;
        var discovered = snapshot?.Find(entity);
        var readout = context.Readouts.Peek(entity);
        var known = readout?.Status is ReadoutStatus.Found or ReadoutStatus.Unavailable;
        var field = input.EntityField;
        var name = entity.Value;

        if (snapshot is null)
        {
            if (!known)
            {
                issues.Add(SetupIssue.Error(
                    SetupIssueCodes.EntityCheckUnavailable,
                    field,
                    $"Home Assistant cannot be reached, so whether {name} exists cannot be checked.",
                    ("entity", name),
                    ("reason", "home_assistant_unavailable")));
                return;
            }
        }
        else if (discovered is null && !known)
        {
            if (readout?.Status == ReadoutStatus.NotFound || !snapshot.Truncated)
            {
                issues.Add(SetupIssue.Error(SetupIssueCodes.EntityNotFound, field, $"Home Assistant has no entity {name}.", ("entity", name)));
                return;
            }

            // Beyond the discovery bound: neither found nor refused.
            issues.Add(SetupIssue.Warning(
                SetupIssueCodes.EntityCheckUnavailable,
                field,
                $"{name} is not in the (truncated) discovery and no live state arrived yet, so its existence is not confirmed.",
                ("entity", name),
                ("reason", "discovery_truncated")));
        }

        // Judged by entity ID: the registry's platform index is complete even when the discovered list was cut.
        var platform = DerivedSensorRule.PlatformOf(entity, snapshot);
        switch (DerivedSensorRule.Check(entity, snapshot, context.Defaults))
        {
            case DerivedCheck.Derived when input.Kind == InputKind.DeviceActuator:
                issues.Add(SetupIssue.Warning(
                    SetupIssueCodes.ActuatorDerived,
                    field,
                    $"{name} is provided by the {platform} integration (a derived entity). It is declared as the actuator only; its state is read from the state entity.",
                    ("entity", name),
                    ("platform", platform!)));
                break;
            case DerivedCheck.Derived:
                issues.Add(SetupIssue.Error(
                    SetupIssueCodes.DerivedSensorRefused,
                    field,
                    $"{name} is computed by the {platform} integration from other sensors; a derived sensor is never a control input. Map the primary measurement instead.",
                    ("entity", name),
                    ("platform", platform!)));
                return;
            case DerivedCheck.NotInRegistry when input.Kind is not (InputKind.DeviceActuator or InputKind.DeviceState):
                // A measurement whose platform is unknown may be a template sensor (the house-load sensor on the
                // maintainer's installation is one): refused, with the fix that makes it checkable.
                issues.Add(SetupIssue.Error(
                    SetupIssueCodes.SourceNotInRegistry,
                    field,
                    $"{name} is not in Home Assistant's entity registry (defined in YAML without a unique_id), so it may be a template or statistics sensor; a derived sensor is never a control input. Give it a unique_id so Home Assistant registers its integration, or map the primary measurement.",
                    ("entity", name)));
                return;
            case DerivedCheck.NotInRegistry when input.Kind == InputKind.DeviceState:
                issues.Add(SetupIssue.Warning(
                    SetupIssueCodes.DerivedCheckUnavailable,
                    field,
                    $"The source of {name} cannot be verified: it is not in Home Assistant's entity registry (defined in YAML without a unique_id). Make sure it is a primary measurement, not a template or statistics sensor.",
                    ("entity", name),
                    ("reason", "not_in_registry")));
                break;
            case DerivedCheck.RegistryUnavailable when input.Kind != InputKind.DeviceActuator:
                // Home Assistant unreachable (a live readout still knows the entity) or its registry
                // unreadable: never silent, the field is named in the warning below.
                registryUnchecked.Add(field);
                break;
        }

        if (input.Kind is InputKind.DeviceState or InputKind.DeviceActuator)
        {
            return;
        }

        var kind = KindOf(input.Kind);
        var haUnit = known ? readout!.Unit : discovered?.Unit;
        var allowed = SignalUnits.AllowedFor(kind);
        if (discovered?.DeviceClass is { } deviceClass && ExpectedDeviceClass(kind) is { } expectedClass && deviceClass != expectedClass
            && deviceClass is "current" or "power" or "voltage" or "energy" or "apparent_power" or "reactive_power")
        {
            issues.Add(SetupIssue.Error(
                SetupIssueCodes.DeviceClassMismatch,
                field,
                $"{name} has device class {deviceClass}; this field needs a {expectedClass} sensor.",
                ("entity", name),
                ("deviceClass", deviceClass),
                ("expected", expectedClass)));
            return;
        }

        if (haUnit is not null && (!allowed.Contains(haUnit) || (input.Unit is not null && input.Unit != haUnit)))
        {
            // A draw has no unit field of its own: its unit comes with the picked entity, so the refusal names the entity.
            var draw = input.Kind is InputKind.DrawCurrent or InputKind.DrawPower;
            issues.Add(SetupIssue.Error(
                SetupIssueCodes.UnitMismatch,
                input.Unit is null || !allowed.Contains(haUnit) || draw ? field : $"{input.Field}.unit",
                !allowed.Contains(haUnit) ? $"{name} reports in {haUnit}, which is not a unit of this field ({string.Join(", ", allowed)})."
                    : draw ? $"{name} reports in {haUnit}, but the draw would be read in {input.Unit}; pick the entity again so its unit is taken over."
                    : $"{name} reports in {haUnit}, not {input.Unit}; set the unit to {haUnit}.",
                ("entity", name),
                ("unit", haUnit),
                ("declared", input.Unit ?? string.Empty)));
            return;
        }

        CheckValue(input, name, known ? readout!.State : discovered?.State, input.Unit ?? haUnit, draft, profile, context.Defaults.Validation, issues);
        CheckRefresh(input, entity, context, issues, refresh);
    }

    private static SignalKind KindOf(InputKind kind) => kind switch
    {
        InputKind.PhaseCurrent or InputKind.TotalCurrent or InputKind.InverterCurrent or InputKind.DrawCurrent => SignalKind.Current,
        InputKind.PhaseVoltage => SignalKind.Voltage,
        _ => SignalKind.Power,
    };

    private static string? ExpectedDeviceClass(SignalKind kind) => kind switch
    {
        SignalKind.Current => "current",
        SignalKind.Power => "power",
        SignalKind.Voltage => "voltage",
        _ => null,
    };

    private static void CheckValue(Input input, string name, string? state, string? unit, SetupDraft draft, ConnectionProfile? profile, SetupValidationRules rules, List<SetupIssue> issues)
    {
        if (state is null)
        {
            return;
        }

        if (state is SignalNormalizer.UnavailableState or SignalNormalizer.UnknownState)
        {
            var asleep = input.Kind == InputKind.InverterPower && draft.Sources.Inverter?.UnavailableAsZero == true;
            if (asleep)
            {
                return;
            }

            var protection = input.Kind is InputKind.PhaseCurrent or InputKind.TotalCurrent or InputKind.PhasePower or InputKind.TotalPower;
            var message = $"{name} reports {state} right now; a {(protection ? "meter input" : "value")} must report a number.";
            issues.Add(protection
                ? SetupIssue.Error(SetupIssueCodes.EntityUnavailable, input.EntityField, message, ("entity", name), ("state", state))
                : SetupIssue.Warning(SetupIssueCodes.EntityUnavailable, input.EntityField, message, ("entity", name), ("state", state)));
            return;
        }

        if (!double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) || !double.IsFinite(raw))
        {
            issues.Add(SetupIssue.Error(SetupIssueCodes.ValueImplausible, input.EntityField, $"{name} reports '{state}', which is not a number.", ("entity", name), ("state", state)));
            return;
        }

        if (profile is null || unit is null || !SignalUnits.AllowedFor(KindOf(input.Kind)).Contains(unit))
        {
            return;
        }

        var value = Math.Abs(raw * SignalUnits.Factor(unit));
        var rating = profile.BreakerRating.Value;
        var volts = profile.Voltage.Value;
        var phases = profile.Phases.Length;
        var (limit, low, kindName) = input.Kind switch
        {
            InputKind.PhaseCurrent or InputKind.InverterCurrent or InputKind.DrawCurrent => (rules.PlausibleCurrentMaxMultiple * rating, (double?)null, "current"),
            InputKind.TotalCurrent => (rules.PlausibleCurrentMaxMultiple * rating * phases, null, "current"),
            InputKind.PhasePower or InputKind.DrawPower => (rules.PlausiblePowerMaxMultiple * rating * volts, null, "power"),
            InputKind.TotalPower or InputKind.InverterPower => (rules.PlausiblePowerMaxMultiple * rating * volts * phases, null, "power"),
            _ => (volts * (1 + rules.PlausibleVoltageBand), volts * (1 - rules.PlausibleVoltageBand), "voltage"),
        };
        if (value > limit || (low is { } floor && value < floor))
        {
            issues.Add(SetupIssue.Error(
                SetupIssueCodes.ValueImplausible,
                input.EntityField,
                string.Create(CultureInfo.InvariantCulture, $"{name} reports {raw} {unit}, which is not a plausible {kindName} for a {rating} A, {volts} V connection; check the entity and its unit."),
                ("entity", name),
                ("state", state),
                ("unit", unit)));
        }
    }

    private static void CheckRefresh(Input input, EntityId entity, SetupContext context, List<SetupIssue> issues, List<RefreshCheck> checks)
    {
        if (input.Kind == InputKind.PhaseVoltage)
        {
            return;
        }

        var meterRow = input.Kind is InputKind.PhaseCurrent or InputKind.PhasePower or InputKind.TotalCurrent or InputKind.TotalPower;
        var detected = context.RuntimeRefresh.TryGetValue(entity, out var runtime) ? runtime : context.Readouts.Peek(entity)?.Refresh;
        var maxAge = Duration(input.MaxAge);
        var lag = context.Control.Signals.CurrentLagPolls;
        var suggested = detected * context.Defaults.Validation.SuggestedMaxAgeIntervals;
        if (detected is null)
        {
            if (meterRow)
            {
                checks.Add(new RefreshCheck(input.Field, null, maxAge?.TotalSeconds, "not_detected", null));
                issues.Add(SetupIssue.Warning(
                    SetupIssueCodes.RefreshNotDetected,
                    $"{input.Field}.maxAge",
                    $"The refresh of {entity} is not detected yet (it needs {context.Control.Signals.Refresh.MinIntervals} pushed reports); the minimum safe reserve uses max_age instead.",
                    ("entity", entity.Value)));
            }

            return;
        }

        var needed = detected.Value * (1 + lag);
        var ceiling = detected.Value * context.Defaults.Validation.MaxAgeAboveRefreshIntervals;
        var state = maxAge is { } age && age < needed ? "below_refresh"
            : meterRow && maxAge is { } trusted && trusted > ceiling ? "above_refresh"
            : "ok";
        checks.Add(new RefreshCheck(input.Field, detected.Value.TotalSeconds, maxAge?.TotalSeconds, state, suggested?.TotalSeconds));
        if (state == "above_refresh")
        {
            // CONN-06 both ways: a meter row's max_age is how long a frozen poll stays trusted.
            issues.Add(SetupIssue.Warning(
                SetupIssueCodes.MaxAgeAboveRefresh,
                $"{input.Field}.maxAge",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entity} reports every {detected.Value.TotalSeconds:0.#} s, but max_age {maxAge!.Value.TotalSeconds:0.#} s would keep trusting a frozen value that long; suggested {suggested!.Value.TotalSeconds:0} s."),
                ("entity", entity.Value),
                ("refreshS", detected.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)),
                ("maxAgeS", maxAge.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)),
                ("suggestedS", suggested.Value.TotalSeconds.ToString("0", CultureInfo.InvariantCulture))));
        }

        if (state == "below_refresh")
        {
            issues.Add(SetupIssue.Error(
                SetupIssueCodes.MaxAgeBelowRefresh,
                $"{input.Field}.maxAge",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entity} reports every {detected.Value.TotalSeconds:0.#} s, so max_age {maxAge!.Value.TotalSeconds:0.#} s would turn it stale between reports; use at least {needed.TotalSeconds:0.#} s (suggested {suggested!.Value.TotalSeconds:0} s)."),
                ("entity", entity.Value),
                ("refreshS", detected.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)),
                ("minimumS", needed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)),
                ("suggestedS", suggested!.Value.TotalSeconds.ToString("0", CultureInfo.InvariantCulture))));
        }
    }

    /// <summary>An age-judged state older than state_max_age goes stale and holds its device off: a relay that reports only on change needs on_change.</summary>
    private static void DeviceStateAges(SetupDraft draft, SetupContext context, List<SetupIssue> issues)
    {
        for (var i = 0; i < draft.Devices.Count; i++)
        {
            var device = draft.Devices[i];
            if ((device.Existing && !device.Editable) || device.StateReports == OnChange
                || !EntityId.TryParse(device.StateEntity ?? device.Actuator, out var entity)
                || context.Readouts.Peek(entity.Value) is not { Status: ReadoutStatus.Found, Age: { } age }
                || age < context.Control.Devices.StateMaxAge)
            {
                continue;
            }

            issues.Add(SetupIssue.Warning(
                SetupIssueCodes.MaxAgeBelowRefresh,
                $"devices[{i.ToString(CultureInfo.InvariantCulture)}].stateReports",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entity} last reported {age.TotalMinutes:0} min ago, longer than the state max age ({context.Control.Devices.StateMaxAge.TotalMinutes:0} min): it seems to report only on change. Choose 'reports on change', or the device will be held off as stale."),
                ("entity", entity.Value.Value),
                ("suggest", OnChange)));
        }
    }

    /// <summary>
    /// CONN-06: the phases sum to the total. Judged on the median residual of aligned samples (each
    /// total report against the phase values in force within max_skew of it), so one lagging poll
    /// cannot fail it; skipped, never passed, without a total or with unsigned currents only.
    /// </summary>
    private static PhaseSumCheck PhaseSum(SetupDraft draft, SetupContext context, List<SetupIssue> issues)
    {
        var meter = draft.Meter;
        var profile = Profile(draft.Connection, context.Control.Connection.DefaultReserve);
        var phases = profile?.Phases ?? [Phase.L1, Phase.L2, Phase.L3];
        PhaseSumCheck Skip(string reason, string message)
        {
            issues.Add(SetupIssue.Warning(SetupIssueCodes.PhaseSumSkipped, "meter.total", message, ("reason", reason)));
            return new PhaseSumCheck("skipped", null, null, 0, null, reason);
        }

        (SignalDraft Total, IReadOnlyList<SignalDraft> Phases, string Sign, string Unit, SignalKind Kind)? terms = null;
        var powers = phases.Select(p => Row(meter.Phases, p)?.Power).ToList();
        var currents = phases.Select(p => Row(meter.Phases, p)?.Current).ToList();
        if (meter.Total.Power?.Entity is not null && powers.All(p => p?.Entity is not null) && meter.PowerSign is not null and not Unsigned)
        {
            terms = (meter.Total.Power, powers!, meter.PowerSign, "W", SignalKind.Power);
        }
        else if (meter.Total.Current?.Entity is not null && currents.All(c => c?.Entity is not null) && meter.CurrentSign is not null and not Unsigned)
        {
            terms = (meter.Total.Current, currents!, meter.CurrentSign, "A", SignalKind.Current);
        }

        if (terms is not { } t)
        {
            return meter.Total.Power?.Entity is null && meter.Total.Current?.Entity is null
                ? Skip("no_total", "No total is mapped, so the phases cannot be checked against it (skipped, not passed).")
                : Skip("not_comparable", "The total and the phases cannot be compared (a phase is missing, or the currents are unsigned): skipped, not passed.");
        }

        MeterTerm Term(SignalDraft signal) => new(EntityId.Parse(signal.Entity!), signal.Unit ?? t.Unit, t.Sign);
        List<ReportPoint> Series(SignalDraft signal)
        {
            if (!EntityId.TryParse(signal.Entity, out var entity))
            {
                return [];
            }

            var term = Term(signal);
            return [.. context.Readouts.History(entity.Value)
                .Select(u => (u.Reported, Value: MeterValues.Read(u.State, term)))
                .Where(p => p.Value is not null)
                .Select(p => new ReportPoint(p.Reported, p.Value!.Value))];
        }

        var total = Series(t.Total);
        var phaseSeries = t.Phases.Select(Series).ToList();
        var skew = context.Control.Signals.Balance.MaxSkew;
        var residuals = new List<double>();
        var magnitudes = new List<double>();
        foreach (var point in total)
        {
            var values = phaseSeries.Select(s => s.LastOrDefault(p => p.At <= point.At + skew)).ToList();
            if (values.Any(v => v == default))
            {
                continue;
            }

            residuals.Add(point.Value - values.Sum(v => v.Value));
            magnitudes.Add(Math.Max(Math.Abs(point.Value), values.Sum(v => Math.Abs(v.Value))));
        }

        if (residuals.Count < context.Defaults.Validation.PhaseSumMinSamples)
        {
            return Skip(
                "not_enough_samples",
                $"Only {residuals.Count} aligned samples of the total and every phase so far ({context.Defaults.Validation.PhaseSumMinSamples} needed); keep the page open a little longer.");
        }

        var residual = Median(residuals);
        var sigma = context.Control.Signals.DefaultSigma.GetValueOrDefault(t.Kind);
        var tolerance = (context.Defaults.Validation.PhaseSumToleranceFraction * Median(magnitudes))
            + (context.Control.ReserveDerivation.SigmaMultiplier * sigma * (1 + t.Phases.Count));
        if (Math.Abs(residual) > tolerance)
        {
            issues.Add(SetupIssue.Error(
                SetupIssueCodes.PhaseSumMismatch,
                "meter.total",
                string.Create(CultureInfo.InvariantCulture, $"The phases differ from the total by {residual:0.#} {t.Unit} (tolerance {tolerance:0.#} {t.Unit}): a phase entity, a unit or the sign convention is wrong."),
                ("residual", residual.ToString("0.#", CultureInfo.InvariantCulture)),
                ("tolerance", tolerance.ToString("0.#", CultureInfo.InvariantCulture)),
                ("unit", t.Unit)));
            return new PhaseSumCheck("failed", residual, tolerance, residuals.Count, t.Unit, null);
        }

        return new PhaseSumCheck("passed", residual, tolerance, residuals.Count, t.Unit, null);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static ReserveReport? Reserve(SetupDraft draft, ConnectionProfile? profile, SetupContext context, List<SetupIssue> issues)
    {
        if (profile is null)
        {
            return null;
        }

        var refresh = new Dictionary<Phase, PhaseRefresh>();
        var defaultSigma = context.Control.Signals.DefaultSigma.GetValueOrDefault(SignalKind.Current);
        foreach (var phase in profile.Phases)
        {
            var signal = profile.Topology == Topology.ThreePhaseSummed ? draft.Meter.Total.Current : Row(draft.Meter.Phases, phase)?.Current;
            if (signal is null || Duration(signal.MaxAge) is not { } maxAge)
            {
                continue;
            }

            TimeSpan? detected = null;
            var sigma = defaultSigma;
            if (EntityId.TryParse(signal.Entity, out var entity))
            {
                detected = context.RuntimeRefresh.TryGetValue(entity.Value, out var runtime) ? runtime : context.Readouts.Peek(entity.Value)?.Refresh;

                // The signal's own sigma when the document declares one, as SignalStore reserves with it.
                sigma = context.DeclaredSigma.TryGetValue(entity.Value, out var declared) ? declared : defaultSigma;
            }

            refresh[phase] = new PhaseRefresh(detected, maxAge, sigma);
        }

        var report = ReserveExplanation.Compute(profile, context.Control, refresh);
        foreach (var phase in report?.PerPhase.Where(p => p.BelowMinimum) ?? [])
        {
            issues.Add(SetupIssue.Warning(
                SetupIssueCodes.ReserveBelowMinimum,
                $"connection.reserveW.{phase.Phase}",
                string.Create(CultureInfo.InvariantCulture, $"The reserve on {phase.Phase} ({phase.ReserveW:0} W) is below the minimum safe reserve ({phase.MinimumSafeW:0} W) for a meter refreshing every {phase.RefreshS:0.#} s. It is shown, never enforced."),
                ("phase", phase.Phase.ToString()),
                ("reserveW", phase.ReserveW.ToString("0", CultureInfo.InvariantCulture)),
                ("minimumW", phase.MinimumSafeW.ToString("0", CultureInfo.InvariantCulture))));
        }

        return report;
    }

    private static ImmutableArray<DeviceSelfTest> SelfTests(SetupDraft draft, ConnectionProfile? profile, SetupContext context, List<SetupIssue> issues)
    {
        var results = ImmutableArray.CreateBuilder<DeviceSelfTest>();
        var phases = profile?.Phases ?? [Phase.L1, Phase.L2, Phase.L3];
        var powerTerms = MeterTerms(draft, phases, power: true);
        var currentTerms = MeterTerms(draft, phases, power: false);
        for (var i = 0; i < draft.Devices.Count; i++)
        {
            var device = draft.Devices[i];
            if ((device.Existing && !device.Editable) || device.Type != "onoff"
                || device.Phase is not ("L1" or "L2" or "L3")
                || !EntityId.TryParse(device.StateEntity ?? device.Actuator, out var state))
            {
                continue;
            }

            var field = $"devices[{i.ToString(CultureInfo.InvariantCulture)}].phase";
            var declared = Enum.Parse<Phase>(device.Phase);
            var judgement = PhaseEvidence.Judge(context.Evidence.For(state.Value), declared, device.RatedPowerW, device.RatedCurrentA, powerTerms, currentTerms, context.Defaults.SelfTest.Classifier);
            results.Add(new DeviceSelfTest(field, device.Id, state.Value, judgement));
            var summary = judgement.Summary;
            var label = device.Name ?? device.Id ?? state.Value.Value;
            if (summary.Verdict == PhaseStepVerdict.Mismatch)
            {
                // Only ever on agreeing switchings both ways (min_concordant, a switch on and a switch
                // off on the same phase): a single coincident step never refuses a save.
                issues.Add(SetupIssue.Error(
                    SetupIssueCodes.PhaseSelfTestMismatch,
                    field,
                    $"When {state} was switched on and off ({summary.Mismatched} times), the meter's {summary.Observed} moved with it, not {declared}: either the device is on {summary.Observed}, or the meter's phase entities are swapped. Fix the phase (or the meter mapping) before saving.",
                    ("device", label),
                    ("declared", declared.ToString()),
                    ("observed", summary.Observed!.Value.ToString()),
                    ("count", summary.Mismatched.ToString(CultureInfo.InvariantCulture))));
            }
            else if (summary.Verdict == PhaseStepVerdict.Inconclusive)
            {
                var reason = summary.Reason ?? InconclusiveReason.TimedOut;
                issues.Add(reason == InconclusiveReason.Unconfirmed && summary.Observed is { } pointed
                    ? SetupIssue.Warning(
                        SetupIssueCodes.PhaseSelfTestMissing,
                        field,
                        $"A switching of {label} moved the meter's {pointed} (declared {declared}), but one switching can be a coincidence: switch it on and off while the wizard watches to confirm.",
                        ("device", label),
                        ("reason", Snake(reason)),
                        ("observed", pointed.ToString()),
                        ("declared", declared.ToString()))
                    : SetupIssue.Warning(
                        SetupIssueCodes.PhaseSelfTestMissing,
                        field,
                        $"The phase of {label} is not confirmed yet: run the phase self-test (switch the device on and off yourself while the wizard watches).",
                        ("device", label),
                        ("reason", Snake(reason))));
            }
        }

        return results.ToImmutable();
    }

    /// <summary>The draft's meter terms for every phase of the topology (null when one is missing).</summary>
    public static ImmutableDictionary<Phase, MeterTerm>? MeterTerms(SetupDraft draft, IReadOnlyList<Phase> phases, bool power)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(phases);
        var builder = ImmutableDictionary.CreateBuilder<Phase, MeterTerm>();
        foreach (var phase in phases)
        {
            var row = Row(draft.Meter.Phases, phase);
            var signal = power ? row?.Power : row?.Current;
            var sign = power ? draft.Meter.PowerSign : draft.Meter.CurrentSign;
            if (signal?.Entity is null || !EntityId.TryParse(signal.Entity, out var entity) || signal.Unit is null || sign is null)
            {
                return null;
            }

            builder[phase] = new MeterTerm(entity.Value, signal.Unit, sign);
        }

        return builder.ToImmutable();
    }

    private static string Snake(InconclusiveReason reason) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(reason.ToString());
}
