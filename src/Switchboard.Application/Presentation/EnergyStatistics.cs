using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Presentation;

/// <summary>One device's energy in one bucket.</summary>
/// <param name="Measured">From its own power draw.</param>
/// <param name="Model">From the rated model (a relay's rating while it reports on): an estimate, never a measurement.</param>
/// <param name="Unknown">Observed time during which its power was not known.</param>
/// <param name="Pv">The part of its energy that came from local generation.</param>
/// <param name="Value">Its energy at its value per kWh in force (no currency: v1 has no tariff).</param>
public sealed record DeviceEnergy(KilowattHours Measured, KilowattHours Model, TimeSpan Unknown, KilowattHours Pv, double Value)
{
    public static DeviceEnergy Zero { get; } = new(KilowattHours.Zero, KilowattHours.Zero, TimeSpan.Zero, KilowattHours.Zero, 0);

    public KilowattHours Total => Measured + Model;

    public DeviceEnergy Add(DeviceEnergy other) =>
        new(Measured + other.Measured, Model + other.Model, Unknown + other.Unknown, Pv + other.Pv, Value + other.Value);
}

/// <summary>
/// Where local generation went: the house's own consumption, storage, the grid, and the part that
/// could not be split while a device's power was unknown. Together with the devices' PV shares it
/// sums to the generation of the bucket.
/// </summary>
public sealed record PvAttribution(KilowattHours House, KilowattHours Storage, KilowattHours Export, KilowattHours Unattributed)
{
    public static PvAttribution Zero { get; } = new(KilowattHours.Zero, KilowattHours.Zero, KilowattHours.Zero, KilowattHours.Zero);

    public PvAttribution Add(PvAttribution other) =>
        new(House + other.House, Storage + other.Storage, Export + other.Export, Unattributed + other.Unattributed);
}

/// <summary>
/// One UTC-aligned bucket of integrated energy. <see cref="Observed"/> is the time the whole
/// installation's power picture was known; <see cref="Unobserved"/> the time it was not (stale
/// inputs, a gap). Energy is integrated over observed time only: unknown is never counted as 0.
/// </summary>
/// <param name="VoltageLosses">Phase-voltage losses on some phases while the others stayed up: possible breaker trips.</param>
/// <param name="SupplyOutages">Losses on every mapped phase at once: a supply outage, not a trip.</param>
/// <param name="VoltageObserved">Time during which every mapped phase voltage was fresh.</param>
public sealed record EnergyBucket(
    DateTimeOffset Start,
    TimeSpan Observed,
    TimeSpan Unobserved,
    KilowattHours GridImport,
    KilowattHours GridExport,
    KilowattHours Generation,
    KilowattHours Load,
    KilowattHours Uncontrolled,
    KilowattHours StorageCharge,
    KilowattHours StorageDischarge,
    PvAttribution Pv,
    ImmutableDictionary<DeviceName, DeviceEnergy> Devices,
    int Sheds,
    int WouldSheds,
    int VoltageLosses,
    int SupplyOutages,
    TimeSpan VoltageObserved)
{
    public static EnergyBucket Empty(DateTimeOffset start) => new(
        start,
        TimeSpan.Zero,
        TimeSpan.Zero,
        KilowattHours.Zero,
        KilowattHours.Zero,
        KilowattHours.Zero,
        KilowattHours.Zero,
        KilowattHours.Zero,
        KilowattHours.Zero,
        KilowattHours.Zero,
        PvAttribution.Zero,
        ImmutableDictionary<DeviceName, DeviceEnergy>.Empty,
        0,
        0,
        0,
        0,
        TimeSpan.Zero);

    /// <summary>The sum of two buckets (or of a range), keeping this one's start.</summary>
    public EnergyBucket Add(EnergyBucket other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var devices = Devices;
        foreach (var (device, energy) in other.Devices)
        {
            devices = devices.SetItem(device, devices.GetValueOrDefault(device, DeviceEnergy.Zero).Add(energy));
        }

        return new EnergyBucket(
            Start,
            Observed + other.Observed,
            Unobserved + other.Unobserved,
            GridImport + other.GridImport,
            GridExport + other.GridExport,
            Generation + other.Generation,
            Load + other.Load,
            Uncontrolled + other.Uncontrolled,
            StorageCharge + other.StorageCharge,
            StorageDischarge + other.StorageDischarge,
            Pv.Add(other.Pv),
            devices,
            Sheds + other.Sheds,
            WouldSheds + other.WouldSheds,
            VoltageLosses + other.VoltageLosses,
            SupplyOutages + other.SupplyOutages,
            VoltageObserved + other.VoltageObserved);
    }
}

/// <summary>Counters since the statistics began (they survive restarts through statistics.json).</summary>
public sealed record LifetimeCounters(DateTimeOffset? Since, long Sheds, long WouldSheds, long VoltageLosses, long SupplyOutages, TimeSpan VoltageObserved)
{
    public static LifetimeCounters Zero { get; } = new(null, 0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>What statistics.json holds. <see cref="IntegratedTo"/> is the high-water mark: nothing before it is integrated twice.</summary>
public sealed record StatisticsState(
    DateTimeOffset? SavedAt,
    TimeSpan BucketLength,
    DateTimeOffset? IntegratedTo,
    ImmutableArray<EnergyBucket> Buckets,
    LifetimeCounters Lifetime);

/// <summary>A declared device as the statistics name and value it.</summary>
public sealed record StatisticsDevice(DeviceName Id, string? Name, double ValuePerKwh);

/// <summary>The statistics at one moment, immutable: what the API reads and the writer saves.</summary>
/// <param name="At">The time of the last step counted (signal clock); null before the first.</param>
/// <param name="VoltageMeasured">At least one phase_voltage signal is mapped: voltage losses can be seen at all.</param>
/// <param name="Version">Grows with every change; the writer saves when it moved.</param>
public sealed record StatisticsSnapshot(
    DateTimeOffset? At,
    TimeSpan BucketLength,
    bool VoltageMeasured,
    ImmutableArray<StatisticsDevice> Devices,
    ImmutableSortedDictionary<DateTimeOffset, EnergyBucket> Buckets,
    LifetimeCounters Lifetime,
    DateTimeOffset? IntegratedTo,
    long Version)
{
    public StatisticsState ToState(DateTimeOffset savedAt) => new(savedAt, BucketLength, IntegratedTo, [.. Buckets.Values], Lifetime);
}
