using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Options;
using Switchboard.Application.State;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Presentation;

/// <summary>How the two presentation files were found at start and whether they can be written.</summary>
public sealed record PresentationFileReport(
    StateFileStatus Statistics,
    string? StatisticsWarning,
    StateFileStatus Excursions,
    string? ExcursionsWarning,
    string? WriteError)
{
    public static PresentationFileReport Fresh { get; } = new(StateFileStatus.Fresh, null, StateFileStatus.Fresh, null, null);
}

/// <summary>The presentation's read side: immutable snapshots, read without locking.</summary>
public interface IPresentationSource
{
    /// <summary>The last control step observed; null before the first.</summary>
    GuardStep? LastStep { get; }

    /// <summary>The power picture of the last step; null before the first.</summary>
    PowerView? Power { get; }

    StatisticsSnapshot Statistics { get; }

    ExcursionsSnapshot Excursions { get; }

    PresentationFileReport Report { get; }
}

/// <summary>
/// A read-only consumer of the control loop: on every step (the guard runner's Stepped event, on
/// the guard's thread) it builds the power picture, feeds the energy accumulator and the excursion
/// recorder under one lock, and swaps in immutable snapshots for the API. It touches memory only:
/// a save is requested through a one-slot channel and done by the writer loop
/// (<see cref="PresentationPersistence"/>). It holds no command port, no kill switch and no override
/// control, and nothing it does reaches protection, dispatch or the effector gate.
/// </summary>
public sealed class PresentationHub : IPresentationSource
{
    private readonly StatisticsOptions _options;
    private readonly ILogger<PresentationHub> _logger;
    private readonly object _sync = new();
    private readonly EnergyAccumulator _energy;
    private readonly ExcursionRecorder _excursions;
    private readonly Dictionary<Phase, DateTimeOffset> _foreignDetected = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private GuardStep? _lastStep;
    private PowerView? _power;
    private StatisticsSnapshot _statistics;
    private ExcursionsSnapshot _excursionSnapshot = ExcursionsSnapshot.Empty;
    private PresentationFileReport _report = PresentationFileReport.Fresh;
    private bool _failing;

    public PresentationHub(IOptions<StatisticsOptions> options, ILogger<PresentationHub> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        _energy = new EnergyAccumulator(_options);
        _excursions = new ExcursionRecorder(_options);
        _statistics = _energy.Snapshot();
    }

    public GuardStep? LastStep => Volatile.Read(ref _lastStep);

    public PowerView? Power => Volatile.Read(ref _power);

    public StatisticsSnapshot Statistics => Volatile.Read(ref _statistics);

    public ExcursionsSnapshot Excursions => Volatile.Read(ref _excursionSnapshot);

    public PresentationFileReport Report => Volatile.Read(ref _report);

    /// <summary>Wakes the writer when something must be saved before the next interval (an excursion ended).</summary>
    public ChannelReader<bool> SaveRequests => _wake.Reader;

    /// <summary>Follows the control loop. Call once, before the runner's first step.</summary>
    public void Attach(GuardRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Stepped += (_, step) => Observe(step);
    }

    /// <summary>Takes over what the two files held; call before the first step.</summary>
    public void Restore(PresentationLoad<StatisticsState> statistics, PresentationLoad<ExcursionState> excursions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(excursions);
        lock (_sync)
        {
            if (statistics.State is { } stats)
            {
                _energy.Restore(stats, now);
            }

            if (excursions.State is { } recorded)
            {
                _excursions.Restore(recorded, now);
            }

            Volatile.Write(ref _statistics, _energy.Snapshot());
            Volatile.Write(ref _excursionSnapshot, _excursions.Snapshot());
            Volatile.Write(ref _report, new PresentationFileReport(statistics.Status, statistics.Warning, excursions.Status, excursions.Warning, null));
        }
    }

    /// <summary>
    /// Called on the guard's thread after every step. A failure here is logged once per streak and
    /// never reaches the guard (the runner isolates its listeners as well).
    /// </summary>
    public void Observe(GuardStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            lock (_sync)
            {
                foreach (var foreign in step.Decisions.Where(d => d.Action == DecisionAction.ForeignLoadDetected && d.Phase is not null))
                {
                    _foreignDetected[foreign.Phase!.Value] = foreign.At;
                }

                _excursions.Observe(step);
                var power = PowerViewBuilder.Build(step, ForeignOfExcursions(step.Evaluation.At), _options.VoltageLossFraction);

                // Published before the accumulator runs: a failure in the statistics must not
                // freeze the power picture the dashboard shows.
                Volatile.Write(ref _lastStep, step);
                Volatile.Write(ref _power, power);
                _energy.Observe(step, power);
                Volatile.Write(ref _statistics, _energy.Snapshot());
                Volatile.Write(ref _excursionSnapshot, _excursions.Snapshot());
                if (_excursions.TakeEnded())
                {
                    _wake.Writer.TryWrite(true);
                }

                _failing = false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_failing)
            {
                _logger.LogError(ex, "The runtime UI could not count a control step; protection and dispatch are unaffected");
                _failing = true;
            }
        }
    }

    /// <summary>Called by the writer after a save attempt.</summary>
    public void ReportWrite(string? error)
    {
        var current = Report;
        Volatile.Write(ref _report, current with { WriteError = error });
    }

    /// <summary>
    /// The foreign load events that belong to the excursion now on screen: one detected since shortly
    /// before an ongoing excursion began, or, without one, within the last pre-roll.
    /// </summary>
    private Dictionary<Phase, DateTimeOffset> ForeignOfExcursions(DateTimeOffset now)
    {
        var result = new Dictionary<Phase, DateTimeOffset>();
        foreach (var (phase, at) in _foreignDetected)
        {
            var since = _excursions.Active(phase) is { } active ? active.Start - _options.ExcursionPreRoll : now - _options.ExcursionPreRoll;
            if (at >= since && at <= now)
            {
                result[phase] = at;
            }
        }

        return result;
    }
}
