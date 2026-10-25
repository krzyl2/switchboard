using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Options;
using Switchboard.Application.Setup;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.Configuration;

/// <summary>Reads and validates the configuration document from disk; untrusted input, size-capped, strict UTF-8.</summary>
public sealed class ConfigurationFileReader(string path, int maxFileBytes, ConfigNode defaults)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string Path { get; } = path;

    /// <summary>The file's bytes, or the issue that prevents reading them; plus a fingerprint for change detection.</summary>
    public FileSnapshot ReadFile()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                return FileSnapshot.Problem(ConfigurationStatus.Missing, ConfigurationIssueCodes.FileMissing, $"{Path} does not exist.");
            }

            if (info.Length > maxFileBytes)
            {
                return FileSnapshot.Problem(ConfigurationStatus.Invalid, ConfigurationIssueCodes.FileTooLarge, $"{Path} is larger than {maxFileBytes} bytes.");
            }

            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[maxFileBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += read;
            }

            if (length > maxFileBytes)
            {
                return FileSnapshot.Problem(ConfigurationStatus.Invalid, ConfigurationIssueCodes.FileTooLarge, $"{Path} is larger than {maxFileBytes} bytes.");
            }

            var bytes = buffer.AsSpan(0, length).ToArray();
            return new FileSnapshot(bytes, null, ConfigurationStatus.Valid, Fingerprint(bytes));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileSnapshot.Problem(ConfigurationStatus.Missing, ConfigurationIssueCodes.FileMissing, $"{Path} does not exist.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return FileSnapshot.Problem(ConfigurationStatus.Invalid, ConfigurationIssueCodes.FileUnreadable, $"{Path} cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The snapshot <see cref="ReadFile"/> would take of a file holding exactly these bytes (the same
    /// size cap and fingerprint), so the wizard validates what it is about to write the way the
    /// running add-on will read it.
    /// </summary>
    public FileSnapshot Snapshot(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length > maxFileBytes
            ? FileSnapshot.Problem(ConfigurationStatus.Invalid, ConfigurationIssueCodes.FileTooLarge, $"{Path} would be larger than {maxFileBytes} bytes.")
            : new FileSnapshot(bytes, null, ConfigurationStatus.Valid, Fingerprint(bytes));
    }

    /// <summary>The content fingerprint the watcher compares, and the wizard's revision of the file.</summary>
    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public ConfigurationResult Parse(FileSnapshot file, DateTimeOffset loadedAt)
    {
        if (file.Issue is { } issue)
        {
            return ConfigurationResult.Failed(file.Status, [issue], Path, loadedAt);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(file.Bytes!);
        }
        catch (DecoderFallbackException)
        {
            return ConfigurationResult.Failed(
                ConfigurationStatus.Invalid,
                [new ConfigurationIssue(ConfigurationIssueCodes.FileUnreadable, string.Empty, null, null, $"{Path} is not UTF-8 text.")],
                Path,
                loadedAt);
        }

        var yaml = YamlDocumentReader.Read(text.TrimStart('﻿'), ConfigOrigin.Document);
        return yaml.Errors.IsEmpty
            ? SwitchboardDocumentParser.Parse(yaml.Root, defaults, Path, loadedAt)
            : ConfigurationResult.Failed(ConfigurationStatus.Invalid, yaml.Errors, Path, loadedAt);
    }

    public sealed record FileSnapshot(byte[]? Bytes, ConfigurationIssue? Issue, ConfigurationStatus Status, string Fingerprint)
    {
        public static FileSnapshot Problem(ConfigurationStatus status, string code, string message) =>
            new(null, new ConfigurationIssue(code, string.Empty, null, null, message), status, $"{status}:{code}");
    }
}

/// <summary>
/// Keeps the store's configuration in step with the file, without a restart. A content-hash poll on
/// the clock is the source of truth; file-system events only bring the check forward, because they
/// do not cross every container bind mount and are lost on overflow. Changes are applied once the
/// file has been quiet for the debounce, and only when the content actually changed. A broken edit
/// is applied too: it puts the store in the safe state and the log and UI say why.
/// </summary>
public sealed class ConfigurationFileWatcher : IDisposable, IConfigurationReloads
{
    private readonly ConfigurationFileReader _reader;
    private readonly SignalInbox _inbox;
    private readonly ConfigurationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigurationFileWatcher> _logger;
    private readonly object _gate = new();
    private ITimer? _poll;
    private ITimer? _debounce;
    private FileSystemWatcher? _watcher;
    private string? _fingerprint;
    private bool _debouncePending;
    private bool _disposed;

    public ConfigurationFileWatcher(
        ConfigurationFileReader reader,
        SignalInbox inbox,
        IOptions<ConfigurationOptions> options,
        TimeProvider timeProvider,
        ILogger<ConfigurationFileWatcher> logger)
    {
        _reader = reader;
        _inbox = inbox;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>Raised after every reload with the fingerprint of the file read and how it parsed; handlers must not block.</summary>
    public event EventHandler<ConfigurationReloaded>? Reloaded;

    /// <summary>File-system events as an accelerator of the poll; off only in tests that drive time themselves.</summary>
    internal bool FileEventsEnabled { get; init; } = true;

    /// <summary>Loads the document once, then keeps watching it.</summary>
    public void Start()
    {
        lock (_gate)
        {
            Reload(_reader.ReadFile());
            _debounce = _time.CreateTimer(_ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _poll = _time.CreateTimer(_ => OnPoll(), null, _options.PollInterval, _options.PollInterval);
            _watcher = FileEventsEnabled ? TryWatch() : null;
        }
    }

    public bool Watching
    {
        get
        {
            lock (_gate)
            {
                return _debounce is not null && !_disposed;
            }
        }
    }

    /// <summary>Asks for a check of the file now (after the debounce), as a file-system event would: the wizard calls it after a save.</summary>
    public void RequestCheck() => OnFileEvent();

    /// <summary>A file-system event for the document: check after the debounce. Internal for tests.</summary>
    internal void OnFileEvent()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debouncePending = true;
            _debounce?.Change(_options.ReloadDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _poll?.Dispose();
            _debounce?.Dispose();
        }
    }

    private void OnPoll()
    {
        lock (_gate)
        {
            if (_disposed || _debouncePending)
            {
                return;
            }

            // The poll only notices a change; the reload still waits for the file to be quiet.
            if (_reader.ReadFile().Fingerprint != _fingerprint)
            {
                _debouncePending = true;
                _debounce?.Change(_options.ReloadDebounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnDebounceElapsed()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debouncePending = false;
            var file = _reader.ReadFile();
            if (file.Fingerprint != _fingerprint)
            {
                Reload(file);
            }
        }
    }

    private void Reload(ConfigurationFileReader.FileSnapshot file)
    {
        _fingerprint = file.Fingerprint;
        var result = _reader.Parse(file, _time.GetUtcNow());
        Log(result);
        _inbox.Post(result);
        try
        {
            Reloaded?.Invoke(this, new ConfigurationReloaded(file.Fingerprint, result.Status));
        }
        catch (Exception ex)
        {
            // A listener (the wizard waiting for its save) never stops the reload itself.
            _logger.LogWarning("A configuration reload listener failed: {Reason}", ex.Message);
        }
    }

    private void Log(ConfigurationResult result)
    {
        switch (result.Status)
        {
            case ConfigurationStatus.Valid:
                _logger.LogInformation(
                    "Configuration {Path} loaded: {Signals} signals, {Topology}",
                    result.Path,
                    result.Configuration!.Signals.Length,
                    result.Configuration.Connection.Topology);
                break;
            case ConfigurationStatus.Missing:
                _logger.LogWarning(
                    "Configuration {Path} not found; no protection data is valid until it exists. See the add-on documentation, section Configuration.",
                    result.Path);
                break;
            default:
                _logger.LogError(
                    "Configuration {Path} is invalid ({Count} problems); no protection data is valid until it is fixed",
                    result.Path,
                    result.Errors.Length);
                foreach (var error in result.Errors)
                {
                    _logger.LogError("Configuration: {Issue}", error);
                }

                break;
        }

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("Configuration: {Issue}", warning);
        }
    }

    private FileSystemWatcher? TryWatch()
    {
        try
        {
            var full = Path.GetFullPath(_reader.Path);
            var directory = Path.GetDirectoryName(full);
            if (directory is null || !Directory.Exists(directory))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(directory, Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, _) => OnFileEvent();
            watcher.Created += (_, _) => OnFileEvent();
            watcher.Deleted += (_, _) => OnFileEvent();
            watcher.Renamed += (_, _) => OnFileEvent();
            watcher.Error += (_, e) => _logger.LogDebug("Configuration file watcher error ({Reason}); the poll still checks the file", e.GetException().Message);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Configuration file events unavailable ({Reason}); the poll checks the file", ex.Message);
            return null;
        }
    }
}
