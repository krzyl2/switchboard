using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Options;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Presentation;

/// <summary>
/// Integrates the power picture of every control step into UTC-aligned energy buckets, on the
/// step's own time (the signal clock): a zero-order hold of step k's figures over [t_k, t_k+1),
/// split at bucket edges, so a constant fresh value keeps accumulating (07-PITFALLS: Riemann
/// integration that stops on a constant source). Only observed time is integrated: an interval
/// longer than MaxIntegrationGap, or one whose figures were not known, is counted as unobserved,
/// never as zero. A high-water mark keeps a clock step back from integrating anything twice, and
/// unobserved time is only ever allocated inside the retention window, so a forward jump of years
/// creates at most a window of buckets. It also counts sheds and would-sheds, and phase-voltage
/// losses (possible breaker trips) apart from supply outages. Not thread-safe: the hub calls it
/// under its lock. Nothing here reads a wall clock or the Recorder.
/// </summary>
public sealed class EnergyAccumulator
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    // Not Deferred: a reduction waiting for a lockout is counted when it takes effect (the applied
    // change carries the deferral in its inputs), and one dispatch drops while waiting switched nothing.
    private static readonly ImmutableHashSet<DecisionAction> ShedActions =
        [DecisionAction.SwitchOff, DecisionAction.ReduceToMinimum, DecisionAction.SetSetpoint];

    private static readonly ImmutableHashSet<ReasonCode> ShedReasons =
        [ReasonCode.GuardTierExceeded, ReasonCode.GuardBudgetLow, ReasonCode.ReserveThreshold, ReasonCode.ForeignLoad];

    private readonly StatisticsOptions _options;
    private ImmutableSortedDictionary<DateTimeOffset, EnergyBucket> _buckets = ImmutableSortedDictionary<DateTimeOffset, EnergyBucket>.Empty;
    private LifetimeCounters _lifetime = LifetimeCounters.Zero;
    private DateTimeOffset? _integratedTo;
    private Held? _held;
    private ImmutableHashSet<Phase> _lossPhases = [];
    private LossEvent? _loss;
    private bool _voltageMeasured;
    private ImmutableArray<StatisticsDevice> _devices = [];
    private DateTimeOffset? _at;
    private long _version;

    public EnergyAccumulator(StatisticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Counts one step: integrates the previous step's figures up to this one, then holds this step's.</summary>
    public void Observe(GuardStep step, PowerView view)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(view);
        var now = step.Evaluation.At;
        _lifetime = _lifetime.Since is null ? _lifetime with { Since = now } : _lifetime;
        if (_held is { } held)
        {
            Integrate(held, now);
        }

        CountSheds(step, _held?.Status, now);
        var voltageFresh = TrackVoltage(step, now);
        var setup = step.Inputs?.DispatchSetup;
        if (setup is not null)
        {
            var names = step.Inputs?.Presentation?.DisplayNames ?? ImmutableDictionary<DeviceName, string>.Empty;
            _devices = [.. setup.Devices.Select(d => new StatisticsDevice(d.Id, names.GetValueOrDefault(d.Id), d.Spec.ValuePerKwh))];
        }

        _held = new Held(now, view, voltageFresh, step.DispatchStatus, setup);
        _at = now;
        Prune(now);
        _version++;
    }

    public StatisticsSnapshot Snapshot() =>
        new(_at, _options.BucketLength, _voltageMeasured, _devices, _buckets, _lifetime, _integratedTo, _version);

    /// <summary>Takes over what statistics.json held: buckets within the retention window, the counters and the high-water mark.</summary>
    public void Restore(StatisticsState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.BucketLength != _options.BucketLength)
        {
            return;
        }

        _buckets = state.Buckets.Where(b => b.Start >= Cutoff(now)).ToImmutableSortedDictionary(b => b.Start, b => b);
        _lifetime = state.Lifetime;
        _integratedTo = state.IntegratedTo;
        _version++;
    }

    /// <summary>The most buckets the retention window can hold (the current, partial one included); statistics.json may hold no more.</summary>
    public static int MaxBuckets(StatisticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (int)(options.Retention.Ticks / options.BucketLength.Ticks) + 2;
    }

    /// <summary>The UTC bucket a moment falls into.</summary>
    public static DateTimeOffset BucketOf(DateTimeOffset at, TimeSpan length)
    {
        var elapsed = (at - Epoch).Ticks;
        var floor = elapsed >= 0 ? elapsed / length.Ticks : ((elapsed + 1) / length.Ticks) - 1;
        return Epoch + TimeSpan.FromTicks(floor * length.Ticks);
    }

    /// <summary>The oldest bucket start still kept: one retention back from the bucket of <paramref name="now"/>.</summary>
    private DateTimeOffset Cutoff(DateTimeOffset now) => BucketOf(now - _options.Retention, _options.BucketLength);

    private void Integrate(Held held, DateTimeOffset now)
    {
        var from = held.At;
        if (now <= from)
        {
            // A clock step back (or the same instant): nothing to integrate, and the high-water
            // mark keeps the following steps from counting this stretch a second time.
            return;
        }

        var gap = now - from;
        if (_integratedTo is { } mark && mark > from)
        {
            from = mark;
        }

        var cutoff = Cutoff(now);
        if (from < cutoff)
        {
            from = cutoff;
        }

        if (now <= from)
        {
            _integratedTo = _integratedTo is { } previous && previous > now ? previous : now;
            return;
        }

        var withinGap = gap <= _options.MaxIntegrationGap;
        var observed = withinGap && held.View.Total.Load.Value is not null && held.View.Total.NetImport.Value is not null;
        for (var start = from; start < now;)
        {
            var bucket = BucketOf(start, _options.BucketLength);
            var end = bucket + _options.BucketLength < now ? bucket + _options.BucketLength : now;
            var duration = end - start;
            var current = _buckets.GetValueOrDefault(bucket) ?? EnergyBucket.Empty(bucket);
            current = observed ? AddObserved(current, held, duration) : current with { Unobserved = current.Unobserved + duration };
            if (withinGap && held.VoltageFresh)
            {
                current = current with { VoltageObserved = current.VoltageObserved + duration };
                _lifetime = _lifetime with { VoltageObserved = _lifetime.VoltageObserved + duration };
            }

            _buckets = _buckets.SetItem(bucket, current);
            start = end;
        }

        _integratedTo = now;
    }

    /// <summary>
    /// One observed stretch of the held figures. Generation is attributed part by part: export first
    /// (from the net total), the rest to every known consumer in proportion to its power; only the
    /// part of a phase that cannot be split (its load minus the known devices on it, while one of them
    /// is unknown) goes to unattributed, never another phase's.
    /// </summary>
    private static EnergyBucket AddObserved(EnergyBucket bucket, Held held, TimeSpan duration)
    {
        var view = held.View;
        var total = view.Total;
        var net = total.NetImport.Value!.Value;
        var generation = Math.Max(0, total.Generation.InBalance ?? 0);
        var kwh = (double watts) => SignalUnits.EnergyOf(new Watts(watts), duration);

        var devices = bucket.Devices;
        var storageCharge = 0.0;
        var storageDischarge = 0.0;
        var uncontrolled = 0.0;
        var consumers = new List<(DeviceName? Device, PvTarget Target, double Watts)>();
        foreach (var phase in view.Phases)
        {
            var onPhase = view.Devices.SelectMany(d => d.Phases.Where(p => p.Phase == phase.Phase).Select(p => (d.Device, Part: p))).ToList();
            var knownOnPhase = 0.0;
            foreach (var (device, part) in onPhase)
            {
                if (part.Power is not { } power)
                {
                    continue;
                }

                knownOnPhase += power.Value;
                if (IsStorage(held, device))
                {
                    storageCharge += Math.Max(0, power.Value);
                    storageDischarge += Math.Max(0, -power.Value);
                    consumers.Add((device, PvTarget.Storage, Math.Max(0, power.Value)));
                }
                else
                {
                    consumers.Add((device, PvTarget.Device, Math.Max(0, power.Value)));
                }
            }

            if (phase.Balance.Uncontrolled.Value is { } house)
            {
                uncontrolled += Math.Max(0, house);
                consumers.Add((null, PvTarget.House, Math.Max(0, house)));
            }
            else if (phase.Balance.Load.Value is { } load)
            {
                consumers.Add((null, PvTarget.Unattributed, Math.Max(0, load - knownOnPhase)));
            }
        }

        var export = Math.Max(0, -net);
        var self = Math.Max(0, generation - export);
        var consumed = consumers.Sum(c => c.Watts);
        var pv = new PvAttribution(KilowattHours.Zero, KilowattHours.Zero, kwh(Math.Min(export, generation)), KilowattHours.Zero);
        var pvByDevice = new Dictionary<DeviceName, double>();
        if (self > 0)
        {
            if (consumed <= 0)
            {
                pv = pv with { Unattributed = pv.Unattributed + kwh(self) };
            }
            else
            {
                foreach (var (device, target, watts) in consumers)
                {
                    var share = self * watts / consumed;
                    switch (target)
                    {
                        case PvTarget.House:
                            pv = pv with { House = pv.House + kwh(share) };
                            break;
                        case PvTarget.Unattributed:
                            pv = pv with { Unattributed = pv.Unattributed + kwh(share) };
                            break;
                        case PvTarget.Storage:
                            pv = pv with { Storage = pv.Storage + kwh(share) };
                            break;
                        default:
                            pvByDevice[device!.Value] = pvByDevice.GetValueOrDefault(device.Value) + share;
                            break;
                    }
                }
            }
        }

        // The prices dispatch used in this step (sample-and-hold, like every figure here): the effective value of a device and the two prices. Without an
        // economics section there is no view and the value is the static one, as before.
        var economics = held.Status?.Economics;
        var controlled = 0.0;
        foreach (var device in view.Devices)
        {
            var value = economics?.Devices.FirstOrDefault(d => d.Device == device.Device)?.VEff.PerKwh
                ?? held.Setup?.Device(device.Device)?.Spec.ValuePerKwh ?? 0;
            DeviceEnergy energy;
            if (device.Total is null)
            {
                energy = DeviceEnergy.Zero with { Unknown = duration };
            }
            else
            {
                var storage = IsStorage(held, device.Device);
                var measured = device.Phases.Where(p => p.Source == PowerSource.Measured).Sum(p => storage ? Math.Max(0, p.Power!.Value.Value) : p.Power!.Value.Value);
                var model = device.Phases.Where(p => p.Source == PowerSource.RatedModel).Sum(p => p.Power!.Value.Value);
                var measuredKwh = kwh(Math.Max(0, measured));
                var modelKwh = kwh(Math.Max(0, model));
                energy = new DeviceEnergy(measuredKwh, modelKwh, TimeSpan.Zero, kwh(pvByDevice.GetValueOrDefault(device.Device)), (measuredKwh + modelKwh).Value * value);
                controlled += (measuredKwh + modelKwh).Value;
            }

            devices = devices.SetItem(device.Device, devices.GetValueOrDefault(device.Device, DeviceEnergy.Zero).Add(energy));
        }

        var prices = bucket.Prices;
        if (economics is not null)
        {
            // What the controlled loads took while the house imported would have been imported less with them off; the rest exported
            // (14-ECONOMICS, Savings; only net billing prices the two apart, but the split is the same fact for every model).
            var offset = new KilowattHours(Math.Min(controlled, kwh(Math.Max(0, net)).Value));
            var current = prices ?? BucketPrices.Empty;
            prices = new BucketPrices(
                economics.Import is { } importPrice ? current.Import.Held(importPrice, duration) : current.Import,
                economics.Export is { } exportPrice ? current.Export.Held(exportPrice, duration) : current.Export,
                current.ControlledOffsettingImport + offset);
        }

        return bucket with
        {
            Prices = prices,
            Observed = bucket.Observed + duration,
            GridImport = bucket.GridImport + kwh(Math.Max(0, net)),
            GridExport = bucket.GridExport + kwh(export),
            Generation = bucket.Generation + kwh(generation),
            Load = bucket.Load + kwh(Math.Max(0, total.Load.Value!.Value)),
            Uncontrolled = bucket.Uncontrolled + kwh(uncontrolled),
            StorageCharge = bucket.StorageCharge + kwh(storageCharge),
            StorageDischarge = bucket.StorageDischarge + kwh(storageDischarge),
            Pv = bucket.Pv.Add(pv),
            Devices = devices,
        };
    }

    private static bool IsStorage(Held held, DeviceName device) => held.Setup?.Device(device)?.Spec.Type == DeviceType.Storage;

    /// <summary>
    /// A shed or would-shed: a device decision switching off, reducing to the minimum or lowering a
    /// setpoint, for a tier, a low budget or the reserve threshold, whose target is below what the
    /// device was intended at in the previous step. Counted once per device per step (a stepped
    /// device's stages are one shed); commanded ones are sheds, the rest would-sheds. Safe-state and
    /// kill-switch holds are not sheds. A reduction deferred by a min_on lockout counts once, when it
    /// takes effect at the lockout's expiry (in active mode that is the command), never also when the
    /// deferral was recorded.
    /// </summary>
    private void CountSheds(GuardStep step, DispatchStatus? previous, DateTimeOffset now)
    {
        var setup = step.Evaluation.Setup;
        var sheds = 0;
        var would = 0;
        foreach (var group in step.Decisions
            .Where(d => d.Scope == DecisionScope.Device && d.Device is not null && ShedActions.Contains(d.Action) && ShedReasons.Contains(d.Reason))
            .GroupBy(d => d.Device!.Value))
        {
            var spec = setup?.Device(group.Key);
            var before = previous?.Devices.FirstOrDefault(d => d.Id == group.Key) is { } was ? was.Intended ?? was.Reported : null;
            var lowered = group.Any(d =>
                spec is null || before is null || d.Target is not { } target
                || DeviceLevels.Total(spec, target) < DeviceLevels.Total(spec, before) - DeviceLevel.SetpointTolerance);
            if (!lowered)
            {
                continue;
            }

            if (group.Any(d => d.Commanded))
            {
                sheds++;
            }
            else
            {
                would++;
            }
        }

        if (sheds + would == 0)
        {
            return;
        }

        Update(now, b => b with { Sheds = b.Sheds + sheds, WouldSheds = b.WouldSheds + would });
        _lifetime = _lifetime with { Sheds = _lifetime.Sheds + sheds, WouldSheds = _lifetime.WouldSheds + would };
    }

    /// <summary>
    /// Phase voltages: a fresh voltage below VoltageLossFraction × the nominal voltage puts its phase
    /// in loss, a fresh one at or above takes it out, a stale one changes nothing. An event begins
    /// when the first phase enters loss: on some phases only it is a possible breaker trip; once
    /// every mapped phase is in loss it is a supply outage instead (counted once, moved if it began
    /// on fewer phases). Returns whether every mapped phase voltage is fresh.
    /// </summary>
    private bool TrackVoltage(GuardStep step, DateTimeOffset now)
    {
        var presentation = step.Inputs?.Presentation;
        var inputs = step.Inputs?.Dispatch;
        if (presentation is null || inputs is null)
        {
            return false;
        }

        var mapped = presentation.VoltageMapped.Where(v => v.Value).Select(v => v.Key).ToImmutableHashSet();
        _voltageMeasured = !mapped.IsEmpty;
        if (mapped.IsEmpty)
        {
            _lossPhases = [];
            _loss = null;
            return false;
        }

        var threshold = presentation.NominalVoltage.Value * _options.VoltageLossFraction;
        var fresh = true;
        var loss = _lossPhases;
        foreach (var phase in mapped)
        {
            if (inputs.For(phase)?.Voltage is not { } voltage)
            {
                fresh = false;
                continue;
            }

            loss = voltage.Value < threshold ? loss.Add(phase) : loss.Remove(phase);
        }

        loss = loss.Intersect(mapped);
        var everywhere = mapped.IsSubsetOf(loss);
        if (_lossPhases.IsEmpty && !loss.IsEmpty)
        {
            _loss = new LossEvent(BucketOf(now, _options.BucketLength), everywhere);
            CountLoss(_loss.Bucket, everywhere ? 0 : 1, everywhere ? 1 : 0);
        }
        else if (_loss is { Outage: false } open && everywhere)
        {
            _loss = open with { Outage = true };
            CountLoss(open.Bucket, -1, 1);
        }

        if (loss.IsEmpty)
        {
            _loss = null;
        }

        _lossPhases = loss;
        return fresh;
    }

    private void CountLoss(DateTimeOffset bucket, int trips, int outages)
    {
        if (_buckets.ContainsKey(bucket) || trips > 0 || outages > 0)
        {
            var current = _buckets.GetValueOrDefault(bucket) ?? EnergyBucket.Empty(bucket);
            _buckets = _buckets.SetItem(bucket, current with
            {
                VoltageLosses = Math.Max(0, current.VoltageLosses + trips),
                SupplyOutages = current.SupplyOutages + outages,
            });
        }

        _lifetime = _lifetime with
        {
            VoltageLosses = Math.Max(0, _lifetime.VoltageLosses + trips),
            SupplyOutages = _lifetime.SupplyOutages + outages,
        };
    }

    private void Update(DateTimeOffset at, Func<EnergyBucket, EnergyBucket> change)
    {
        var bucket = BucketOf(at, _options.BucketLength);
        _buckets = _buckets.SetItem(bucket, change(_buckets.GetValueOrDefault(bucket) ?? EnergyBucket.Empty(bucket)));
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = Cutoff(now);
        var old = _buckets.Keys.TakeWhile(k => k < cutoff).ToList();
        if (old.Count > 0)
        {
            _buckets = _buckets.RemoveRange(old);
        }
    }

    private enum PvTarget
    {
        Device,
        Storage,
        House,
        Unattributed,
    }

    /// <summary>The step whose figures are held until the next one.</summary>
    private sealed record Held(DateTimeOffset At, PowerView View, bool VoltageFresh, DispatchStatus? Status, DispatchSetup? Setup);

    /// <summary>An open voltage-loss event and where it was counted.</summary>
    private sealed record LossEvent(DateTimeOffset Bucket, bool Outage);
}
