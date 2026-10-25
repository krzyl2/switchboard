using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Setup;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// UI-06: the wizard writes /config/switchboard.yaml atomically (the running add-on, and a crash,
/// only ever see a whole file), keeps a bounded set of timestamped backups next to it, never
/// overwrites a file changed since the wizard read it, and writes nothing outside the file's own
/// directory. Comments of a hand-written file cannot be kept: the writer's header says so, and is
/// recognised so the acknowledgement is not asked for again.
/// </summary>
public sealed class ConfigurationDocumentStoreTests : IDisposable
{
    private readonly TempDataDirectory _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 10, 15, 0, TimeSpan.Zero));

    private string ConfigDirectory => Path.Combine(_root.Path, "config");

    private string FilePath => Path.Combine(ConfigDirectory, "switchboard.yaml");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Clean_install_creates_file_without_backup()
    {
        var store = Store();

        var saved = Assert.IsType<SaveOutcome.Saved>(store.Write("schema_version: 1\n", null));

        Assert.Null(saved.Backup);
        Assert.Equal("schema_version: 1\n", File.ReadAllText(FilePath));
        Assert.Equal(store.Read().Revision, saved.Revision);
        Assert.Equal(["switchboard.yaml"], Files());
    }

    [Fact]
    public void Backup_kept_next_to_file()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(FilePath, "# hand-written\nschema_version: 1\n");
        var store = Store();

        var saved = Assert.IsType<SaveOutcome.Saved>(store.Write("schema_version: 1\nconnection: {}\n", store.Read().Revision));

        Assert.Equal("switchboard.yaml.20261025T101500000Z.bak", saved.Backup);
        Assert.Equal("# hand-written\nschema_version: 1\n", File.ReadAllText(Path.Combine(ConfigDirectory, saved.Backup!)));
        Assert.Equal("schema_version: 1\nconnection: {}\n", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Stale_base_revision_is_409_and_writes_nothing()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(FilePath, "schema_version: 1\n");
        var store = Store();
        var loaded = store.Read().Revision;
        File.WriteAllText(FilePath, "schema_version: 1\n# edited by hand meanwhile\n");

        var conflict = Assert.IsType<SaveOutcome.Conflict>(store.Write("schema_version: 1\nsignals: {}\n", loaded));

        Assert.Equal(store.Read().Revision, conflict.CurrentRevision);
        Assert.Equal("schema_version: 1\n# edited by hand meanwhile\n", File.ReadAllText(FilePath));
        Assert.Equal(["switchboard.yaml"], Files());
    }

    [Fact]
    public void A_file_that_appeared_meanwhile_is_not_overwritten_by_a_clean_install()
    {
        var store = Store();
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(FilePath, "schema_version: 1\n");

        Assert.IsType<SaveOutcome.Conflict>(store.Write("schema_version: 1\nsignals: {}\n", null));
    }

    [Fact]
    public void Two_saves_in_one_second_keep_two_backups()
    {
        var store = Store();
        store.Write("a: 1\n", null);
        store.Write("a: 2\n", store.Read().Revision);
        store.Write("a: 3\n", store.Read().Revision);

        var backups = Files().Where(f => f.EndsWith(".bak", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, backups.Count);
        Assert.Equal(["a: 1\n", "a: 2\n"], backups.Select(b => File.ReadAllText(Path.Combine(ConfigDirectory, b))).Order());
    }

    [Fact]
    public void Backups_bounded_oldest_removed()
    {
        var store = Store(maxBackups: 2);
        store.Write("v: 0\n", null);
        for (var i = 1; i <= 4; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            store.Write($"v: {i}\n", store.Read().Revision);
        }

        var backups = Files().Where(f => f.EndsWith(".bak", StringComparison.Ordinal)).Order().ToList();
        Assert.Equal(2, backups.Count);
        Assert.Equal(["v: 2\n", "v: 3\n"], backups.Select(b => File.ReadAllText(Path.Combine(ConfigDirectory, b))));
    }

    [Fact]
    public void The_backup_of_this_save_survives_a_clock_behind_earlier_backups()
    {
        // A Raspberry Pi boots with an old clock before NTP: the new backup's stamp sorts before the
        // existing ones. Pruning by name alone would delete it in the same write, so the previous
        // file would survive nowhere while the answer still names its backup.
        var store = Store(maxBackups: 2);
        store.Write("v: 0\n", null);
        for (var i = 1; i <= 2; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            store.Write($"v: {i}\n", store.Read().Revision);
        }

        // The add-on restarted with the clock 400 days behind.
        var behind = new ConfigurationDocumentStore(new ConfigurationFileReader(FilePath, 262144, TestConfigurations.Defaults), 2, new FakeTimeProvider(_time.GetUtcNow() - TimeSpan.FromDays(400)), NullLogger<ConfigurationDocumentStore>.Instance);
        var saved = Assert.IsType<SaveOutcome.Saved>(behind.Write("v: 3\n", behind.Read().Revision));

        Assert.NotNull(saved.Backup);
        Assert.Equal("v: 2\n", File.ReadAllText(Path.Combine(ConfigDirectory, saved.Backup!)));
        Assert.Equal(2, Files().Count(f => store.IsBackupName(f)));
    }

    [Fact]
    public void Pruning_never_touches_foreign_bak_files()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(Path.Combine(ConfigDirectory, "switchboard.yaml.old.bak"), "mine");
        File.WriteAllText(Path.Combine(ConfigDirectory, "switchboard.yaml.20200101T000000000Z.bak.keep"), "mine too");
        var store = Store(maxBackups: 1);
        store.Write("v: 0\n", null);
        for (var i = 1; i <= 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            store.Write($"v: {i}\n", store.Read().Revision);
        }

        Assert.True(File.Exists(Path.Combine(ConfigDirectory, "switchboard.yaml.old.bak")));
        Assert.True(File.Exists(Path.Combine(ConfigDirectory, "switchboard.yaml.20200101T000000000Z.bak.keep")));
        Assert.Single(Files(), f => store.IsBackupName(f));
    }

    [Fact]
    public void Save_is_atomic_old_file_whole_until_rename()
    {
        // The new content is complete in a temporary file before the rename; nothing of it is left
        // behind, and a write that cannot complete leaves the old file exactly as it was.
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(FilePath, "old: true\n");
        var store = Store();
        var revision = store.Read().Revision;

        Assert.ThrowsAny<EncoderFallbackException>(() => store.Write("broken: \uD800\n", revision));
        Assert.Equal("old: true\n", File.ReadAllText(FilePath));
        Assert.Equal(["switchboard.yaml"], Files());

        store.Write("new: true\n", revision);
        Assert.Equal("new: true\n", File.ReadAllText(FilePath));
        Assert.DoesNotContain(Files(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_only_inside_config_directory()
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, "data"));
        var before = Directory.GetFileSystemEntries(_root.Path, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(ConfigDirectory, StringComparison.Ordinal)).Order().ToList();
        var store = Store();

        store.Write("a: 1\n", null);
        store.Write("a: 2\n", store.Read().Revision);

        var after = Directory.GetFileSystemEntries(_root.Path, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(ConfigDirectory, StringComparison.Ordinal)).Order().ToList();
        Assert.Equal(before, after);
        Assert.All(Files(), f => Assert.StartsWith("switchboard.yaml", f, StringComparison.Ordinal));
    }

    [Fact]
    public void Wizard_written_file_does_not_require_the_comments_acknowledgement_again()
    {
        var text = YamlDocumentWriter.Write(SetupFixtures.Tree("schema_version: 1\n"));

        Assert.False(YamlDocumentWriter.HasComments(text));
        Assert.True(YamlDocumentWriter.HasComments(text + "# my note\n"));
        Assert.True(YamlDocumentWriter.HasComments("schema_version: 1   # inline note\n"));
        Assert.False(YamlDocumentWriter.HasComments("name: \"Grzałka #1\"\n"));
        Assert.True(YamlDocumentWriter.HasComments(TestConfigurations.Example));
    }

    [Fact]
    public void Save_refuses_a_document_over_the_reader_size_cap()
    {
        // The wizard validates the bytes it would write through the runtime's own reader, size cap included.
        var reader = new ConfigurationFileReader(FilePath, 64, TestConfigurations.Defaults);
        var codec = new SetupDocumentCodec(reader, TestConfigurations.Defaults);

        var result = codec.Parse(Encoding.UTF8.GetBytes("schema_version: 1\n" + new string('#', 100) + "\n"), _time.GetUtcNow());

        Assert.Equal(Switchboard.Application.Configuration.ConfigurationIssueCodes.FileTooLarge, Assert.Single(result.Errors).Code);
    }

    private ConfigurationDocumentStore Store(int maxBackups = 10) =>
        new(new ConfigurationFileReader(FilePath, 262144, TestConfigurations.Defaults), maxBackups, _time, NullLogger<ConfigurationDocumentStore>.Instance);

    private List<string> Files() =>
        Directory.Exists(ConfigDirectory) ? [.. Directory.GetFiles(ConfigDirectory).Select(Path.GetFileName).OfType<string>().Order()] : [];
}
