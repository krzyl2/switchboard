using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Dispatch;
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

/// <summary>
/// The guard runner's one read per step: protection inputs, dispatch inputs and the dispatch setup
/// of the same store cycle, so the guard and dispatch never see two different moments (no torn
/// cycle). Protection reads only <see cref="Protection"/>; dispatch reads only
/// <see cref="Dispatch"/> and <see cref="DispatchSetup"/> plus the guard's envelope (SIG-04).
/// </summary>
public sealed record ControlInputs(ProtectionInputs Protection, DispatchInputs Dispatch, DispatchSetup? DispatchSetup)
{
    /// <summary>
    /// What only the presentation reads (nominal voltage, how generation was obtained, display
    /// names), from the same store cycle. The guard, dispatch and the effector gate never read it.
    /// </summary>
    public PresentationInputs? Presentation { get; init; }
}

/// <summary>The control port: both input views of one store cycle. The guard runner depends on this and on nothing wider.</summary>
public interface IControlInputSource
{
    ControlInputs Current { get; }

    event EventHandler<ControlInputs>? Published;
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
public sealed class SignalSnapshotHolder : IProtectionInputSource, IDispatchInputSource, ISignalDiagnosticsSource, IControlInputSource
{
    private SignalDiagnostics _current;
    private ControlInputs _control;

    public SignalSnapshotHolder(SignalDiagnostics initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
        _control = Control(initial);
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

    event EventHandler<ControlInputs>? IControlInputSource.Published
    {
        add => ControlPublished += value;
        remove => ControlPublished -= value;
    }

    private event EventHandler<DispatchInputs>? DispatchPublished;

    private event EventHandler<ControlInputs>? ControlPublished;

    public SignalDiagnostics Current => Volatile.Read(ref _current);

    ProtectionInputs IProtectionInputSource.Current => Current.Snapshot.Protection;

    DispatchInputs IDispatchInputSource.Current => Current.Snapshot.Dispatch;

    ControlInputs IControlInputSource.Current => Volatile.Read(ref _control);

    public void Publish(SignalDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var control = Control(diagnostics);

        // The control view first: whoever reads the diagnostics after this sees the same cycle.
        Volatile.Write(ref _control, control);
        Volatile.Write(ref _current, diagnostics);
        ProtectionPublished?.Invoke(this, diagnostics.Snapshot.Protection);
        DispatchPublished?.Invoke(this, diagnostics.Snapshot.Dispatch);
        ControlPublished?.Invoke(this, control);
    }

    private static ControlInputs Control(SignalDiagnostics diagnostics) =>
        new(diagnostics.Snapshot.Protection, diagnostics.Snapshot.Dispatch, diagnostics.Snapshot.DispatchSetup) { Presentation = diagnostics.Snapshot.Presentation };
}
