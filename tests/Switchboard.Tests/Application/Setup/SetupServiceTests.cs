using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Options;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Tests.Infrastructure;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// UI-05, UI-06 and SAFE-06 end to end in the backend: a draft is validated exactly as it would be
/// written (runtime parser and wizard rules), refused with the exact field when it is wrong (the file
/// untouched), saved atomically with a backup only after the not-a-protective-device statement was
/// acknowledged, and hot-loaded by the running watcher without a restart. Re-running the wizard on a
/// file keeps every device.
/// </summary>
public sealed class SetupServiceTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private readonly TempDataDirectory _root = new();
    private readonly ObservableTimeProvider _time = new();
    private readonly SignalInbox _inbox = new();
    private readonly ConfigurationFileReader _reader;
    private readonly ConfigurationFileWatcher _watcher;
    private readonly SetupService _service;

    public SetupServiceTests()
    {
        Directory.CreateDirectory(ConfigDirectory);
        _reader = new ConfigurationFileReader(FilePath, 262144, TestConfigurations.Defaults);
        _watcher = new ConfigurationFileWatcher(
            _reader,
            _inbox,
            MsOptions.Create(new ConfigurationOptions { ReloadDebounce = Debounce, PollInterval = TimeSpan.FromSeconds(5), MaxFileBytes = 262144 }),
            _time,
            NullLogger<ConfigurationFileWatcher>.Instance) { FileEventsEnabled = false };
        _service = new SetupService(
            new ConfigurationDocumentStore(_reader, 10, _time, NullLogger<ConfigurationDocumentStore>.Instance),
            new SetupDocumentCodec(_reader, TestConfigurations.Defaults),
            _watcher,
            new EntityDiscoveryCache(new FixtureDiscovery(), _time, TimeSpan.FromMinutes(1)),
            new FakeReadouts(_time),
            new PhaseEvidenceStore(20),
            new NoRuntime(),
            SetupFixtures.Defaults,
            SetupFixtures.Control,
            new SignalClock(_time),
            _time,
            MsOptions.Create(DiscoveryLinkTests.Options()),
            NullLogger<SetupService>.Instance);
    }

    private string ConfigDirectory => Path.Combine(_root.Path, "config");

    private string FilePath => Path.Combine(ConfigDirectory, "switchboard.yaml");

    public void Dispose()
    {
        _watcher.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task Clean_install_is_saved_without_backup_and_hot_loaded()
    {
        _watcher.Start();
        Assert.Equal("missing", _service.LoadDraft().FileState);

        var saved = Assert.IsType<SaveResult.Saved>(await SaveAsync(SetupFixtures.MaintainerDraft(), null));

        Assert.Null(saved.Backup);
        Assert.Equal((true, ConfigurationStatus.Valid), (saved.Reloaded, saved.LoadedStatus));
        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(File.ReadAllText(FilePath)).Status);
    }

    [Fact]
    public async Task Saved_document_hot_reloaded_without_restart()
    {
        // The running watcher (the same one the add-on uses) reads the new file and hands the store
        // the new configuration: 20 signals (the charger's stop included) and the four devices, no restart.
        File.WriteAllText(FilePath, TestConfigurations.Example);
        _watcher.Start();
        Assert.True(_inbox.Reader.TryRead(out _));
        var draft = _service.LoadDraft();
        var renamed = draft.Draft with
        {
            Devices = [draft.Draft.Devices[0] with { Name = "Grzałka łazienka" }, .. draft.Draft.Devices.Skip(1)],
            Acknowledgements = new AcknowledgementsDraft { NotProtective = true, CommentsDropped = true },
        };

        var saved = Assert.IsType<SaveResult.Saved>(await SaveAsync(renamed, draft.Revision));

        Assert.True(saved.Reloaded);
        Assert.True(_inbox.Reader.TryRead(out var message));
        var result = Assert.IsType<ConfigurationInput>(message).Result;
        Assert.Equal((ConfigurationStatus.Valid, 22), (result.Status, result.Configuration!.Signals.Length));
        Assert.Equal(["Grzałka łazienka", "Grzałka CWU L2", "Grzałka CWU L3", "Ładowarka go-e"], result.Configuration.Devices.Select(d => d.Name));
        Assert.NotNull(saved.Backup);
        Assert.Equal(TestConfigurations.Example, File.ReadAllText(Path.Combine(ConfigDirectory, saved.Backup!)));
    }

    [Fact]
    public async Task Re_running_the_wizard_on_the_example_keeps_every_device()
    {
        File.WriteAllText(FilePath, TestConfigurations.Example);
        _watcher.Start();
        var draft = _service.LoadDraft();
        Assert.Equal(("valid", true), (draft.FileState, draft.HasComments));
        Assert.Equal(4, draft.Draft.Devices.Count);

        var saved = await SaveAsync(draft.Draft with { Acknowledgements = new AcknowledgementsDraft { NotProtective = true, CommentsDropped = true } }, draft.Revision);

        Assert.IsType<SaveResult.Saved>(saved);
        var before = TestConfigurations.Valid(TestConfigurations.Example).Configuration!;
        var after = TestConfigurations.Valid(File.ReadAllText(FilePath)).Configuration!;
        Assert.Equal(before.Devices.Select(d => (d.Id, d.Name, d.Type, d.Stages[0].Actuator, d.Stages[0].Phases[0])), after.Devices.Select(d => (d.Id, d.Name, d.Type, d.Stages[0].Actuator, d.Stages[0].Phases[0])));
        Assert.Equal(before.Signals.Select(s => s.ToString()), after.Signals.Select(s => s.ToString()));
    }

    [Fact]
    public async Task Invalid_draft_refused_file_untouched()
    {
        File.WriteAllText(FilePath, TestConfigurations.Example);
        var draft = _service.LoadDraft();
        var broken = draft.Draft with
        {
            Devices = [draft.Draft.Devices[0] with { RatedCurrentA = 99 }, .. draft.Draft.Devices.Skip(1)],
            Acknowledgements = new AcknowledgementsDraft { NotProtective = true, CommentsDropped = true },
        };

        var refused = Assert.IsType<SaveResult.Refused>(await _service.SaveAsync(broken, draft.Revision, CancellationToken.None));

        Assert.Contains(refused.Issues, i => i.Field == "devices[0].ratedCurrentA" && i.Severity == IssueSeverity.Error);
        Assert.Equal(TestConfigurations.Example, File.ReadAllText(FilePath));
        Assert.Single(Directory.GetFiles(ConfigDirectory));
    }

    [Fact]
    public async Task Save_without_not_protective_acknowledgement_is_refused()
    {
        var refused = Assert.IsType<SaveResult.Refused>(await _service.SaveAsync(SetupFixtures.MaintainerDraft(acknowledged: false), null, CancellationToken.None));

        Assert.Contains(refused.Issues, i => i.Code == SetupIssueCodes.AcknowledgementRequired && i.Field == "acknowledgements.notProtective");
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Commented_file_requires_comments_acknowledgement()
    {
        File.WriteAllText(FilePath, TestConfigurations.Example);
        _watcher.Start();
        var draft = _service.LoadDraft();

        var refused = Assert.IsType<SaveResult.Refused>(await _service.SaveAsync(draft.Draft with { Acknowledgements = new AcknowledgementsDraft { NotProtective = true } }, draft.Revision, CancellationToken.None));
        Assert.Contains(refused.Issues, i => i.Code == SetupIssueCodes.CommentsAcknowledgementRequired && i.Field == "acknowledgements.commentsDropped");

        Assert.IsType<SaveResult.Saved>(await SaveAsync(draft.Draft with { Acknowledgements = new AcknowledgementsDraft { NotProtective = true, CommentsDropped = true } }, draft.Revision));

        // The wizard's own header is no comment of the user's: the next save does not ask again.
        var again = _service.LoadDraft();
        Assert.False(again.HasComments);
        Assert.IsType<SaveResult.Saved>(await SaveAsync(again.Draft with { Acknowledgements = new AcknowledgementsDraft { NotProtective = true } }, again.Revision));
    }

    [Fact]
    public async Task Stale_base_revision_is_409_and_writes_nothing()
    {
        File.WriteAllText(FilePath, TestConfigurations.Example);
        var draft = _service.LoadDraft();
        File.AppendAllText(FilePath, "# a hand edit meanwhile\n");

        var conflict = Assert.IsType<SaveResult.Conflict>(await _service.SaveAsync(draft.Draft with { Acknowledgements = new AcknowledgementsDraft { NotProtective = true, CommentsDropped = true } }, draft.Revision, CancellationToken.None));

        Assert.NotEqual(draft.Revision, conflict.CurrentRevision);
        Assert.EndsWith("# a hand edit meanwhile\n", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_file_is_not_overwritten_without_acknowledgement()
    {
        // A YAML syntax error leaves the wizard an empty draft: saving it would replace the whole file.
        File.WriteAllText(FilePath, "schema_version: 1\ndevices: [unclosed\n");
        _watcher.Start();
        var draft = _service.LoadDraft();
        Assert.True(draft.Unreadable);
        Assert.Contains(draft.FileIssues, i => i.Args["issue"] == ConfigurationIssueCodes.YamlSyntax);

        var refused = Assert.IsType<SaveResult.Refused>(await _service.SaveAsync(SetupFixtures.MaintainerDraft(), draft.Revision, CancellationToken.None));
        Assert.Contains(refused.Issues, i => i.Code == SetupIssueCodes.ReplaceUnreadableRequired && i.Field == "acknowledgements.replaceUnreadable");

        var replace = SetupFixtures.MaintainerDraft() with { Acknowledgements = new AcknowledgementsDraft { NotProtective = true, ReplaceUnreadable = true } };
        var saved = Assert.IsType<SaveResult.Saved>(await SaveAsync(replace, draft.Revision));
        Assert.Equal("schema_version: 1\ndevices: [unclosed\n", File.ReadAllText(Path.Combine(ConfigDirectory, saved.Backup!)));
    }

    [Fact]
    public async Task Validation_reports_the_parser_and_the_wizard_rules_together()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var outcome = await _service.ValidateAsync(draft with { Connection = draft.Connection with { BreakerRatingA = null } }, CancellationToken.None);

        Assert.False(outcome.Valid);
        Assert.Single(outcome.Issues, i => i.Field == "connection.breakerRatingA" && i.Severity == IssueSeverity.Error);
        Assert.Contains(outcome.Issues, i => i.Code == SetupIssueCodes.PhaseSelfTestMissing);
    }

    private async Task<SaveResult> SaveAsync(SetupDraft draft, string? revision)
    {
        var save = _service.SaveAsync(draft, revision, CancellationToken.None);
        if (!save.IsCompleted)
        {
            await _time.WaitForTimerAsync(Debounce);
            _time.Advance(Debounce);
        }

        return await save.Soon();
    }

    private sealed class FixtureDiscovery : IEntityDiscoverySource
    {
        public Task<DiscoverySnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(SetupValidatorTests.Fixture());
    }

    private sealed class NoRuntime : ISetupRuntimeView
    {
        public SwitchboardConfiguration? Configuration => null;

        public IReadOnlyDictionary<EntityId, TimeSpan> DetectedRefresh { get; } = ImmutableDictionary<EntityId, TimeSpan>.Empty;
    }
}
