using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Options;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The document is edited by hand while the add-on runs. Every change must take effect without a
/// restart, a broken edit must put the store in safe state and say why, and nothing may depend on
/// file-system events alone (they do not cross every container bind mount). All on the virtual clock.
/// </summary>
public sealed class ConfigurationFileWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private const int MaxBytes = 64 * 1024;

    private readonly ObservableTimeProvider _time = new();
    private readonly SignalInbox _inbox = new();
    private readonly LogCapture _logs = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private ConfigurationFileWatcher? _watcher;

    public ConfigurationFileWatcherTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "switchboard.yaml");
    }

    [Fact]
    public void Start_loads_the_document_once()
    {
        Write(TestConfigurations.ThreePhase());

        Start();

        Assert.Equal(ConfigurationStatus.Valid, Assert.Single(Results()).Status);
    }

    [Fact]
    public void Edits_debounced_into_one_reload()
    {
        Write(TestConfigurations.ThreePhase());
        Start();
        Results();

        Write(TestConfigurations.ThreePhase(currentMaxAge: "60s"));
        _watcher!.OnFileEvent();
        _time.Advance(Debounce / 2);
        Write(TestConfigurations.ThreePhase(currentMaxAge: "70s"));
        _watcher.OnFileEvent();
        _time.Advance(Debounce - TimeSpan.FromTicks(1));
        Assert.Empty(Results());

        _time.Advance(TimeSpan.FromTicks(1));
        var result = Assert.Single(Results());
        Assert.Equal(TimeSpan.FromSeconds(70), result.Configuration!.Signals.First(s => s.Name.Value == "grid_current_l1").MaxAge);
    }

    [Fact]
    public void Unchanged_content_does_not_reload()
    {
        Write(TestConfigurations.ThreePhase());
        Start();
        Results();

        File.SetLastWriteTimeUtc(_path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _watcher!.OnFileEvent();
        _time.Advance(Debounce);
        _time.Advance(Poll);
        _time.Advance(Debounce);

        Assert.Empty(Results());
    }

    [Fact]
    public void Broken_edit_switches_to_safe_state_and_says_why()
    {
        Write(TestConfigurations.ThreePhase());
        Start();
        Results();

        Write(TestConfigurations.ThreePhase().Replace("max_age: 90s", "max_agee: 90s", StringComparison.Ordinal));
        _watcher!.OnFileEvent();
        _time.Advance(Debounce);

        var result = Assert.Single(Results());
        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == "unknown_key" && e.Line is not null);
        Assert.Contains(_logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error
                                            && e.Text.Contains(":", StringComparison.Ordinal) && e.Text.Contains("unknown_key", StringComparison.Ordinal));
    }

    [Fact]
    public void Fixing_file_restores_valid_state_without_restart()
    {
        Write("schema_version: 1\nconnection: [");
        Start();
        Assert.Equal(ConfigurationStatus.Invalid, Assert.Single(Results()).Status);

        Write(TestConfigurations.ThreePhase());
        _watcher!.OnFileEvent();
        _time.Advance(Debounce);

        Assert.Equal(ConfigurationStatus.Valid, Assert.Single(Results()).Status);
    }

    [Fact]
    public void Poll_notices_a_change_without_any_file_event()
    {
        Write(TestConfigurations.ThreePhase());
        Start();
        Results();

        Write(TestConfigurations.ThreePhase(currentMaxAge: "60s"));
        _time.Advance(Poll);
        Assert.Empty(Results());
        _time.Advance(Debounce);

        Assert.Single(Results());
    }

    [Fact]
    public void Missing_folder_is_missing_and_found_later_by_the_poll()
    {
        var path = Path.Combine(_directory, "not-yet", "switchboard.yaml");
        Start(path);
        var missing = Assert.Single(Results());
        Assert.Equal((ConfigurationStatus.Missing, "file_missing"), (missing.Status, missing.Errors.Single().Code));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, TestConfigurations.ThreePhase());
        _time.Advance(Poll);
        _time.Advance(Debounce);

        Assert.Equal(ConfigurationStatus.Valid, Assert.Single(Results()).Status);
    }

    [Fact]
    public void Oversized_file_is_refused_unread()
    {
        Write("# " + new string('x', MaxBytes) + "\n");

        Start();

        Assert.Equal("file_too_large", Assert.Single(Results()).Errors.Single().Code);
    }

    [Fact]
    public void Text_that_is_not_utf8_is_refused()
    {
        File.WriteAllBytes(_path, [0x61, 0x3A, 0x20, 0xFF, 0xFE, 0x0A]);

        Start();

        Assert.Equal("file_unreadable", Assert.Single(Results()).Errors.Single().Code);
    }

    [Fact]
    public void Configuration_change_resubscribes()
    {
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        var source = new RecordingSignalSource();
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(_time.GetUtcNow()), null, [], []));
        var runner = new SignalStoreRunner(_inbox, store, source, holder, new SignalClock(_time), NullLogger<SignalStoreRunner>.Instance);
        Write(TestConfigurations.ThreePhase());
        Start();
        runner.Step();

        Write(TestConfigurations.ThreePhase(inverter: false));
        _watcher!.OnFileEvent();
        _time.Advance(Debounce);
        runner.Step();

        Assert.Equal(2, source.Tracks.Count);
        Assert.Equal(9, source.Tracks[1].Count);
        Assert.Equal(ConfigurationStatus.Valid, holder.Current.Configuration!.Status);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }

    private void Start(string? path = null)
    {
        var options = new ConfigurationOptions { ReloadDebounce = Debounce, PollInterval = Poll, MaxFileBytes = MaxBytes };
        var reader = new ConfigurationFileReader(path ?? _path, MaxBytes, TestConfigurations.Defaults);
        // Real file events arrive on their own threads at real times; the tests drive OnFileEvent
        // themselves so that every reload happens at a known virtual time. The poll works alone.
        _watcher = new ConfigurationFileWatcher(reader, _inbox, MsOptions.Create(options), _time, _logs.For<ConfigurationFileWatcher>())
        {
            FileEventsEnabled = false,
        };
        _watcher.Start();
    }

    private void Write(string text) => File.WriteAllText(_path, text, new UTF8Encoding(false));

    private List<ConfigurationResult> Results()
    {
        var results = new List<ConfigurationResult>();
        while (_inbox.Reader.TryRead(out var input))
        {
            if (input is ConfigurationInput configuration)
            {
                results.Add(configuration.Result);
            }
        }

        return results;
    }
}
