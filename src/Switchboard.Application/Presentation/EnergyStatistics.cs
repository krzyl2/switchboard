using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Presentation;

/// <summary>One device's energy in one bucket.</summary>
/// <param name="Measured">From its own power draw.</param>
/// <param name="Model">From the rated model (a relay's rating while it reports on): an estimate, never a measurement.</param>
/// <param name="Unknown">Observed time during which its power was not known.</param>
/// <param name="Pv">The part of its energy that came from local generation.</param>
/// <param name="Value">Its energy at its value per kWh in force, time-weighted: each step's energy at that step's effective value (the static
/// value without an <c>economics:</c> section). A plain number; the currency label belongs to configuration. It is the <c>value_gained</c> of savings accounting.</param>
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
/// <param name="Prices">The prices dispatch used in this bucket, time-weighted, and the controlled energy that ran while importing; null when none was recorded (no
/// <c>economics:</c> section, or a bucket read from a statistics.json older than schema 2: no price data, never back-filled).</param>
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
    TimeSpan VoltageObserved,
    BucketPrices? Prices = null)
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
            VoltageObserved + other.VoltageObserved,
            Prices is null ? other.Prices : other.Prices is null ? Prices : Prices.Add(other.Prices));
    }
}

/// <summary>
/// The time one price was in force in a bucket, by basis, and the price-weighted time: price times seconds, so the mean price is
/// <c>PriceSeconds / Priced</c> (sample-and-hold on the signal clock, each control step's price for as long as it was held, never the
/// price at the end of the bucket). Basis and flat/zone/live/fallback are those of <see cref="PriceBasis"/>.
/// </summary>
/// <param name="PriceSeconds">Currency per kWh times seconds, summed over the observed steps that had this price.</param>
public sealed record PriceTrack(double PriceSeconds, TimeSpan Flat, TimeSpan Zone, TimeSpan Live, TimeSpan Fallback)
{
    public static PriceTrack Zero { get; } = new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>The time this price was known; in a bucket that was fully priced it equals the observed time.</summary>
    public TimeSpan Priced => Flat + Zone + Live + Fallback;

    /// <summary>Some of the time was at a dynamic price's fallback (the entity was not usable): counted and flagged, never hidden.</summary>
    public bool AnyFallback => Fallback > TimeSpan.Zero;

    /// <summary>The time-weighted mean price, currency per kWh; null when no time was priced.</summary>
    public double? Mean => Priced > TimeSpan.Zero ? PriceSeconds / Priced.TotalSeconds : null;

    public PriceTrack Held(PricePoint price, TimeSpan duration) => this with
    {
        PriceSeconds = PriceSeconds + (price.PerKwh * duration.TotalSeconds),
        Flat = price.Basis == PriceBasis.Flat ? Flat + duration : Flat,
        Zone = price.Basis == PriceBasis.Zone ? Zone + duration : Zone,
        Live = price.Basis == PriceBasis.Live ? Live + duration : Live,
        Fallback = price.Basis == PriceBasis.Fallback ? Fallback + duration : Fallback,
    };

    public PriceTrack Add(PriceTrack other) =>
        new(PriceSeconds + other.PriceSeconds, Flat + other.Flat, Zone + other.Zone, Live + other.Live, Fallback + other.Fallback);
}

/// <summary>
/// What savings accounting needs of one bucket beyond its energies: the import and the export price actually in force (as dispatch saw them
/// each step), and how much of the controlled energy ran while the house imported (the rest it would have exported).
/// </summary>
public sealed record BucketPrices(PriceTrack Import, PriceTrack Export, KilowattHours ControlledOffsettingImport)
{
    public static BucketPrices Empty { get; } = new(PriceTrack.Zero, PriceTrack.Zero, KilowattHours.Zero);

    public BucketPrices Add(BucketPrices other) =>
        new(Import.Add(other.Import), Export.Add(other.Export), ControlledOffsettingImport + other.ControlledOffsettingImport);
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
