using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Options;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup;

/// <summary>What GET api/setup/draft answers: the draft read from the file and what the wizard keeps without editing it.</summary>
/// <param name="FileState">missing, valid or invalid (as the runtime parser reads the file now).</param>
/// <param name="Unreadable">The file exists but is not one usable YAML document: saving replaces it (after an acknowledgement).</param>
/// <param name="FileIssues">The parser's issues of the current file, at the fields they concern.</param>
public sealed record DraftView(
    string? Revision,
    string FileState,
    bool HasComments,
    bool Unreadable,
    SetupDraft Draft,
    ImmutableArray<PreservedSection> Preserved,
    ImmutableArray<SetupIssue> FileIssues);

/// <summary>A draft checked as it would be saved: the wizard's rules plus the runtime parser's verdict on the written document.</summary>
public sealed record ValidationOutcome(SetupValidation Validation, ImmutableArray<SetupIssue> Issues)
{
    public bool Valid => Issues.All(i => i.Severity != IssueSeverity.Error);
}

public abstract record SaveResult
{
    /// <param name="Reloaded">The running add-on read the new file within Setup:ReloadConfirmTimeout.</param>
    public sealed record Saved(string Revision, string? Backup, bool Reloaded, ConfigurationStatus? LoadedStatus) : SaveResult;

    /// <summary>The draft has errors (or a required acknowledgement is missing); nothing was written.</summary>
    public sealed record Refused(ImmutableArray<SetupIssue> Issues) : SaveResult;

    /// <summary>The file changed since the draft was loaded; nothing was written.</summary>
    public sealed record Conflict(string? CurrentRevision) : SaveResult;
}

/// <summary>
/// The wizard's edit cycle (UI-06): load the draft from the parsed file, validate a draft exactly as
/// it would be written (mapped onto the file, written, parsed by the runtime's own reader, then the
/// wizard's CONN-06 rules), and save it atomically with a backup, then wait for the running add-on
/// to hot-load it. It never touches the guard, dispatch or the effector, and commands nothing.
/// </summary>
public sealed class SetupService
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ISetupDocumentStore _store;
    private readonly ISetupDocumentCodec _codec;
    private readonly IConfigurationReloads _reloads;
    private readonly EntityDiscoveryCache _discovery;
    private readonly ICandidateReadouts _readouts;
    private readonly PhaseEvidenceStore _evidence;
    private readonly ISetupRuntimeView _runtime;
    private readonly SetupDefaults _defaults;
    private readonly ControlParameters _shippedControl;
    private readonly TimeProvider _signalTime;
    private readonly TimeProvider _hostTime;
    private readonly SetupOptions _options;
    private readonly ILogger<SetupService> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SetupService(
        ISetupDocumentStore store,
        ISetupDocumentCodec codec,
        IConfigurationReloads reloads,
        EntityDiscoveryCache discovery,
        ICandidateReadouts readouts,
        PhaseEvidenceStore evidence,
        ISetupRuntimeView runtime,
        SetupDefaults defaults,
        ControlParameters shippedControl,
        SignalClock clock,
        TimeProvider hostTime,
        IOptions<SetupOptions> options,
        ILogger<SetupService> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _codec = codec;
        _reloads = reloads;
        _discovery = discovery;
        _readouts = readouts;
        _evidence = evidence;
        _runtime = runtime;
        _defaults = defaults;
        _shippedControl = shippedControl;
        _signalTime = clock.Time;
        _hostTime = hostTime;
        _options = options.Value;
        _logger = logger;
    }

    public SetupDefaults Defaults => _defaults;

    /// <summary>The control parameters of the document on disk now (defaults merged with its control:).</summary>
    public ControlParameters CurrentControl()
    {
        var document = _store.Read();
        var tree = document.Text is { } text ? _codec.ReadTree(text).Root : null;
        return _codec.Control(tree) ?? _shippedControl;
    }

    public DraftView LoadDraft()
    {
        var document = _store.Read();
        if (!document.Exists)
        {
            return new DraftView(null, "missing", false, false, new SetupDraft(), [], []);
        }

        var (tree, errors) = document.Text is { } text ? _codec.ReadTree(text) : (null, []);
        var load = SetupDocumentMapper.Read(tree);
        var fileIssues = ImmutableArray.CreateBuilder<SetupIssue>();
        var state = "invalid";
        if (document.Text is { } content)
        {
            var parsed = _codec.Parse(Utf8.GetBytes(content), _signalTime.GetUtcNow());
            state = parsed.Status == ConfigurationStatus.Valid ? "valid" : "invalid";
            var fields = SetupDocumentMapper.Apply(tree, load.Draft).Fields;
            fileIssues.AddRange(parsed.Errors.Select(e => FromParser(e, fields, IssueSeverity.Error)));
            fileIssues.AddRange(parsed.Warnings.Select(w => FromParser(w, fields, IssueSeverity.Warning)));
        }
        else
        {
            fileIssues.Add(new SetupIssue(SetupIssueCodes.DocumentInvalid, IssueSeverity.Error, null, null, document.Problem ?? "The file cannot be read.", ImmutableDictionary<string, string>.Empty));
        }

        return new DraftView(
            document.Revision,
            state,
            document.Text is { } raw && _codec.HasComments(raw),
            tree is null,
            load.Draft,
            load.Preserved,
            fileIssues.ToImmutable());
    }

    /// <summary>Validates a draft as it would be saved; nothing is written.</summary>
    public async Task<ValidationOutcome> ValidateAsync(SetupDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var prepared = await PrepareAsync(draft, _store.Read(), cancellationToken).ConfigureAwait(false);
        return new ValidationOutcome(prepared.Validation, prepared.Issues);
    }

    public async Task<SaveResult> SaveAsync(SetupDraft draft, string? baseRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _store.Read();
            if (!string.Equals(current.Revision, baseRevision, StringComparison.Ordinal))
            {
                return new SaveResult.Conflict(current.Revision);
            }

            var prepared = await PrepareAsync(draft, current, cancellationToken).ConfigureAwait(false);
            var issues = prepared.Issues.ToBuilder();
            var acknowledgements = draft.Acknowledgements;
            if (!acknowledgements.NotProtective)
            {
                issues.Add(SetupIssue.Error(
                    SetupIssueCodes.AcknowledgementRequired,
                    "acknowledgements.notProtective",
                    "Confirm that you read the statement: Switchboard is not a protective device and does not replace breakers or EVSE protection."));
            }

            if (current.Exists && prepared.BaseTree is null && !acknowledgements.ReplaceUnreadable)
            {
                issues.Add(SetupIssue.Error(
                    SetupIssueCodes.ReplaceUnreadableRequired,
                    "acknowledgements.replaceUnreadable",
                    $"The current file cannot be read ({current.Problem ?? "not one usable YAML document"}); confirm that saving replaces it. The original is kept as a backup."));
            }

            if (current.Text is { } text && _codec.HasComments(text) && !acknowledgements.CommentsDropped)
            {
                issues.Add(SetupIssue.Error(
                    SetupIssueCodes.CommentsAcknowledgementRequired,
                    "acknowledgements.commentsDropped",
                    "The file has comments, which a save from the wizard cannot keep; confirm (the original, with its comments, is kept as a backup)."));
            }

            if (issues.Any(i => i.Severity == IssueSeverity.Error))
            {
                return new SaveResult.Refused(issues.ToImmutable());
            }

            // Nothing changed: no write, no backup; the file in force is already this one.
            if (current.Revision is { } unchanged && string.Equals(current.Text, prepared.Text, StringComparison.Ordinal))
            {
                return new SaveResult.Saved(unchanged, null, true, prepared.Parsed.Status);
            }

            // Subscribed before the write, so the reload of it cannot be missed.
            var reloaded = new TaskCompletionSource<ConfigurationReloaded>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? expected = null;
            void OnReloaded(object? sender, ConfigurationReloaded reload)
            {
                if (Volatile.Read(ref expected) is { } fingerprint && string.Equals(reload.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    reloaded.TrySetResult(reload);
                }
            }

            _reloads.Reloaded += OnReloaded;
            try
            {
                var outcome = _store.Write(prepared.Text, baseRevision);
                if (outcome is SaveOutcome.Conflict conflict)
                {
                    return new SaveResult.Conflict(conflict.CurrentRevision);
                }

                var saved = (SaveOutcome.Saved)outcome;
                if (!_reloads.Watching)
                {
                    // Nothing reads the file in this process (a test host): nothing to wait for.
                    return new SaveResult.Saved(saved.Revision, saved.Backup, false, null);
                }

                Volatile.Write(ref expected, saved.Revision);
                _reloads.RequestCheck();
                try
                {
                    var reload = await reloaded.Task.WaitAsync(_options.ReloadConfirmTimeout, _hostTime, cancellationToken).ConfigureAwait(false);
                    return new SaveResult.Saved(saved.Revision, saved.Backup, true, reload.Status);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("The saved configuration was not reloaded within {Timeout}; the running add-on reads it at its next check", _options.ReloadConfirmTimeout);
                    return new SaveResult.Saved(saved.Revision, saved.Backup, false, null);
                }
            }
            finally
            {
                _reloads.Reloaded -= OnReloaded;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>The entities of a draft, the meter first; the wizard leases them so their readouts and refresh are known.</summary>
    public static IReadOnlyList<EntityId> Entities(SetupDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var texts = new List<string?>();
        foreach (var row in new[] { draft.Meter.Phases.L1, draft.Meter.Phases.L2, draft.Meter.Phases.L3 })
        {
            texts.Add(row?.Current?.Entity);
            texts.Add(row?.Power?.Entity);
        }

        texts.Add(draft.Meter.Total.Power?.Entity);
        texts.Add(draft.Meter.Total.Current?.Entity);
        foreach (var row in new[] { draft.Meter.Phases.L1, draft.Meter.Phases.L2, draft.Meter.Phases.L3 })
        {
            texts.Add(row?.Voltage?.Entity);
        }

        var inverter = draft.Sources.Inverter;
        texts.Add(inverter?.Power?.Entity);
        texts.AddRange([inverter?.PhaseCurrents?.L1?.Entity, inverter?.PhaseCurrents?.L2?.Entity, inverter?.PhaseCurrents?.L3?.Entity]);
        foreach (var device in draft.Devices.Where(d => !d.Existing || d.Editable))
        {
            texts.AddRange([device.StateEntity ?? device.Actuator, device.Draw?.Current, device.Draw?.Power]);
        }

        return [.. texts.Select(t => EntityId.TryParse(t, out var id) ? id : null).OfType<EntityId>().Distinct()];
    }

    /// <summary>A runtime parser issue at the draft field it concerns (UI-05); no line numbers.</summary>
    public static SetupIssue FromParser(ConfigurationIssue issue, FieldMap fields, IssueSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(fields);
        var field = issue.Path switch
        {
            "signals" when issue.Message.Contains("inverter", StringComparison.Ordinal) => "sources.inverter",
            "signals" => "meter",
            "" => null,
            _ => fields.FieldFor(issue.Path),
        };
        return new SetupIssue(
            SetupIssueCodes.DocumentInvalid,
            severity,
            field,
            issue.Path.Length == 0 ? null : issue.Path,
            issue.Message,
            ImmutableDictionary<string, string>.Empty.Add("issue", issue.Code));
    }

    private sealed record Prepared(ConfigNode? BaseTree, string Text, ConfigurationResult Parsed, SetupValidation Validation, ImmutableArray<SetupIssue> Issues);

    private async Task<Prepared> PrepareAsync(SetupDraft draft, DocumentRead current, CancellationToken cancellationToken)
    {
        var baseTree = current.Text is { } text ? _codec.ReadTree(text).Root : null;
        var mapped = SetupDocumentMapper.Apply(baseTree, draft);
        var written = _codec.Write(mapped.Root);
        var now = _signalTime.GetUtcNow();
        var parsed = _codec.Parse(Utf8.GetBytes(written), now);
        var control = _codec.Control(mapped.Root) ?? _shippedControl;

        DiscoverySnapshot? discovery = null;
        try
        {
            discovery = await _discovery.GetAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscoveryUnavailableException ex)
        {
            _logger.LogDebug("Setup validation without discovery: {Reason}", ex.Message);
        }

        _readouts.Lease([.. Entities(draft).Take(_options.MaxReadoutEntities)]);
        var context = new SetupContext(discovery, _readouts, _runtime.DetectedRefresh, _defaults, control, _evidence, now)
        {
            DeclaredSigma = SetupDocumentMapper.DeclaredSigma(mapped.Root),
        };
        var validation = SetupValidator.Validate(draft, context);
        var issues = new List<SetupIssue>(mapped.Issues);
        issues.AddRange(validation.Issues);

        // The wizard's own required-field errors name the exact field; the parser's "a signal with
        // role ... is missing" says the same less precisely.
        var required = validation.Issues.Where(i => i.Code == SetupIssueCodes.RequiredField && i.Field is not null).Select(i => i.Field!).ToList();
        issues.AddRange(parsed.Errors
            .Where(e => !(required.Count > 0 && e.Code == ConfigurationIssueCodes.RequiredSignalMissing))
            .Select(e => FromParser(e, mapped.Fields, IssueSeverity.Error))
            .Where(e => e.Field is null || !required.Contains(e.Field)));
        issues.AddRange(parsed.Warnings.Select(w => FromParser(w, mapped.Fields, IssueSeverity.Warning)));
        return new Prepared(baseTree, written, parsed, validation, [.. issues]);
    }
}
