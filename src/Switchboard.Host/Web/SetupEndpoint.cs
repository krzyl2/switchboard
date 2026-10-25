using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Options;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Host.Web;

/// <summary>GET api/setup/discovery: what Home Assistant offers, with each entity's platform and whether it is derived (07).</summary>
public sealed record DiscoveryResponse(DateTimeOffset TakenAt, bool RegistryAvailable, bool Truncated, IReadOnlyList<DiscoveredEntityDto> Entities, IReadOnlyList<DiscoveredDeviceDto> Devices);

/// <param name="InRegistry">False for an entity defined in YAML without a unique_id: its source cannot be verified.</param>
/// <param name="Derived">Set when the registry platform computes the sensor from other sensors; such an entity is never a control input.</param>
/// <param name="Options">What a select offers (its options attribute); null when the entity reports none.</param>
/// <param name="Own">One of Switchboard's own MQTT entities: never offered as an actuator.</param>
public sealed record DiscoveredEntityDto(
    string EntityId,
    string Domain,
    string? Name,
    string? DeviceClass,
    string? StateClass,
    string? Unit,
    string? Platform,
    string? DeviceId,
    bool Disabled,
    bool Hidden,
    bool InRegistry,
    string? State,
    DateTimeOffset? LastReported,
    DerivedDto? Derived,
    IReadOnlyList<string>? Options,
    bool Own);

public sealed record DerivedDto(string Platform);

public sealed record DiscoveredDeviceDto(string Id, string? Name, string? Manufacturer, string? Model);

/// <summary>GET api/setup/readouts: live value and age of each requested entity (UI-04), ages at <see cref="At"/> on the signal clock.</summary>
public sealed record ReadoutsResponse(DateTimeOffset At, IReadOnlyList<ReadoutDto> Items);

/// <param name="Status">pending, found, not_found or unavailable.</param>
public sealed record ReadoutDto(string EntityId, string Status, string? State, string? Unit, DateTimeOffset? LastReported, double? AgeS, RefreshDto Refresh);

/// <param name="IntervalS">The detected refresh (SIG-07); null until <paramref name="Needed"/> intervals were seen.</param>
public sealed record RefreshDto(double? IntervalS, int Samples, int Needed);

/// <summary>GET api/setup/draft.</summary>
/// <param name="FileState">missing, valid or invalid.</param>
public sealed record DraftResponse(
    string? Revision,
    string FileState,
    bool HasComments,
    bool Unreadable,
    SetupDraft Draft,
    IReadOnlyList<PreservedDto> Preserved,
    IReadOnlyList<SetupIssueDto> FileIssues);

public sealed record PreservedDto(string Path, string Summary);

/// <param name="Severity">error or warning.</param>
/// <param name="Field">The draft field to fix; null for a part of the file the wizard does not edit (see documentPath).</param>
public sealed record SetupIssueDto(string Code, string Severity, string? Field, string? DocumentPath, string Message, IReadOnlyDictionary<string, string> Args);

/// <summary>POST api/setup/validate and POST api/setup/save.</summary>
public sealed record DraftRequest
{
    public SetupDraft? Draft { get; init; }

    public string? BaseRevision { get; init; }
}

public sealed record ValidationResponse(bool Valid, IReadOnlyList<SetupIssueDto> Issues, ChecksDto Checks, SetupReserveDto? Reserve, IReadOnlyList<SelfTestVerdictDto> SelfTest);

public sealed record ChecksDto(PhaseSumDto PhaseSum, IReadOnlyList<RefreshCheckDto> Refresh);

/// <param name="State">passed, failed or skipped.</param>
public sealed record PhaseSumDto(string State, double? Residual, double? Tolerance, int Samples, string? Unit, string? Reason);

/// <param name="State">ok, below_refresh, above_refresh or not_detected.</param>
public sealed record RefreshCheckDto(string Field, double? DetectedS, double? MaxAgeS, string State, double? SuggestedMaxAgeS);

public sealed record SetupReserveDto(IReadOnlyList<SetupReservePhaseDto> PerPhase, ReserveExplanationDto Explanation);

public sealed record SetupReservePhaseDto(string Phase, double ReserveW, double MinimumSafeW, double RefreshS, bool RefreshDetected, bool BelowMinimum);

/// <param name="Reaction">The tier reaction for the kettle on a fully allocated phase.</param>
/// <param name="AllowedS">How long the tier table tolerates it; null for unlimited.</param>
public sealed record ReserveExplanationDto(
    double ReserveW,
    double PhaseW,
    double ContinuousA,
    double AllocatableW,
    double ReferenceStepW,
    double WorstCaseA,
    double Multiple,
    string Reaction,
    double? AllowedS);

/// <param name="Verdict">confirmed, mismatch or inconclusive (never shown as passed).</param>
public sealed record SelfTestVerdictDto(string Field, string? Device, string StateEntity, string Verdict, string? ObservedPhase, string? Reason, int Confirmed, int Mismatched, int Inconclusive);

public sealed record SaveResponse(string Revision, string? Backup, bool Reloaded, string? LoadedStatus);

public sealed record SetupRefusalDto(string Code, IReadOnlyList<SetupIssueDto> Issues, string? CurrentRevision);

/// <summary>GET api/setup/templates (UI-02).</summary>
/// <param name="PrefillVoltageV">The starting value of the connection's voltage, confirmed by the user.</param>
public sealed record TemplatesResponse(IReadOnlyList<TemplateDto> Templates, double PrefillVoltageV);

/// <param name="Prefill">Draft fields the template sets (written to the file).</param>
/// <param name="Inherited">What the device inherits from the control defaults (shown, not written).</param>
/// <param name="StopActuatorDomains">The domains a stop actuator may have (switch or select); empty for a type without one.</param>
public sealed record TemplateDto(string Id, string Type, IReadOnlyDictionary<string, object> Prefill, InheritedDto Inherited, IReadOnlyList<string> ActuatorDomains, IReadOnlyList<string> StateDomains, bool DrawRecommended, IReadOnlyList<string> StopActuatorDomains);

public sealed record InheritedDto(string ReactionTime, string MinOn, string MinOff);

/// <summary>POST api/setup/self-test: watch a declared device of the draft, or a test load.</summary>
public sealed record SelfTestStartRequest
{
    public SetupDraft? Draft { get; init; }

    public DeviceRef? Device { get; init; }

    public TestLoad? TestLoad { get; init; }
}

public sealed record DeviceRef
{
    public int Index { get; init; }
}

/// <summary>An on/off load that is not declared as a device: its entity, the phase from the switchboard's label, its rated power.</summary>
public sealed record TestLoad
{
    public string? StateEntity { get; init; }

    public string? Phase { get; init; }

    public double? RatedPowerW { get; init; }
}

public sealed record SelfTestStartedResponse(string Id, string Status, DateTimeOffset ExpiresAt);

/// <param name="Status">watching, finished, expired or cancelled.</param>
public sealed record SelfTestResponse(
    string Id,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    SelfTestExpectedDto Expected,
    IReadOnlyList<ObservationDto> Observations,
    SelfTestSummaryDto Verdict);

/// <param name="Unit">W, or A when the meter maps currents only.</param>
public sealed record SelfTestExpectedDto(string Phase, double Rating, string Unit, string StateEntity);

/// <param name="Transition">on or off.</param>
/// <param name="Deltas">The step of each phase that reported, import positive.</param>
/// <param name="Source">watch (the user switched it while the wizard watched) or recorded (the add-on saw someone switch it).</param>
public sealed record ObservationDto(
    DateTimeOffset At,
    string Transition,
    IReadOnlyDictionary<string, double> Deltas,
    string Unit,
    string Verdict,
    string? ObservedPhase,
    string? Reason,
    double? RatingRatio,
    string Source,
    bool HouseLoad,
    string? Note);

public sealed record SelfTestSummaryDto(string Verdict, string? ObservedPhase, string? Reason, int Confirmed, int Mismatched, int Inconclusive);

/// <summary>GET api/setup/evidence: the running add-on's recorded observations per declared device, judged against the running configuration.</summary>
public sealed record EvidenceResponse(IReadOnlyList<DeviceEvidenceDto> Devices);

public sealed record DeviceEvidenceDto(string Id, string StateEntity, string Phase, IReadOnlyList<ObservationDto> Observations, SelfTestSummaryDto Verdict);

/// <summary>
/// The setup wizard's API (Stage 5). Ingress only; every POST requires Content-Type
/// application/json (a cross-site form cannot send it without a CORS preflight, which the add-on
/// never answers) and a DELETE always needs a preflight. Nothing here can command a Home Assistant
/// entity: discovery and readouts go through the read-only discovery link, and the self-test only
/// watches while the user switches the device.
/// </summary>
/// <param name="Code">One of the closed refusal codes (control_not_active, headroom_insufficient, lockout_min_off, ...).</param>
/// <param name="Phase">The phase concerned (headroom_insufficient).</param>
/// <param name="RemainingS">Seconds until it ends (lockout_min_off).</param>
public sealed record ActiveTestRefusalDto(string Code, string? Phase, double? RemainingS);

/// <summary>GET api/setup/active-test/preconditions: whether the active phase self-test can start now, and how long it would take.</summary>
public sealed record ActiveTestPreconditionsResponse(bool Ok, IReadOnlyList<ActiveTestRefusalDto> Refusals, double? ExpectedDurationS);

/// <summary>Body of POST api/setup/active-test: a declared device of the configuration in force, and its relay stage (default 1).</summary>
public sealed record ActiveTestStartRequest
{
    public string? Device { get; init; }

    public int? Stage { get; init; }
}

public sealed record ActiveTestStartedResponse(string Id, double ExpectedDurationS);

/// <summary>Why the active self-test did not start (409).</summary>
public sealed record ActiveTestRefusedResponse(string Code, IReadOnlyList<ActiveTestRefusalDto> Refusals);

/// <summary>GET api/setup/active-test/{id}.</summary>
/// <param name="State">running, finished or aborted.</param>
/// <param name="Phase">pending_on, on, pending_off, judging or done.</param>
/// <param name="Verdict">The classifier's verdict over the test's own on and off (null until judged).</param>
/// <param name="AbortReason">guard_acted, safe_state, kill_switch, increases_stopped, not_confirmed, cancelled, control_left_active or configuration_changed.</param>
public sealed record ActiveTestResponse(
    string Id,
    string Device,
    int Stage,
    string StateEntity,
    string ExpectedPhase,
    string State,
    string Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? OnAt,
    DateTimeOffset? OffAt,
    double ExpectedDurationS,
    SelfTestSummaryDto? Verdict,
    IReadOnlyList<ObservationDto> Observations,
    string? AbortReason);

internal static class SetupEndpoint
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = PhaseAwareCamelCase.Instance,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };

    /// <summary>The answers: camelCase like every API, except that a phase stays L1, L2, L3 (the draft's own keys).</summary>
    internal static readonly JsonSerializerOptions Answers = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = PhaseAwareCamelCase.Instance };

    public static IEndpointRouteBuilder MapSetupApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/setup/discovery", async (
            HttpRequest request,
            [FromServices] EntityDiscoveryCache cache,
            [FromServices] SetupDefaults defaults,
            CancellationToken cancellationToken) =>
        {
            var refresh = request.Query.TryGetValue("refresh", out var flag) && flag == "true";
            try
            {
                var snapshot = await cache.GetAsync(refresh, cancellationToken).ConfigureAwait(false);
                return Json(Discovery(snapshot, defaults));
            }
            catch (DiscoveryUnavailableException ex)
            {
                return Json(new { code = ex.Code, message = ex.Message }, StatusCodes.Status503ServiceUnavailable);
            }
        });

        endpoints.MapGet("/api/setup/readouts", (
            HttpRequest request,
            [FromServices] ICandidateReadouts readouts,
            [FromServices] SignalClock clock,
            [FromServices] IOptions<SetupOptions> options) =>
        {
            var texts = request.Query["entity"];
            var entities = new List<EntityId>();
            foreach (var text in texts)
            {
                if (!EntityId.TryParse(text, out var entity))
                {
                    return Results.Problem($"'{text}' is not a Home Assistant entity ID.", statusCode: StatusCodes.Status400BadRequest);
                }

                entities.Add(entity.Value);
            }

            if (entities.Count == 0 || entities.Distinct().Count() > options.Value.MaxReadoutEntities)
            {
                return Results.Problem($"Ask for 1 to {options.Value.MaxReadoutEntities} entities with entity=<entity_id>.", statusCode: StatusCodes.Status400BadRequest);
            }

            var items = readouts.Lease(entities);
            return Json(new ReadoutsResponse(clock.Time.GetUtcNow(), [.. items.Select(Readout)]));
        });

        endpoints.MapGet("/api/setup/draft", ([FromServices] SetupService setup) =>
        {
            var view = setup.LoadDraft();
            return Json(new DraftResponse(
                view.Revision,
                view.FileState,
                view.HasComments,
                view.Unreadable,
                view.Draft,
                [.. view.Preserved.Select(p => new PreservedDto(p.Path, p.Summary))],
                [.. view.FileIssues.Select(Issue)]));
        });

        endpoints.MapPost("/api/setup/validate", async (HttpRequest request, [FromServices] SetupService setup, CancellationToken cancellationToken) =>
        {
            if (await ReadAsync<DraftRequest>(request, cancellationToken).ConfigureAwait(false) is not { Draft: { } draft })
            {
                return JsonRequired("{\"draft\": {...}, \"baseRevision\": \"...\"}");
            }

            var outcome = await setup.ValidateAsync(draft, cancellationToken).ConfigureAwait(false);
            return Json(Validation(outcome));
        });

        endpoints.MapPost("/api/setup/save", async (HttpRequest request, [FromServices] SetupService setup, CancellationToken cancellationToken) =>
        {
            if (await ReadAsync<DraftRequest>(request, cancellationToken).ConfigureAwait(false) is not { Draft: { } draft } body)
            {
                return JsonRequired("{\"draft\": {...}, \"baseRevision\": \"...\"}");
            }

            return await setup.SaveAsync(draft, body.BaseRevision, cancellationToken).ConfigureAwait(false) switch
            {
                SaveResult.Saved saved => Json(new SaveResponse(saved.Revision, saved.Backup, saved.Reloaded, saved.LoadedStatus is { } status ? ApiText.Snake(status) : null)),
                SaveResult.Conflict conflict => Json(
                    new SetupRefusalDto(SetupIssueCodes.RevisionConflict, [Issue(SetupIssue.Error(SetupIssueCodes.RevisionConflict, null, "The configuration file changed since the wizard loaded it; reload the wizard (your changes are not saved)."))], conflict.CurrentRevision),
                    StatusCodes.Status409Conflict),
                SaveResult.Refused refused => Json(new SetupRefusalDto("invalid", [.. refused.Issues.Select(Issue)], null), StatusCodes.Status422UnprocessableEntity),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        endpoints.MapGet("/api/setup/templates", ([FromServices] SetupService setup) => Json(Templates(setup.Defaults, setup.CurrentControl())));

        endpoints.MapPost("/api/setup/self-test", async (
            HttpRequest request,
            [FromServices] PhaseSelfTestService selfTest,
            [FromServices] SetupDefaults defaults,
            [FromServices] SetupService setup,
            CancellationToken cancellationToken) =>
        {
            if (await ReadAsync<SelfTestStartRequest>(request, cancellationToken).ConfigureAwait(false) is not { Draft: { } draft } body
                || (body.Device is null) == (body.TestLoad is null))
            {
                return JsonRequired("{\"draft\": {...}, \"device\": {\"index\": 0}} or {\"draft\": {...}, \"testLoad\": {\"stateEntity\": \"...\", \"phase\": \"L1\", \"ratedPowerW\": 2000}}");
            }

            var (watch, issues) = SelfTestRequest(draft, body, setup.CurrentControl(), defaults.SelfTest.Classifier);
            if (watch is null)
            {
                return Json(new SetupRefusalDto("invalid", [.. issues.Select(Issue)], null), StatusCodes.Status422UnprocessableEntity);
            }

            try
            {
                var started = selfTest.Start(watch);
                return Json(new SelfTestStartedResponse(started.Id.ToString("N"), Snake(started.Status), started.ExpiresAt), StatusCodes.Status202Accepted);
            }
            catch (SelfTestBusyException busy)
            {
                return Json(new { code = "self_test_busy", id = busy.Running.ToString("N") }, StatusCodes.Status409Conflict);
            }
        });

        endpoints.MapGet("/api/setup/self-test/{id}", (string id, [FromServices] PhaseSelfTestService selfTest) =>
            Guid.TryParseExact(id, "N", out var guid) && selfTest.Get(guid) is { } view
                ? Json(SelfTest(view))
                : Results.NotFound());

        endpoints.MapDelete("/api/setup/self-test/{id}", (string id, [FromServices] PhaseSelfTestService selfTest) =>
            Guid.TryParseExact(id, "N", out var guid) && selfTest.Cancel(guid) ? Results.NoContent() : Results.NotFound());

        // The active phase self-test (CONN-05): Switchboard switches a declared, idle relay stage on and
        // off through the effector gate, below protection, only in effective active mode.
        endpoints.MapGet("/api/setup/active-test/preconditions", (string? device, int? stage, [FromServices] ActivePhaseTestService activeTest) =>
            DeviceName.TryParse(device, out var name)
                ? Json(Preconditions(activeTest.Check(name.Value, stage ?? 1)))
                : Results.Problem("Name a declared device: ?device=<id>&stage=<n>.", statusCode: StatusCodes.Status400BadRequest));

        endpoints.MapPost("/api/setup/active-test", async (HttpRequest request, [FromServices] ActivePhaseTestService activeTest, CancellationToken cancellationToken) =>
        {
            if (await ReadAsync<ActiveTestStartRequest>(request, cancellationToken).ConfigureAwait(false) is not { Device: { } device } body
                || !DeviceName.TryParse(device, out var name) || body.Stage is < 1)
            {
                return JsonRequired("{\"device\": \"<id>\", \"stage\": 1}");
            }

            var (test, preconditions) = activeTest.Start(name.Value, body.Stage ?? 1);
            return test is null
                ? Json(new ActiveTestRefusedResponse("refused", [.. preconditions.Refusals.Select(Refusal)]), StatusCodes.Status409Conflict)
                : Json(new ActiveTestStartedResponse(test.Id.ToString("N"), test.ExpectedDuration.TotalSeconds), StatusCodes.Status202Accepted);
        });

        endpoints.MapGet("/api/setup/active-test/{id}", (string id, [FromServices] ActivePhaseTestService activeTest) =>
            Guid.TryParseExact(id, "N", out var guid) && activeTest.Get(guid) is { } view
                ? Json(ActiveTest(view))
                : Results.NotFound());

        endpoints.MapDelete("/api/setup/active-test/{id}", (string id, [FromServices] ActivePhaseTestService activeTest) =>
            Guid.TryParseExact(id, "N", out var guid) && activeTest.Cancel(guid) ? Results.NoContent() : Results.NotFound());

        endpoints.MapGet("/api/setup/evidence", (
            [FromServices] PhaseEvidenceStore evidence,
            [FromServices] ISignalDiagnosticsSource diagnostics,
            [FromServices] SetupDefaults defaults) => Json(Evidence(evidence, diagnostics.Current, defaults)));

        return endpoints;
    }

    internal static DiscoveryResponse Discovery(DiscoverySnapshot snapshot, SetupDefaults defaults) => new(
        snapshot.TakenAt,
        snapshot.RegistryAvailable,
        snapshot.Truncated,
        [.. snapshot.Entities.Select(e => new DiscoveredEntityDto(
            e.Entity.Value,
            e.Domain,
            e.Name,
            e.DeviceClass,
            e.StateClass,
            e.Unit,
            e.Platform,
            e.DeviceId,
            e.Disabled,
            e.Hidden,
            e.InRegistry,
            e.State,
            e.Reported,
            DerivedSensorRule.Check(e, snapshot, defaults) == DerivedCheck.Derived ? new DerivedDto(e.Platform!) : null,
            e.Options is { } options ? [.. options] : null,
            e.Own))],
        [.. snapshot.Devices.Select(d => new DiscoveredDeviceDto(d.Id, d.Name, d.Manufacturer, d.Model))]);

    internal static ReadoutDto Readout(CandidateReadout readout) => new(
        readout.Entity.Value,
        Snake(readout.Status),
        readout.State,
        readout.Unit,
        readout.LastReported,
        readout.Age?.TotalSeconds,
        new RefreshDto(readout.Refresh?.TotalSeconds, readout.Samples, readout.Needed));

    internal static SetupIssueDto Issue(SetupIssue issue) => new(
        issue.Code,
        issue.Severity == IssueSeverity.Error ? "error" : "warning",
        issue.Field,
        issue.DocumentPath,
        issue.Message,
        issue.Args);

    internal static ValidationResponse Validation(ValidationOutcome outcome)
    {
        var validation = outcome.Validation;
        var sum = validation.PhaseSum;
        return new ValidationResponse(
            outcome.Valid,
            [.. outcome.Issues.Select(Issue)],
            new ChecksDto(
                new PhaseSumDto(sum.State, sum.Residual, sum.Tolerance, sum.Samples, sum.Unit, sum.Reason),
                [.. validation.Refresh.Select(r => new RefreshCheckDto(r.Field, r.DetectedS, r.MaxAgeS, r.State, r.SuggestedMaxAgeS))]),
            validation.Reserve is { } reserve
                ? new SetupReserveDto(
                    [.. reserve.PerPhase.Select(p => new SetupReservePhaseDto(p.Phase.ToString(), p.ReserveW, p.MinimumSafeW, p.RefreshS, p.RefreshDetected, p.BelowMinimum))],
                    new ReserveExplanationDto(
                        reserve.Explanation.ReserveW,
                        reserve.Explanation.PhaseW,
                        reserve.Explanation.ContinuousA,
                        reserve.Explanation.AllocatableW,
                        reserve.Explanation.ReferenceStepW,
                        reserve.Explanation.WorstCaseA,
                        reserve.Explanation.Multiple,
                        ApiText.Snake(reserve.Explanation.Reaction),
                        reserve.Explanation.AllowedS))
                : null,
            [.. validation.SelfTest.Select(s => new SelfTestVerdictDto(
                s.Field,
                s.Device,
                s.StateEntity.Value,
                Snake(s.Judgement.Summary.Verdict),
                s.Judgement.Summary.Observed?.ToString(),
                s.Judgement.Summary.Reason is { } reason ? Snake(reason) : null,
                s.Judgement.Summary.Confirmed,
                s.Judgement.Summary.Mismatched,
                s.Judgement.Summary.Inconclusive))]);
    }

    internal static TemplatesResponse Templates(SetupDefaults defaults, ControlParameters control) => new(
        [.. DeviceTemplates.Describe(defaults, control).Select(t => new TemplateDto(
            t.Id,
            t.Type,
            t.Prefill,
            new InheritedDto(Duration(t.ReactionTime), Duration(t.MinOn), Duration(t.MinOff)),
            [.. t.ActuatorDomains],
            [.. t.StateDomains],
            t.DrawRecommended,
            [.. t.StopActuatorDomains]))],
        defaults.PrefillVoltage);

    /// <summary>A duration the way switchboard.yaml writes it: the largest whole unit.</summary>
    internal static string Duration(TimeSpan value) =>
        value.Ticks % TimeSpan.TicksPerHour == 0 && value > TimeSpan.Zero ? string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalHours}h")
        : value.Ticks % TimeSpan.TicksPerMinute == 0 && value > TimeSpan.Zero ? string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalMinutes}m")
        : value.Ticks % TimeSpan.TicksPerSecond == 0 ? string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalSeconds}s")
        : string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalMilliseconds}ms");

    internal static SelfTestResponse SelfTest(SelfTestView view) => new(
        view.Id.ToString("N"),
        Snake(view.Status),
        view.StartedAt,
        view.ExpiresAt,
        new SelfTestExpectedDto(view.Request.ExpectedPhase.ToString(), view.Request.ExpectedStep, view.Request.Unit, view.Request.StateEntity.Value),
        [.. view.Observations.Select(Observation)],
        Summary(view.Summary));

    internal static ActiveTestPreconditionsResponse Preconditions(ActiveTestPreconditions preconditions) =>
        new(preconditions.Ok, [.. preconditions.Refusals.Select(Refusal)], preconditions.ExpectedDuration?.TotalSeconds);

    internal static ActiveTestRefusalDto Refusal(ActiveTestRefusal refusal) => new(Snake(refusal.Code), refusal.Phase?.ToString(), refusal.Remaining?.TotalSeconds);

    internal static ActiveTestResponse ActiveTest(ActiveTestView view) => new(
        view.Id.ToString("N"),
        view.Device.Value,
        view.Stage,
        view.StateEntity.Value,
        view.ExpectedPhase.ToString(),
        Snake(view.State),
        Snake(view.Phase),
        view.StartedAt,
        view.OnAt,
        view.OffAt,
        view.ExpectedDuration.TotalSeconds,
        view.Judgement is { } judgement ? Summary(judgement.Summary) : null,
        [.. (view.Judgement?.Observations ?? []).Select(Observation)],
        view.AbortReason is { } reason ? Snake(reason) : null);

    internal static EvidenceResponse Evidence(PhaseEvidenceStore evidence, SignalDiagnostics diagnostics, SetupDefaults defaults)
    {
        var configuration = diagnostics.Configuration?.Configuration;
        if (configuration is null)
        {
            return new EvidenceResponse([]);
        }

        var phases = configuration.Connection.Phases;
        var powerTerms = Terms(configuration, phases, SignalRole.PhasePower);
        var currentTerms = Terms(configuration, phases, SignalRole.PhaseCurrent);
        return new EvidenceResponse([.. configuration.Devices
            .Where(d => d.Type == DeviceType.OnOff && d.Stages.Length == 1 && d.Stages[0].Phases.Length == 1)
            .Select(d =>
            {
                var stage = d.Stages[0];
                var judgement = PhaseEvidence.Judge(
                    evidence.For(stage.StateEntity),
                    stage.Phases[0],
                    stage.RatedPower?.Value,
                    stage.RatedCurrent.Value,
                    powerTerms,
                    currentTerms,
                    defaults.SelfTest.Classifier);
                return new DeviceEvidenceDto(d.Id.Value, stage.StateEntity.Value, stage.Phases[0].ToString(), [.. judgement.Observations.Select(Observation)], Summary(judgement.Summary));
            })]);
    }

    private static ImmutableDictionary<Phase, MeterTerm>? Terms(SwitchboardConfiguration configuration, IEnumerable<Phase> phases, SignalRole role)
    {
        var builder = ImmutableDictionary.CreateBuilder<Phase, MeterTerm>();
        foreach (var phase in phases)
        {
            if (configuration.Signals.FirstOrDefault(s => s.Role == role && s.Phase == phase) is not { } signal)
            {
                return null;
            }

            builder[phase] = new MeterTerm(signal.Entity, signal.Unit ?? string.Empty, MeterValues.SignName(signal.Sign));
        }

        return builder.ToImmutable();
    }

    private static ObservationDto Observation(JudgedObservation judged) => new(
        judged.Observation.At,
        judged.Observation.Direction > 0 ? "on" : "off",
        judged.Result.Deltas.OrderBy(d => d.Key).ToDictionary(d => d.Key.ToString(), d => d.Value),
        judged.Observation.Unit,
        Snake(judged.Result.Verdict),
        judged.Result.Observed?.ToString(),
        judged.Result.Reason is { } reason ? Snake(reason) : null,
        judged.Result.RatingRatio,
        Snake(judged.Observation.Source),
        judged.Observation.HouseLoad,
        judged.Observation.Note);

    private static SelfTestSummaryDto Summary(PhaseStepSummary summary) => new(
        Snake(summary.Verdict),
        summary.Observed?.ToString(),
        summary.Reason is { } reason ? Snake(reason) : null,
        summary.Confirmed,
        summary.Mismatched,
        summary.Inconclusive);

    /// <summary>The watch a self-test request describes, from the draft's meter rows; or why it cannot be watched.</summary>
    private static (Application.Setup.SelfTest.SelfTestRequest? Watch, ImmutableArray<SetupIssue> Issues) SelfTestRequest(SetupDraft draft, SelfTestStartRequest body, ControlParameters control, PhaseStepParameters parameters)
    {
        var profile = SetupValidator.Profile(draft.Connection, control.Connection.DefaultReserve);
        var phases = profile?.Phases ?? [Phase.L1, Phase.L2, Phase.L3];
        var power = SetupValidator.MeterTerms(draft, phases, power: true);
        var current = power is null ? SetupValidator.MeterTerms(draft, phases, power: false) : null;
        if (power is null && current is null)
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, "meter", "Map the meter of every phase (current or power, with unit and sign) before the self-test.")]);
        }

        string? stateText;
        string? phaseText;
        double? rating;
        double? ratedCurrent;
        double? ratedPower;
        string field;
        if (body.Device is { } device)
        {
            field = $"devices[{device.Index.ToString(CultureInfo.InvariantCulture)}]";
            if (device.Index < 0 || device.Index >= draft.Devices.Count)
            {
                return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, "devices", $"There is no device {device.Index} in the draft.")]);
            }

            var d = draft.Devices[device.Index];
            if (d.Type != "onoff" || d.Phase is not ("L1" or "L2" or "L3"))
            {
                return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.phase", "The self-test watches an on/off device on one phase (a charger or a three-phase device cannot be judged this way).")]);
            }

            stateText = d.StateEntity ?? d.Actuator;
            phaseText = d.Phase;
            rating = power is not null ? d.RatedPowerW : d.RatedCurrentA;
            ratedCurrent = d.RatedCurrentA ?? (d.RatedPowerW is { } w && profile is not null ? w / profile.Voltage.Value : null);
            ratedPower = d.RatedPowerW ?? (d.RatedCurrentA is { } a && profile is not null ? a * profile.Voltage.Value : null);
        }
        else
        {
            var load = body.TestLoad!;
            field = "testLoad";
            stateText = load.StateEntity;
            phaseText = load.Phase;
            rating = load.RatedPowerW is { } watts && power is null && profile is not null ? watts / profile.Voltage.Value : load.RatedPowerW;
            ratedPower = load.RatedPowerW;
            ratedCurrent = load.RatedPowerW is { } loadWatts && profile is not null ? loadWatts / profile.Voltage.Value : null;
        }

        if (!EntityId.TryParse(stateText, out var state))
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.{(body.Device is null ? "stateEntity" : "actuator")}", "The switched entity is required.")]);
        }

        if (!Enum.TryParse<Phase>(phaseText, out var phase) || !phases.Contains(phase))
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.phase", "The device's phase (L1, L2 or L3) is required.")]);
        }

        if (rating is not > 0)
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.{(power is not null ? "ratedPowerW" : "ratedCurrentA")}", "The rating is required: the step is judged against it.")]);
        }

        var terms = power ?? current!;
        var kind = power is not null ? SignalKind.Power : SignalKind.Current;

        // The draft's other devices and draws: one of them changing in the window is not this device's step.
        var (switches, draws) = SelfTestIsolation.Of(
            draft,
            body.Device?.Index,
            state.Value,
            ratedCurrent ?? (power is null ? rating.Value : 0),
            ratedPower ?? (power is not null ? rating.Value : 0),
            control,
            parameters);
        return (new Application.Setup.SelfTest.SelfTestRequest(
            state.Value,
            phase,
            rating.Value,
            power is not null ? "W" : "A",
            power is null && draft.Meter.CurrentSign == "unsigned",
            terms,
            control.Signals.DefaultSigma.GetValueOrDefault(kind))
        {
            OtherSwitches = switches,
            OtherDraws = draws,
        }, []);
    }

    private static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => ApiText.Snake(value);

    private static IResult JsonRequired(string shape) =>
        Results.Problem($"Send Content-Type: application/json with the body {shape}.", statusCode: StatusCodes.Status400BadRequest);

    private static IResult Json<T>(T value, int status = StatusCodes.Status200OK) => Results.Json(value, Answers, statusCode: status);

    /// <summary>The body, strictly: application/json only, and no key the API does not define.</summary>
    internal static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, Strict, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>camelCase, but a phase name (L1, L2, L3) keeps its spelling: the draft's per-phase keys.</summary>
internal sealed class PhaseAwareCamelCase : JsonNamingPolicy
{
    public static readonly PhaseAwareCamelCase Instance = new();

    public override string ConvertName(string name) => name is "L1" or "L2" or "L3" ? name : CamelCase.ConvertName(name);
}
