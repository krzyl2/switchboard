using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup.SelfTest;

public enum SelfTestStatus
{
    Watching,

    /// <summary>The switchings seen agree on a verdict: confirmed or mismatch (a switch on and a switch off, at least min_concordant).</summary>
    Finished,

    /// <summary>The watch timeout passed without a verdict (every switching inside it was judged first).</summary>
    Expired,

    Cancelled,
}

/// <summary>
/// What a guided watch looks for: the switched entity, the phase and rating the draft declares, and
/// the draft's meter terms per phase (phase power when every phase has one, else phase current).
/// </summary>
/// <param name="Unit">W or A: the unit of <paramref name="ExpectedStep"/> and of the meter terms.</param>
/// <param name="Unsigned">The meter terms are unsigned currents (magnitudes).</param>
public sealed record SelfTestRequest(
    EntityId StateEntity,
    Phase ExpectedPhase,
    double ExpectedStep,
    string Unit,
    bool Unsigned,
    ImmutableDictionary<Phase, MeterTerm> Meter,
    double Sigma)
{
    /// <summary>The draft's other on/off devices: one of them switching inside the window makes the observation not isolated.</summary>
    public ImmutableArray<EntityId> OtherSwitches { get; init; } = [];

    /// <summary>The draft's draw sensors: a draw changing by more than its threshold inside the window makes the observation not isolated.</summary>
    public ImmutableArray<DrawTerm> OtherDraws { get; init; } = [];
}

/// <summary>A draw sensor the watch keeps an eye on, as the recorder does (PhaseEvidenceRecorder).</summary>
/// <param name="Threshold">T_quiet of the watched device in the draw's own base unit (A or W): a smaller change is left to the per-phase checks.</param>
public sealed record DrawTerm(EntityId Entity, string Unit, double Threshold);

/// <summary>What else in a draft could make a guided watch's step ambiguous: the recorder's isolation inputs.</summary>
public static class SelfTestIsolation
{
    /// <param name="watchedIndex">The watched device's index in the draft (its own draw is expected to change); null for a test load.</param>
    /// <param name="ratedCurrentA">The watched device's rating in A: T_quiet of a current draw.</param>
    /// <param name="ratedPowerW">The watched device's rating in W: T_quiet of a power draw.</param>
    public static (ImmutableArray<EntityId> Switches, ImmutableArray<DrawTerm> Draws) Of(
        SetupDraft draft,
        int? watchedIndex,
        EntityId watched,
        double ratedCurrentA,
        double ratedPowerW,
        ControlParameters control,
        PhaseStepParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(parameters);
        double Quiet(double rating, SignalKind kind) =>
            Math.Max(parameters.QuietMaxFraction * rating, parameters.SigmaMultiplier * control.Signals.DefaultSigma.GetValueOrDefault(kind));

        var switches = new List<EntityId>();
        var draws = new List<DrawTerm>();
        for (var i = 0; i < draft.Devices.Count; i++)
        {
            var device = draft.Devices[i];
            if (i == watchedIndex)
            {
                continue;
            }

            if (EntityId.TryParse(device.StateEntity ?? device.Actuator, out var state) && state.Value != watched)
            {
                switches.Add(state.Value);
            }

            if (device.Draw is not { } draw)
            {
                continue;
            }

            if (EntityId.TryParse(draw.Current, out var amps))
            {
                draws.Add(new DrawTerm(amps.Value, draw.CurrentUnit ?? DeviceSectionParser.DefaultUnit, Quiet(ratedCurrentA, SignalKind.Current)));
            }

            if (EntityId.TryParse(draw.Power, out var watts))
            {
                draws.Add(new DrawTerm(watts.Value, draw.PowerUnit ?? DeviceSectionParser.DefaultPowerUnit, Quiet(ratedPowerW, SignalKind.Power)));
            }
        }

        return ([.. switches.Distinct()], [.. draws.DistinctBy(d => d.Entity)]);
    }
}

public sealed record SelfTestView(
    Guid Id,
    SelfTestStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    SelfTestRequest Request,
    ImmutableArray<JudgedObservation> Observations,
    PhaseStepSummary Summary);

/// <summary>Starting a watch failed because one is already running.</summary>
public sealed class SelfTestBusyException(Guid running) : InvalidOperationException("A phase self-test is already watching.")
{
    public Guid Running { get; } = running;
}

/// <summary>
/// The guided, passive phase self-test (CONN-05). Switchboard switches nothing: the user switches
/// one device on and off in Home Assistant or by hand while this watches the meter. A session holds
/// its own readout subscription for its whole timeout, independent of the page polling (the user is
/// in another tab switching the device), and judges on its own timer: each switching of the watched
/// entity is classified as soon as its window has passed (before the bounded report history moves
/// on) and stored as raw evidence. Another declared device switching, or a draw changing, inside the
/// window makes it not isolated, as in the recorder. The session finishes once its observations
/// agree on a verdict (see PhaseStepClassifier.Summarize); one session runs at a time. Everything
/// runs on the signal clock.
/// </summary>
public sealed class PhaseSelfTestService : IDisposable
{
    private readonly ICandidateReadouts _readouts;
    private readonly PhaseEvidenceStore _evidence;
    private readonly SetupDefaults _defaults;
    private readonly Func<int> _lagPolls;
    private readonly TimeProvider _time;
    private readonly ILogger<PhaseSelfTestService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = [];

    public PhaseSelfTestService(
        ICandidateReadouts readouts,
        PhaseEvidenceStore evidence,
        SetupDefaults defaults,
        Func<int> lagPolls,
        SignalClock clock,
        ILogger<PhaseSelfTestService> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _readouts = readouts;
        _evidence = evidence;
        _defaults = defaults;
        _lagPolls = lagPolls;
        _time = clock.Time;
        _logger = logger;
    }

    /// <exception cref="SelfTestBusyException">Another session is still watching.</exception>
    public SelfTestView Start(SelfTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_sessions.Values.FirstOrDefault(s => s.Status == SelfTestStatus.Watching) is { } running)
            {
                throw new SelfTestBusyException(running.Id);
            }

            var now = _time.GetUtcNow();
            var entities = request.Meter.Values.Select(t => t.Entity)
                .Append(request.StateEntity)
                .Concat(request.OtherSwitches)
                .Concat(request.OtherDraws.Select(d => d.Entity))
                .Distinct()
                .ToList();
            var session = new Session(Guid.NewGuid(), request, now, now + _defaults.SelfTest.WatchTimeout, _readouts.Hold(entities));
            session.Timer = _time.CreateTimer(_ => Tick(session.Id), null, _defaults.SelfTest.Isolation, Timeout.InfiniteTimeSpan);
            session.Expiry = _time.CreateTimer(_ => Tick(session.Id), null, _defaults.SelfTest.WatchTimeout, Timeout.InfiniteTimeSpan);
            _sessions[session.Id] = session;

            // Finished and expired sessions stay readable until the next one starts.
            foreach (var old in _sessions.Values.Where(s => s.Id != session.Id).ToList())
            {
                _sessions.Remove(old.Id);
            }

            _logger.LogInformation(
                "Phase self-test watching {Entity} (declared {Phase}) for {Timeout}; Switchboard switches nothing, the user does",
                request.StateEntity,
                request.ExpectedPhase,
                _defaults.SelfTest.WatchTimeout);
            return View(session);
        }
    }

    public SelfTestView? Get(Guid id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session))
            {
                return null;
            }

            Evaluate(session, _time.GetUtcNow());
            return View(session);
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session))
            {
                return false;
            }

            if (session.Status == SelfTestStatus.Watching)
            {
                session.Status = SelfTestStatus.Cancelled;
                session.Release();
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                session.Release();
            }

            _sessions.Clear();
        }
    }

    /// <summary>The session's own timer: judges what is due, then wakes again when the next window passes (or after the isolation time, to see new switchings).</summary>
    private void Tick(Guid id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Status != SelfTestStatus.Watching)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var next = Evaluate(session, now);
            if (session.Status == SelfTestStatus.Watching)
            {
                var due = next is { } at && at - now < _defaults.SelfTest.Isolation ? at - now : _defaults.SelfTest.Isolation;
                session.Timer?.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Classifies every switching whose window has passed; ends the session once its observations
    /// agree on a verdict, or at its timeout when no switching inside it still waits for its window.
    /// Returns when the next waiting switching becomes classifiable.
    /// </summary>
    private DateTimeOffset? Evaluate(Session session, DateTimeOffset now)
    {
        if (session.Status != SelfTestStatus.Watching)
        {
            return null;
        }

        var request = session.Request;
        var lag = _lagPolls();
        var refresh = _readouts.Peek(request.Meter.GetValueOrDefault(request.ExpectedPhase)?.Entity ?? request.StateEntity)?.Refresh;
        var window = StepWindow.Window(_defaults.SelfTest.Isolation, lag, refresh);
        var transitions = Transitions(_readouts.History(request.StateEntity), session.StartedAt);
        DateTimeOffset? next = null;
        foreach (var (at, direction) in transitions.Where(t => !session.Seen.Contains(t.At) && t.At <= session.ExpiresAt))
        {
            // Without a detected refresh the window is unknown: after the isolation time the switching
            // is recorded as inconclusive, never guessed.
            var due = at + (window ?? _defaults.SelfTest.Isolation);
            if (now < due)
            {
                next = next is { } earlier && earlier < due ? earlier : due;
                continue;
            }

            PhaseObservation observation;
            if (window is not { } span)
            {
                observation = new PhaseObservation(at, direction, request.StateEntity, ObservationSource.Watch, request.Unit, request.Unsigned, false, request.Meter, ImmutableDictionary<Phase, PhaseStepMeasurement>.Empty, true, "refresh_not_detected");
            }
            else
            {
                var phases = request.Meter.ToImmutableDictionary(
                    t => t.Key,
                    t => StepWindow.Measure(Series(t.Value), at, span, lag, request.Sigma));
                observation = new PhaseObservation(at, direction, request.StateEntity, ObservationSource.Watch, request.Unit, request.Unsigned, false, request.Meter, phases, Isolated(request, transitions, at, span), null);
            }

            session.Seen.Add(at);
            _evidence.Add(observation);
            var result = PhaseStepClassifier.Classify(
                new PhaseStepInput(request.ExpectedPhase, direction, request.ExpectedStep, observation.Phases, observation.Isolated, request.Unsigned),
                _defaults.SelfTest.Classifier);
            session.Observations.Add(new JudgedObservation(observation, result));
        }

        var summary = PhaseStepClassifier.Summarize([.. session.Observations.Select(o => o.Result)], _defaults.SelfTest.Classifier);
        if (summary.Verdict != PhaseStepVerdict.Inconclusive)
        {
            session.Status = SelfTestStatus.Finished;
        }
        else if (now >= session.ExpiresAt && next is null)
        {
            session.Status = SelfTestStatus.Expired;
        }

        if (session.Status != SelfTestStatus.Watching)
        {
            session.Release();
            return null;
        }

        return next;
    }

    /// <summary>
    /// No other switching of the watched entity, no other declared device switching and no draw
    /// changing by more than its threshold within the window: the recorder's rule (PhaseEvidenceRecorder).
    /// </summary>
    private bool Isolated(SelfTestRequest request, List<(DateTimeOffset At, int Direction)> transitions, DateTimeOffset at, TimeSpan span) =>
        !transitions.Any(o => o.At != at && (o.At - at).Duration() < span)
        && !request.OtherSwitches.Any(e => Transitions(_readouts.History(e), DateTimeOffset.MinValue).Any(o => o.At > at - span && o.At <= at + span))
        && !request.OtherDraws.Any(d => StepWindow.Changed(Series(new MeterTerm(d.Entity, d.Unit, MeterValues.SignName(null))), at, span, d.Threshold));

    private IReadOnlyList<ReportPoint> Series(MeterTerm term) =>
        [.. _readouts.History(term.Entity)
            .Select(u => (u.Reported, Value: MeterValues.Read(u.State, term)))
            .Where(p => p.Value is not null)
            .Select(p => new ReportPoint(p.Reported, p.Value!.Value))];

    /// <summary>On/off changes of the watched entity reported after the watch started; the state before them is the baseline.</summary>
    private static List<(DateTimeOffset At, int Direction)> Transitions(ImmutableArray<EntityUpdate> history, DateTimeOffset since)
    {
        var transitions = new List<(DateTimeOffset, int)>();
        string? previous = null;
        foreach (var update in history)
        {
            if (update.State is not ("on" or "off"))
            {
                previous = null;
                continue;
            }

            if (previous is not null && previous != update.State && update.Reported > since)
            {
                transitions.Add((update.Reported, update.State == "on" ? 1 : -1));
            }

            previous = update.State;
        }

        return transitions;
    }

    private SelfTestView View(Session session) => new(
        session.Id,
        session.Status,
        session.StartedAt,
        session.ExpiresAt,
        session.Request,
        [.. session.Observations],
        PhaseStepClassifier.Summarize([.. session.Observations.Select(o => o.Result)], _defaults.SelfTest.Classifier));

    private sealed class Session(Guid id, SelfTestRequest request, DateTimeOffset startedAt, DateTimeOffset expiresAt, IDisposable hold)
    {
        private IDisposable? _hold = hold;

        public Guid Id { get; } = id;

        public SelfTestRequest Request { get; } = request;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public SelfTestStatus Status { get; set; } = SelfTestStatus.Watching;

        public HashSet<DateTimeOffset> Seen { get; } = [];

        public List<JudgedObservation> Observations { get; } = [];

        /// <summary>The evaluation timer, re-armed for the next window that passes.</summary>
        public ITimer? Timer { get; set; }

        /// <summary>Fires once at the watch timeout.</summary>
        public ITimer? Expiry { get; set; }

        /// <summary>Ends the session's own subscription; idempotent.</summary>
        public void Release()
        {
            Timer?.Dispose();
            Expiry?.Dispose();
            Interlocked.Exchange(ref _hold, null)?.Dispose();
        }
    }
}
