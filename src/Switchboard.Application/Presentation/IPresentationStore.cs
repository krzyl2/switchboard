using Switchboard.Application.State;

namespace Switchboard.Application.Presentation;

/// <summary>What a presentation file held at start, and how reading it went (loaded, fresh, or set aside as corrupt).</summary>
/// <param name="State">null when the file was absent or set aside.</param>
public sealed record PresentationLoad<T>(T? State, StateFileStatus Status, string? Warning)
    where T : class;

/// <summary>
/// A presentation file in the data folder (statistics.json, excursions.json): untrusted on read,
/// written atomically, bounded in size. Implemented in Infrastructure; called only from the writer
/// loop, never from the guard's thread.
/// </summary>
public interface IPresentationStore<T>
    where T : class
{
    PresentationLoad<T> Load();

    /// <summary>Writes the state, trimmed to the size cap if needed; throws <see cref="IOException"/> when the disk refuses.</summary>
    void Save(T state);
}
