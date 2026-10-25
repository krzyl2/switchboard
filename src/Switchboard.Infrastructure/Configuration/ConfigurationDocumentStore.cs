using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;

namespace Switchboard.Infrastructure.Configuration;

/// <summary>
/// switchboard.yaml as the wizard writes it (UI-06). A write goes to a temporary file in the same
/// directory, is flushed to disk, the previous file is copied to a timestamped backup next to it,
/// and only then does the new file replace the old one with a rename, so the running add-on (and a
/// crash) only ever sees a whole file. The base revision is checked under one lock: a file changed
/// since the wizard read it is never overwritten. Nothing is written outside the file's directory,
/// and only backups with the wizard's exact name pattern are ever pruned.
/// </summary>
public sealed partial class ConfigurationDocumentStore : ISetupDocumentStore
{
    private const string BackupExtension = ".bak";
    private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ConfigurationFileReader _reader;
    private readonly int _maxBackups;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigurationDocumentStore> _logger;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _fileName;

    public ConfigurationDocumentStore(ConfigurationFileReader reader, int maxBackups, TimeProvider time, ILogger<ConfigurationDocumentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _maxBackups = maxBackups;
        _time = time;
        _logger = logger;
        var full = Path.GetFullPath(reader.Path);
        _directory = Path.GetDirectoryName(full) ?? throw new ArgumentException("The configuration path has no directory.", nameof(reader));
        _fileName = Path.GetFileName(full);
    }

    public string FullPath => Path.Combine(_directory, _fileName);

    public DocumentRead Read()
    {
        lock (_gate)
        {
            return ReadLocked();
        }
    }

    public SaveOutcome Write(string text, string? baseRevision)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            var current = ReadLocked();
            if (!string.Equals(current.Revision, baseRevision, StringComparison.Ordinal))
            {
                return new SaveOutcome.Conflict(current.Revision);
            }

            var bytes = StrictUtf8.GetBytes(text);
            Directory.CreateDirectory(_directory);
            var temporary = Path.Combine(_directory, $".{_fileName}.{Guid.NewGuid():N}.tmp");
            string? backup = null;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (current.Exists)
                {
                    backup = Backup();
                }

                File.Move(temporary, FullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            Prune(backup);
            var revision = ConfigurationFileReader.Fingerprint(bytes);
            _logger.LogInformation(
                "Configuration {Path} written by the setup wizard{Backup}",
                FullPath,
                backup is null ? string.Empty : $"; previous version kept as {backup}");
            return new SaveOutcome.Saved(revision, backup);
        }
    }

    /// <summary>True for a backup name this store creates (switchboard.yaml.20261025T101500123Z.bak, optionally -n before .bak).</summary>
    public bool IsBackupName(string name) =>
        name.StartsWith(_fileName + ".", StringComparison.Ordinal) && BackupSuffix().IsMatch(name[(_fileName.Length + 1)..]);

    private DocumentRead ReadLocked()
    {
        var file = _reader.ReadFile();
        if (file.Status == ConfigurationStatus.Missing)
        {
            return new DocumentRead(false, null, null, null);
        }

        if (file.Issue is { } issue)
        {
            return new DocumentRead(true, null, file.Fingerprint, issue.Message);
        }

        try
        {
            return new DocumentRead(true, StrictUtf8.GetString(file.Bytes!), file.Fingerprint, null);
        }
        catch (DecoderFallbackException)
        {
            return new DocumentRead(true, null, file.Fingerprint, $"{FullPath} is not UTF-8 text.");
        }
    }

    /// <summary>
    /// Copies the current file to a backup name that does not exist yet (two saves in one millisecond
    /// get a counter) and flushes it to disk before the new file replaces the original, so a power loss
    /// right after a save never leaves the rename without the backup it promised.
    /// </summary>
    private string Backup()
    {
        var stamp = _time.GetUtcNow().UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        for (var n = 0; ; n++)
        {
            var name = n == 0 ? $"{_fileName}.{stamp}{BackupExtension}" : string.Create(CultureInfo.InvariantCulture, $"{_fileName}.{stamp}-{n}{BackupExtension}");
            var path = Path.Combine(_directory, name);
            if (File.Exists(path))
            {
                continue;
            }

            try
            {
                using (var source = new FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(target);
                    target.Flush(flushToDisk: true);
                }

                return name;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Created between the check and the copy; take the next counter.
            }
        }
    }

    /// <summary>
    /// Deletes the oldest backups (by their stamp) beyond MaxBackups. The backup this save made is
    /// never a candidate: with the host clock behind earlier stamps (a boot before NTP) it would sort
    /// first and be deleted in the same write.
    /// </summary>
    private void Prune(string? made)
    {
        try
        {
            var backups = Directory.GetFiles(_directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(IsBackupName)
                .ToList();
            var candidates = backups.Where(b => !string.Equals(b, made, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            foreach (var old in candidates.Take(Math.Max(0, backups.Count - _maxBackups)))
            {
                File.Delete(Path.Combine(_directory, old));
                _logger.LogInformation("Deleted configuration backup {Backup} (more than {Count} kept)", old, _maxBackups);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The save itself succeeded; an extra backup is harmless.
            _logger.LogWarning("Old configuration backups could not be pruned: {Reason}", ex.Message);
        }
    }

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{9}Z(-[0-9]{1,6})?\.bak$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupSuffix();
}
