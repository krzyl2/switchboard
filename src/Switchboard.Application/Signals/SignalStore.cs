using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <param name="ProbeLatencyAllowance">How long a freshness probe may take to answer (the state read timeout).</param>
public sealed record SignalStoreSettings(TimeSpan ProbeLatencyAllowance);

/// <summary>
/// The signal mapping layer's state machine: the only place where entity IDs meet logical signals
/// (SIG-01). Normalizes units and signs (SIG-02), resolves direction (SIG-03), keeps freshness from
/// report times (SIG-05, SIG-06), detects refresh intervals (SIG-07), derives per-phase house load
/// (SIG-08) and runs the balance check (SIG-09). It also reads the devices' applied states (a
/// state declared on_change is fresh while available, with no age limit; a select stop actuator is
/// read against its declared stop option), their declared draws and the circuit groups' sub-meters,
/// and keeps the options each entity reports (a select's choices) for the send-time options check.
/// Deterministic: it never reads a clock, every call is handed <c>now</c>, and it is driven by
/// exactly one reader.
/// </summary>
/// <remarks>
/// A facade over the parts that own the work: <see cref="SignalRegistry"/> (which signals exist, as an
/// immutable <see cref="SignalMap"/>), <see cref="SampleIntake"/> (a state into a runtime, with the
/// <see cref="BalanceChecker"/>), <see cref="ProbeScheduler"/> (when to ask again) and
/// <see cref="SnapshotBuilder"/> (one immutable snapshot). Freshness and direction are functions of a
/// runtime and <c>now</c> (<see cref="SignalFreshness"/>, <see cref="DirectionResolver"/>). The store
/// holds no lock and starts no thread: its only reader is the runner, fed by the single-reader inbox.
/// </remarks>
public sealed class SignalStore
{
    private readonly SignalStoreSettings _settings;
    private readonly SignalRegistry _registry = new();
    private readonly BalanceChecker _balance = new();
    private readonly SampleIntake _intake;
    private readonly ProbeScheduler _probes = new();
    private long _sequence;

    public SignalStore(SignalStoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _intake = new SampleIntake(_balance);
    }

    /// <summary>The configuration result last applied, valid or not; null before the first one.</summary>
    public ConfigurationResult? ConfigurationResult { get; private set; }

    public SignalSourceStatus Source { get; private set; } = SignalSourceStatus.Disconnected;

    /// <summary>
    /// A valid document replaces the signal set; signals reading the same entity the same way keep
    /// their samples, so editing a comment never blanks protection data. A signal whose reading
    /// changed (kind, unit or sign) re-reads the state it last saw under the new definition, as a new
    /// initial state would deliver it: Home Assistant sends nothing for a state that does not change,
    /// so waiting for the next push could leave the signal awaiting for hours (an inverter asleep all
    /// night). An invalid or missing document clears everything: no signal, no valid protection data,
    /// nothing tracked.
    /// </summary>
    public void ApplyConfiguration(ConfigurationResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        ConfigurationResult = result;
        var reread = _registry.Replace(result);

        // After the new set is in place, so the balance check and direction see the new definitions.
        foreach (var (runtime, raw) in reread)
        {
            _intake.Reread(_registry.Map, runtime, raw, now);
        }
    }

    /// <summary>
    /// The options Home Assistant reported with the latest state of each tracked entity that reports
    /// any (a select's choices). Empty while the source is not connected: nothing is known then, and
    /// an entity removed, missing or re-awaited has none. Absent never means "offers nothing".
    /// </summary>
    public ImmutableDictionary<EntityId, ImmutableArray<string>> ReportedOptions()
    {
        if (Source != SignalSourceStatus.Connected)
        {
            return ImmutableDictionary<EntityId, ImmutableArray<string>>.Empty;
        }

        var options = ImmutableDictionary.CreateBuilder<EntityId, ImmutableArray<string>>();
        foreach (var signal in _registry.Map.Signals)
        {
            if (signal.Presence == Presence.Present && signal.Raw?.Options is { } offered)
            {
                options[signal.Definition.Entity] = offered;
            }
        }

        return options.ToImmutable();
    }

    /// <summary>
    /// The clock stepped back (an NTP step, a restored VM): every held report time now lies in the
    /// future, so held values would look fresh while newer reports, stamped before them, are dropped
    /// as late. Everything is forgotten and every signal is read again (see <see cref="TakeDueProbes"/>).
    /// </summary>
    public void ForgetSamplesAfterClockStep()
    {
        foreach (var signal in _registry.Map.Signals)
        {
            signal.MarkAbsent(Presence.Awaiting);
        }

        _probes.RequestResync();
    }

    public void ApplySourceStatus(SignalSourceStatus status)
    {
        Source = status;
        if (status != SignalSourceStatus.Connected)
        {
            foreach (var signal in _registry.Map.Signals)
            {
                signal.Refresh.Break();
            }
        }
    }

    public void Apply(EntityUpdateBatch batch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _intake.Apply(_registry.Map, batch, now);
    }

    /// <summary>Returns the new tracked set once after it changed.</summary>
    public bool TryTakeTrackedChange(out ImmutableArray<TrackedEntity> tracked) => _registry.TryTakeTrackedChange(out tracked);

    /// <summary>
    /// Entities whose freshness should be confirmed now: signals silent for after_fraction × max_age
    /// (a repeated value produces no event, only a newer last_reported), and direction signals a
    /// changed current is still waiting for after the pairing wait. Each is marked as probed.
    /// </summary>
    public IReadOnlyList<EntityId> TakeDueProbes(DateTimeOffset now) => _probes.TakeDue(_registry.Map, Source, now);

    /// <summary>The next moment the snapshot changes without any input: a staleness transition, a probe due, a pairing wait running out.</summary>
    public DateTimeOffset? NextDeadline(DateTimeOffset now) => _probes.NextDeadline(_registry.Map, Source, now);

    /// <summary>
    /// Run-time configuration warnings. A repeating value stays fresh only through a probe that
    /// starts after after_fraction × max_age and must answer before max_age, so the rest of max_age
    /// has to cover one refresh plus the probe's own answer time. A select stop actuator whose
    /// reported options lack its declared stop or resume option is named too: that option would be
    /// refused at send time, so the device could not be stopped (or resumed) through it.
    /// </summary>
    public ImmutableArray<ConfigurationIssue> RuntimeWarnings()
    {
        var map = _registry.Map;
        if (map.Configuration is not { } configuration)
        {
            return [];
        }

        var parameters = configuration.Control.Signals;
        var warnings = ImmutableArray.CreateBuilder<ConfigurationIssue>();
        var offered = ReportedOptions();
        foreach (var device in configuration.Devices)
        {
            if (device.StopActuator is not { } stop || device.StopOptions is not { } options || !offered.TryGetValue(stop, out var choices))
            {
                continue;
            }

            foreach (var (key, option) in new[] { ("stop_option", options.StopOption), ("resume_option", options.ResumeOption) })
            {
                if (!choices.Contains(option.Value, StringComparer.Ordinal))
                {
                    warnings.Add(new ConfigurationIssue(
                        ConfigurationIssueCodes.InvalidValue,
                        $"{device.Path}.{key}",
                        null,
                        null,
                        $"{stop} does not offer the option '{option}' declared as {key} of {device.Id} (it offers {string.Join(", ", choices.Select(c => $"'{c}'"))}); Switchboard will not send it, so {(key == "stop_option" ? "the device cannot be stopped" : "the device cannot be resumed")} through it. Declare the option exactly as the select offers it."));
                }
            }
        }

        foreach (var signal in map.Signals)
        {
            if (signal.Definition.Freshness == StateFreshness.OnChange || signal.Refresh.Estimate(parameters.Refresh) is not { } refresh)
            {
                continue;
            }

            var margin = signal.Definition.MaxAge * (1 - parameters.FreshnessProbe.AfterFraction);
            if (margin <= refresh + _settings.ProbeLatencyAllowance)
            {
                warnings.Add(new ConfigurationIssue(
                    ConfigurationIssueCodes.MaxAgeShortForProbe,
                    signal.Definition.MaxAgePath,
                    null,
                    null,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"max_age {signal.Definition.MaxAge.TotalSeconds:0.#} s is too short for a {refresh.TotalSeconds:0.#} s refresh: a value that repeats may turn stale before its last report can be confirmed. Raise max_age.")));
            }
        }

        return warnings.ToImmutable();
    }

    /// <summary>An immutable picture of everything at <paramref name="now"/>; a new instance every call.</summary>
    public SignalSnapshot Snapshot(DateTimeOffset now)
    {
        var sequence = ++_sequence;
        return new SnapshotBuilder(_registry.Map, Source, ConfigurationResult, _balance, now).Build(sequence, _intake.Rejections);
    }
}
