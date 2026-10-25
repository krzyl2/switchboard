using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Setup;

/// <summary>
/// The configuration file as the wizard sees it. <see cref="Revision"/> is the file's content
/// fingerprint (the one the reload watcher compares), null while the file does not exist.
/// </summary>
/// <param name="Text">The file as text; null when it is missing or cannot be read as UTF-8 text.</param>
/// <param name="Problem">Why an existing file cannot be read (too large, unreadable, not UTF-8).</param>
public sealed record DocumentRead(bool Exists, string? Text, string? Revision, string? Problem);

/// <summary>The outcome of a write.</summary>
public abstract record SaveOutcome
{
    /// <param name="Backup">The file name of the backup of the previous version; null on a clean install.</param>
    public sealed record Saved(string Revision, string? Backup) : SaveOutcome;

    /// <summary>The file changed since the wizard read it (or appeared on a clean install): nothing was written.</summary>
    public sealed record Conflict(string? CurrentRevision) : SaveOutcome;
}

/// <summary>
/// Where switchboard.yaml lives. Writes are atomic (the old file stays whole until the new one
/// replaces it), keep a bounded set of timestamped backups next to it, never touch anything outside
/// the configuration directory, and refuse when the file is not at <paramref name="baseRevision"/>.
/// </summary>
public interface ISetupDocumentStore
{
    DocumentRead Read();

    /// <param name="baseRevision">The revision the draft was edited from; null means the file must still be missing.</param>
    SaveOutcome Write(string text, string? baseRevision);
}

/// <summary>The YAML side of the wizard, kept in Infrastructure with the YAML library.</summary>
public interface ISetupDocumentCodec
{
    /// <summary>The tree of a YAML document, or null with the reader's issues when the text is not one usable document.</summary>
    (ConfigNode? Root, ImmutableArray<ConfigurationIssue> Errors) ReadTree(string text);

    /// <summary>The tree as the wizard writes it, with its fixed header.</summary>
    string Write(ConfigNode root);

    /// <summary>True when the text holds comments other than the wizard's own header (a structured save drops them).</summary>
    bool HasComments(string text);

    /// <summary>The exact bytes parsed the way the running add-on reads its file: size cap, strict UTF-8, YAML rules, schema.</summary>
    ConfigurationResult Parse(byte[] bytes, DateTimeOffset now);

    /// <summary>The control parameters of a document (the shipped defaults merged with its <c>control:</c>); null when that section is invalid.</summary>
    ControlParameters? Control(ConfigNode? root);
}

/// <summary>A reload of the configuration file by the running add-on.</summary>
public sealed record ConfigurationReloaded(string Fingerprint, ConfigurationStatus Status);

/// <summary>The running add-on's configuration watcher, as the wizard needs it after a save.</summary>
public interface IConfigurationReloads
{
    /// <summary>The watcher runs: a requested check will be answered by <see cref="Reloaded"/>.</summary>
    bool Watching { get; }

    void RequestCheck();

    event EventHandler<ConfigurationReloaded>? Reloaded;
}

/// <summary>
/// What the wizard reads of the running add-on: the configuration in force and the refresh the
/// signal store detected per mapped entity. Narrow on purpose: the wizard never sees the full
/// signal snapshot (only the signal layer and diagnostics do).
/// </summary>
public interface ISetupRuntimeView
{
    /// <summary>The valid configuration in force; null without one.</summary>
    SwitchboardConfiguration? Configuration { get; }

    /// <summary>The refresh detected for each mapped entity that has one (SIG-07).</summary>
    IReadOnlyDictionary<EntityId, TimeSpan> DetectedRefresh { get; }

    /// <summary>
    /// The devices Switchboard holds below the level it reduced them from by a command that was sent
    /// (a commanded restore baseline, the leftReduced list of api/control); empty when none or unknown.
    /// </summary>
    IReadOnlyCollection<DeviceName> ReducedBySwitchboard => [];
}
