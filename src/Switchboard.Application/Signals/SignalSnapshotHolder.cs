using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>The protection port (SIG-04): currents only. The guard depends on this and on nothing wider.</summary>
public interface IProtectionInputSource
{
    ProtectionInputs Current { get; }

    event EventHandler<ProtectionInputs>? Published;
}

/// <summary>The dispatch port (SIG-04): power and voltage only.</summary>
public interface IDispatchInputSource
{
    DispatchInputs Current { get; }

    event EventHandler<DispatchInputs>? Published;
}

/// <summary>The full picture, for the diagnostics view only.</summary>
public interface ISignalDiagnosticsSource
{
    SignalDiagnostics Current { get; }
}

/// <summary>
/// One store cycle as diagnostics shows it: the snapshot plus what only the mapping layer knows
/// (entity IDs, the configuration result). Published atomically with the snapshot.
/// </summary>
/// <param name="Configuration">The configuration result applied, or null before the first read.</param>
/// <param name="Entities">Entity ID per signal; the only place outside the store that joins the two.</param>
/// <param name="RuntimeWarnings">Configuration warnings that only show at run time (max_age too short for the detected refresh).</param>
public sealed record SignalDiagnostics(
    SignalSnapshot Snapshot,
    ConfigurationResult? Configuration,
    ImmutableDictionary<SignalName, EntityId> Entities,
    ImmutableArray<ConfigurationIssue> RuntimeWarnings);

/// <summary>Holds the latest published cycle; written by the runner, read by anyone without locking.</summary>
public sealed class SignalSnapshotHolder : IProtectionInputSource, IDispatchInputSource, ISignalDiagnosticsSource
{
    private SignalDiagnostics _current;

    public SignalSnapshotHolder(SignalDiagnostics initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    event EventHandler<ProtectionInputs>? IProtectionInputSource.Published
    {
        add => ProtectionPublished += value;
        remove => ProtectionPublished -= value;
    }

    event EventHandler<DispatchInputs>? IDispatchInputSource.Published
    {
        add => DispatchPublished += value;
        remove => DispatchPublished -= value;
    }

    private event EventHandler<ProtectionInputs>? ProtectionPublished;

    private event EventHandler<DispatchInputs>? DispatchPublished;

    public SignalDiagnostics Current => Volatile.Read(ref _current);

    ProtectionInputs IProtectionInputSource.Current => Current.Snapshot.Protection;

    DispatchInputs IDispatchInputSource.Current => Current.Snapshot.Dispatch;

    public void Publish(SignalDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Volatile.Write(ref _current, diagnostics);
        ProtectionPublished?.Invoke(this, diagnostics.Snapshot.Protection);
        DispatchPublished?.Invoke(this, diagnostics.Snapshot.Dispatch);
    }
}
