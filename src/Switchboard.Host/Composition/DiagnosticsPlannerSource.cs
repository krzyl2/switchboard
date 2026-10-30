using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Host.Composition;

/// <summary>
/// Builds the planner's input from the signal layer's latest cycle (15-PLANNER, "Inputs and their signal roles"): the goals as the goal model states them, the
/// slot grid with prices, PV and house load, the devices in watts and the guard's static allocation limit per phase and circuit group. Reading only: the
/// limits are the guard's own thresholds (breaker rating, reserve, the top of the may-add band), never its live envelope. It lives at the host's edge, beside
/// the web layer, because it is one of the few things that may hold the full signal snapshot (the planner service itself sees a <see cref="PlannerCapture"/> only).
/// </summary>
internal sealed class DiagnosticsPlannerSource(ISignalDiagnosticsSource diagnostics, GoalTracker goals, LoadProfileFeed loadProfile, SignalClock clock) : IPlannerSource
{
    private readonly object _gate = new();
    private SwitchboardConfiguration? _revisionOf;
    private long _revision;

    public PlannerCapture? Capture(DateTimeOffset now)
    {
        var current = diagnostics.Current;
        if (current.Configuration?.Configuration is not { Planner: not null } configuration
            || current.Snapshot.Protection.Setup is not { } setup
            || current.Snapshot.Presentation is not { } presentation)
        {
            return null;
        }

        var parameters = configuration.Control.Planner;
        var board = goals.Evaluate(now);
        var requests = board.Goals.Select(g => g.Request).ToImmutableArray();
        var cuts = requests.Where(r => r.Deadline is not null).SelectMany(r => new[] { r.UsableDeadline!.Value, r.Deadline!.Value });
        var sources = PlanSourceMapper.Map(configuration, current.Snapshot.Forecast, loadProfile.Current, clock.Time.LocalTimeZone, now);
        var inputs = PlanInputs.Build(now, now + parameters.Horizon, cuts, parameters.Forecast(), sources);
        var voltage = presentation.NominalVoltage;
        var input = new PlanInput(
            now,
            inputs,
            Devices(setup, configuration, voltage),
            requests,
            Capacity(setup, voltage),
            parameters,
            configuration.Control.Economics.MinMargin,
            Revision(configuration));
        return new PlannerCapture(input, board.Inactive, Digest(current.Snapshot.Forecast), DeviceFingerprint(current.Snapshot.Protection.DeviceStates));
    }

    /// <summary>The revision a plan made for this configuration carries: one number per configuration object in force (shared with the plan view, which compares it).</summary>
    internal long RevisionOf(SwitchboardConfiguration configuration) => Revision(configuration);

    private long Revision(SwitchboardConfiguration configuration)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_revisionOf, configuration))
            {
                _revisionOf = configuration;
                _revision++;
            }

            return _revision;
        }
    }

    /// <summary>
    /// The guard's static allocation limit per constraint, watts: the lower of In less the reserve and the top of the may-add band, for each main phase and each
    /// phase of a circuit group. Never below zero. The nominal voltage converts amperes to watts here, the one place the planner's inputs do.
    /// </summary>
    internal static ImmutableDictionary<ConstraintKey, double> Capacity(ProtectionSetup setup, Volts voltage)
    {
        var limits = ImmutableDictionary.CreateBuilder<ConstraintKey, double>();
        foreach (var phase in setup.Phases)
        {
            limits[ConstraintKey.Main(phase)] = Watts(Amperes.Min(setup.ReserveThreshold(phase), setup.Breaker.Rating * setup.Breaker.MayAddUpTo), voltage);
        }

        foreach (var group in setup.Groups)
        {
            foreach (var phase in group.Phases)
            {
                limits[new ConstraintKey(group.Id, phase)] = Watts(Amperes.Min(group.ReserveThreshold(phase), group.Breaker.Rating * group.Breaker.MayAddUpTo), voltage);
            }
        }

        return limits.ToImmutable();
    }

    private static double Watts(Amperes current, Volts voltage) => Math.Max(0, current.Value) * voltage.Value;

    internal static ImmutableArray<PlanDevice> Devices(ProtectionSetup setup, SwitchboardConfiguration configuration, Volts voltage)
    {
        var result = ImmutableArray.CreateBuilder<PlanDevice>();
        foreach (var spec in setup.Devices.OrderBy(d => d.Id))
        {
            if (configuration.Devices.FirstOrDefault(d => d.Id == spec.Id) is not { } definition)
            {
                continue;
            }

            result.Add(Device(spec, definition, setup, voltage));
        }

        return result.ToImmutable();
    }

    private static PlanDevice Device(DeviceSpec spec, DeviceDefinition definition, ProtectionSetup setup, Volts voltage)
    {
        double StagePower(StageSpec stage) => stage.RatedCurrent.Value * voltage.Value * stage.Phases.Length;
        var totalPower = spec.Stages.Sum(StagePower);
        var kind = spec.Type switch
        {
            DeviceType.Modulating => PlanDeviceKind.Modulating,
            DeviceType.Storage => PlanDeviceKind.Storage,
            _ => PlanDeviceKind.Relay,
        };

        double min;
        double max;
        double? stepW = null;
        if (spec.Range is { } range && spec.Stages.Length > 0)
        {
            var phases = spec.Stages[0].Phases.Length;
            stepW = range.Step.Value * voltage.Value * phases;
            min = Math.Max(0, range.Min.Value) * voltage.Value * phases;
            max = Math.Max(0, range.Max.Value) * voltage.Value * phases;
        }
        else
        {
            min = spec.Stages.Length == 0 ? 0 : spec.Stages.Min(StagePower);
            max = totalPower;
        }

        // The share of the device's power on each phase: a stage draws its rated current on every phase it uses, so a three-phase stage is a third on each.
        var keys = ImmutableArray.CreateBuilder<PlanKeyShare>();
        var group = spec.CircuitGroup is { } name ? setup.Group(name) : null;
        foreach (var phase in spec.Phases)
        {
            var onPhase = spec.Stages.Where(s => s.Phases.Contains(phase)).Sum(s => s.RatedCurrent.Value * voltage.Value);
            var share = totalPower > 0 ? onPhase / totalPower : 0;
            keys.Add(new PlanKeyShare(ConstraintKey.Main(phase), share));
            if (group is not null && group.Phases.Contains(phase))
            {
                keys.Add(new PlanKeyShare(new ConstraintKey(group.Id, phase), share));
            }
        }

        return new PlanDevice(
            spec.Id,
            kind,
            min,
            max,
            spec.MinOn,
            keys.ToImmutable(),
            definition.Start == StartPolicy.Headroom,
            definition.Value,
            definition.Sources,
            Identity(spec),
            stepW);
    }

    /// <summary>A fingerprint of the device's physical model (type, stages, range, group): a plan entry made for another one is not applied.</summary>
    internal static long Identity(DeviceSpec spec)
    {
        var text = new StringBuilder();
        text.Append(spec.Id.Value).Append('|').Append(spec.Type).Append('|').Append(spec.CircuitGroup?.Value).Append('|');
        foreach (var stage in spec.Stages)
        {
            text.Append(stage.Index).Append(':').Append(string.Join(',', stage.Phases)).Append(':').Append(stage.RatedCurrent.Value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }

        if (spec.Range is { } range)
        {
            text.Append(range.Min.Value.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(range.Max.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        return Fnv(text.ToString());
    }

    private static long Fnv(string text)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * prime;
        }

        return unchecked((long)hash);
    }

    private static string Digest(ForecastInputs? forecast)
    {
        if (forecast is null)
        {
            return "-";
        }

        var text = new StringBuilder();
        foreach (var (name, sample) in forecast.Samples.OrderBy(s => s.Key.Value, StringComparer.Ordinal))
        {
            // The content, not when it was reported: Home Assistant rewrites a forecast sensor when it refreshes, and an unchanged series is not news.
            text.Append(name.Value).Append('=').Append(sample.Usable).Append(':');
            if (sample.Series is { } series)
            {
                foreach (var point in series.Points)
                {
                    text.Append(point.Start.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('/').Append(point.End?.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('/').Append(point.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                }
            }

            text.Append(sample.EnergyKwh?.ToString("R", CultureInfo.InvariantCulture)).Append('@').Append(sample.At?.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return Fnv(text.ToString()).ToString("x", CultureInfo.InvariantCulture);
    }

    private static string DeviceFingerprint(ImmutableArray<DeviceStateSample> states) =>
        states.IsDefault
            ? "-"
            : string.Join(',', states.GroupBy(s => s.Device).OrderBy(g => g.Key).Select(g => g.Key.Value + (g.All(s => s.Fresh) ? "+" : "-")));
}
