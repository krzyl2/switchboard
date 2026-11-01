using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;
using Switchboard.Application.State;

namespace Switchboard.Application.Setup.SelfTest;

/// <summary>The phase evidence as /data/phase-evidence.json holds it.</summary>
public sealed record PhaseEvidenceState(DateTimeOffset SavedAt, ImmutableDictionary<EntityId, ImmutableArray<PhaseObservation>> Observations);

/// <param name="State">null when the file was absent or set aside.</param>
public sealed record PhaseEvidenceLoad(PhaseEvidenceState? State, StateFileStatus Status, string? Warning);

/// <summary>
/// The evidence file in the data folder: untrusted on read (a corrupt file is set aside and the
/// store starts empty), written atomically, bounded in size. Implemented in Infrastructure; called
/// only from the writer loop, never from the guard's thread.
/// </summary>
public interface IPhaseEvidenceFile
{
    PhaseEvidenceLoad Load();

    /// <summary>Writes the evidence, trimmed to the size cap if needed; throws <see cref="IOException"/> when the disk refuses.</summary>
    void Save(PhaseEvidenceState state);
}

/// <summary>
/// Keeps the phase evidence (passive and active) across restarts (maintainer survey 2026-10-25):
/// loaded before the first step, written from its own loop whenever an observation was added (the
/// store's change only wakes the loop: no file I/O on the guard thread, so a slow or full /data never
/// delays a protection step), and once more at shutdown. A failed write is logged and retried at the
/// next change.
/// </summary>
public sealed class PhaseEvidencePersistence
{
    private readonly PhaseEvidenceStore _store;
    private readonly IPhaseEvidenceFile _file;
    private readonly TimeProvider _signalTime;
    private readonly ILogger<PhaseEvidencePersistence> _logger;
    private readonly LatestStateWriter<PhaseEvidenceState> _writer;
    private long _changes;
    private long _saved;

    public PhaseEvidencePersistence(PhaseEvidenceStore store, IPhaseEvidenceFile file, SignalClock clock, ILogger<PhaseEvidencePersistence> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _file = file;
        _writer = new LatestStateWriter<PhaseEvidenceState>("phase-evidence.json", file.Save, "the evidence is kept in memory and written with the next change", logger);
        _signalTime = clock.Time;
        _logger = logger;
        _store.Changed += (_, _) =>
        {
            Interlocked.Increment(ref _changes);
            _writer.Wake();
        };
    }

    /// <summary>Reads the file into the store. Call once, before the guard's first step; a failure starts empty, never stops the start.</summary>
    public void Load()
    {
        try
        {
            var loaded = _file.Load();
            if (loaded.State is { } state)
            {
                _store.Load(state.Observations);
            }

            if (loaded.Warning is { } warning)
            {
                _logger.LogWarning("{Warning}", warning);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "The phase evidence could not be loaded; the self-test starts without it");
        }

        _saved = Interlocked.Read(ref _changes);
    }

    public Task RunAsync(CancellationToken cancellationToken) =>
        _writer.RunAsync(FlushAsync, retry: null, cancellationToken);

    /// <summary>Writes the store if it changed since the last write.</summary>
    public Task FlushAsync(CancellationToken cancellationToken) =>
        _writer.WriteAsync(NextChange, saved: null, cancellationToken);

    private (PhaseEvidenceState State, Action Commit)? NextChange()
    {
        var changes = Interlocked.Read(ref _changes);
        return changes == _saved
            ? null
            : (new PhaseEvidenceState(_signalTime.GetUtcNow(), _store.Snapshot()), () => _saved = changes);
    }
}
