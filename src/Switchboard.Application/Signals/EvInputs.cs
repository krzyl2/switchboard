using Switchboard.Application.Configuration;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// How one EV input was read (EV-02, docs/EV.md B7): whether the section declares it, its state and detail, when Home Assistant last reported it
/// (<see cref="ReadAt"/>, the freshness basis: <c>last_reported</c>) and the age limit it is judged by.
/// </summary>
public sealed record EvRead(bool Configured, SignalState State, SignalDetail Detail, DateTimeOffset? ReadAt, TimeSpan? MaxAge)
{
    public static EvRead NotConfigured { get; } = new(false, SignalState.Unmapped, SignalDetail.None, null, null);

    /// <summary>Declared and fresh: its value may be used.</summary>
    public bool IsFresh => Configured && State == SignalState.Fresh;
}

/// <summary>
/// The EV module's inputs of one store cycle, typed and with their read times: the SoC, the electric range, whether the car is plugged in, and the
/// calendar's event. A value is present only while its signal is fresh (a stale SoC is never the last SoC), as for the forecast.
/// This is the module's view of the signal layer and nothing else: it is not <see cref="GoalInputs"/> (which stays at four roles), and no guard, dispatch
/// or protection type holds it, by structure (<c>EvArchitectureTests</c>).
/// </summary>
/// <param name="SocPercent">The state of charge, percent; null unless fresh.</param>
/// <param name="RangeKm">The electric range in km (a mi sensor was converted in the signal layer); null unless fresh.</param>
/// <param name="IsPlugged">Whether the plugged signal's state is one of the configured plugged states; null unless fresh (an unavailable charger is not "not plugged").</param>
/// <param name="PluggedState">The plugged signal's state as Home Assistant wrote it, for display; null unless fresh.</param>
/// <param name="CalendarEvent">The calendar's current or next event; null when none or unless fresh. <see cref="Calendar"/> says which.</param>
/// <param name="CalendarZoneName">The zone the calendar entity says its times are in, when it says so; null otherwise.</param>
public sealed record EvInputs(
    EvRead Soc,
    double? SocPercent,
    EvRead Range,
    double? RangeKm,
    EvRead Plugged,
    bool? IsPlugged,
    string? PluggedState,
    EvRead Calendar,
    CalendarEvent? CalendarEvent,
    string? CalendarZoneName)
{
    public static EvInputs Empty { get; } = new(EvRead.NotConfigured, null, EvRead.NotConfigured, null, EvRead.NotConfigured, null, null, EvRead.NotConfigured, null, null);

    /// <summary>The roles this view reads, and no others.</summary>
    public static IReadOnlyList<SignalRole> Roles { get; } = [SignalRole.EvSoc, SignalRole.EvRange, SignalRole.EvPlugged, SignalRole.EvCalendar];

    /// <summary>Takes the EV signals the section declares out of a snapshot.</summary>
    public static EvInputs From(SignalSnapshot snapshot, EvDefinition ev)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ev);

        var soc = Find(snapshot, ev.Soc, SignalRole.EvSoc);
        var range = Find(snapshot, ev.Range, SignalRole.EvRange);
        var plugged = Find(snapshot, ev.Plugged, SignalRole.EvPlugged);
        var calendar = Find(snapshot, ev.Calendar, SignalRole.EvCalendar);

        var pluggedState = plugged is { State: SignalState.Fresh, Value: StateValue state } ? state.State : null;
        var calendarValue = calendar is { State: SignalState.Fresh, Value: CalendarEventValue value } ? value : null;
        return new EvInputs(
            Read(soc),
            soc is { State: SignalState.Fresh, Value: PercentageValue percentage } ? percentage.Percentage.Value : null,
            Read(range),
            range is { State: SignalState.Fresh, Value: DistanceValue distance } ? distance.Kilometers : null,
            Read(plugged),
            pluggedState is null ? null : ev.PluggedStates.Contains(pluggedState.Trim(), StringComparer.OrdinalIgnoreCase),
            pluggedState,
            Read(calendar),
            calendarValue?.Event,
            calendarValue?.ZoneName);
    }

    /// <summary>A disconnected feed means nothing is known now: an on-change state would otherwise stay Fresh for ever after a lost link, and a plugged car would be read from a state that may have changed.</summary>
    private static SignalReading? Find(SignalSnapshot snapshot, SignalName? name, SignalRole role)
    {
        if (name is not { } wanted || snapshot.Signals.FirstOrDefault(s => s.Name == wanted && s.Role == role) is not { } reading)
        {
            return null;
        }

        return snapshot.Source == SignalSourceStatus.Connected ? reading : reading with { State = SignalState.Unavailable, Detail = SignalDetail.SourceDisconnected, Value = null };
    }

    private static EvRead Read(SignalReading? reading) =>
        reading is null ? EvRead.NotConfigured : new EvRead(true, reading.State, reading.Detail, reading.LastReported, reading.MaxAge);
}

/// <summary>The configuration in force and the EV inputs of the latest store cycle; the inputs are empty without an <c>ev:</c> section.</summary>
public sealed record EvInputView(ConfigurationResult? Configuration, EvInputs Inputs);

/// <summary>Where the EV module reads its inputs from; the diagnostics-backed implementation is the only one.</summary>
public interface IEvInputSource
{
    EvInputView Current { get; }
}

/// <summary>The latest published store cycle, reduced to what the EV module may see.</summary>
public sealed class DiagnosticsEvInputSource(ISignalDiagnosticsSource diagnostics) : IEvInputSource
{
    public EvInputView Current
    {
        get
        {
            var current = diagnostics.Current;
            return new EvInputView(
                current.Configuration,
                current.Configuration?.Configuration?.Ev is { } ev ? EvInputs.From(current.Snapshot, ev) : EvInputs.Empty);
        }
    }
}
