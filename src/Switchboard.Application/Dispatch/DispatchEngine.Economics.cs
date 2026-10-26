using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The economics of the dispatch loop (14-ECONOMICS, slice 3): the prices and values of a step on the injected clock's local
/// time, the live value order and the read-only exposure. Nothing here can raise a load: order decides who is asked first, the
/// allowance gate (<see cref="ApplyAllowanceGate"/>) only narrows, and the guard never sees any of it.
/// </summary>
public sealed partial class DispatchEngine
{
    private readonly TimeProvider _time;
    private readonly ValueOrder _valueOrder = new();
    private EconomicsContext? _economics;

    /// <summary>The energy class each device was allowed on at the last cycle boundary; kept between boundaries so the read-only view does not flicker.</summary>
    private readonly Dictionary<DeviceName, EnergyClass?> _classes = [];
    private (EconomicsSetup Setup, string ZoneId, Tariff? Import, Tariff? Export)? _tariffs;

    // Whether each dynamic price was at its fallback at the previous step: a change is an event, a steady state is not.
    private bool _importInFallback;
    private bool _exportInFallback;
    private ImmutableArray<TariffFallbackChange> _fallbackChanges = [];

    /// <summary>A dispatch engine on the system's time zone (tests that do not use prices).</summary>
    public DispatchEngine()
        : this(TimeProvider.System)
    {
    }

    /// <param name="time">Only its <see cref="TimeProvider.LocalTimeZone"/> is read, for the tariff zones: the engine never reads the time itself, every call is handed <c>now</c>.</param>
    public DispatchEngine(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>
    /// The prices, values and sources of this step: the tariff zone and the two prices in force at <paramref name="now"/> on the
    /// local clock (a 23- or 25-hour day is the zone rules' business), and per device the effective value and the order key,
    /// <c>shed_priority ?? v_eff</c>. Null without an <c>economics:</c> section.
    /// </summary>
    private EconomicsContext? ObserveEconomics(DispatchSetup setup, DateTimeOffset now, PriceInputs? priceInputs)
    {
        _fallbackChanges = [];
        if (setup.Economics is not { } economics)
        {
            ResetFallbackTracking();
            return null;
        }

        var zone = _time.LocalTimeZone;
        if (_tariffs is not { } cached || !ReferenceEquals(cached.Setup, economics) || cached.ZoneId != zone.Id)
        {
            _tariffs = (
                economics,
                zone.Id,
                economics.Tariff is { } import ? new Tariff(import, economics.Holidays, zone) : null,
                economics.ExportPrice is { } export ? new Tariff(export, economics.Holidays, zone) : null);
        }

        var (_, _, importTariff, exportTariff) = _tariffs.Value;
        var prices = PriceBook.At(now, importTariff, exportTariff, priceInputs);
        TrackFallback(economics, prices);
        var devices = new Dictionary<DeviceName, DeviceEcon>();
        foreach (var device in economics.Devices)
        {
            var value = EffectiveValue.At(device.Value, prices);
            var key = setup.Device(device.Device)?.Spec.ShedPriority ?? value.PerKwh;
            devices[device.Device] = new DeviceEcon(device, value, key);
        }

        return new EconomicsContext(economics, now, importTariff, prices, devices, _classes, priceInputs);
    }

    /// <summary>
    /// A dynamic price entering or leaving its fallback is reported once per change (the runner logs it): no decision and no reason
    /// code, the basis of the price is in the decision inputs. A static price never is at a fallback.
    /// </summary>
    private void TrackFallback(EconomicsSetup economics, PriceBook prices)
    {
        var changes = ImmutableArray.CreateBuilder<TariffFallbackChange>();
        Track("import", economics.Tariff, prices.Import, ref _importInFallback, changes);
        Track("export", economics.ExportPrice, prices.Export, ref _exportInFallback, changes);
        _fallbackChanges = changes.ToImmutable();

        static void Track(string name, PriceSpec? spec, PricePoint? point, ref bool inFallback, ImmutableArray<TariffFallbackChange>.Builder changes)
        {
            var now = spec is DynamicPrice && point?.Basis == PriceBasis.Fallback;
            if (now != inFallback && spec is DynamicPrice dynamic)
            {
                changes.Add(new TariffFallbackChange(name, dynamic.Signal.Value, now));
            }

            inFallback = now;
        }
    }

    private void ResetFallbackTracking()
    {
        _importInFallback = false;
        _exportInFallback = false;
    }

    /// <summary>
    /// The shed key of the start order while values follow prices: the adopted tier of the device, best tier = highest key. The
    /// tiers are adopted by the DISP-10 rule (<see cref="ValueOrder"/>), so a price tick moves nothing until it has held for
    /// economics.order_stability_cycles boundaries and moved two keys apart by economics.order_min_gap_fraction.
    /// </summary>
    private Func<DeviceSpec, double>? OrderKey()
    {
        if (_economics is not { Setup.OrderFollowsPrices: true } economics)
        {
            return null;
        }

        var tiers = _valueOrder.Adopted.IsEmpty
            ? ValueOrder.Propose(economics.Keys(), economics.Setup.Parameters.OrderMinGapFraction)
            : _valueOrder.Adopted;
        return spec => tiers.TryGetValue(spec.Id, out var tier) ? -tier : double.NegativeInfinity;
    }

    /// <summary>One device's value and order key at an instant.</summary>
    private sealed record DeviceEcon(DeviceEconomics Spec, EffectiveValue VEff, double OrderKey);

    /// <summary>The economics of one step, read by the allowance gate, the order and the exposure.</summary>
    private sealed class EconomicsContext(EconomicsSetup setup, DateTimeOffset now, Tariff? importTariff, PriceBook prices, Dictionary<DeviceName, DeviceEcon> devices, Dictionary<DeviceName, EnergyClass?> classes, PriceInputs? priceInputs)
    {
        private TariffChange? _next;
        private bool _nextKnown;

        public EconomicsSetup Setup => setup;

        public DateTimeOffset Now => now;

        public PriceBook Prices => prices;

        /// <summary>The tariff zone now; null for a flat or absent tariff.</summary>
        public string? Zone => prices.Import?.Zone;

        /// <summary>The first minute whose zone differs from now (only evaluated when a start asks for the zone-end margin).</summary>
        public TariffChange? NextChange
        {
            get
            {
                if (!_nextKnown)
                {
                    _next = importTariff?.NextChange(now, priceInputs);
                    _nextKnown = true;
                }

                return _next;
            }
        }

        public Dictionary<DeviceName, EnergyClass?> Classes => classes;

        public DeviceEcon? Device(DeviceName name) => devices.GetValueOrDefault(name);

        public IReadOnlyDictionary<DeviceName, double> Keys() => devices.ToDictionary(d => d.Key, d => d.Value.OrderKey);

        public EconomicsDecisionInputs? Block(DeviceName name, EnergyClass? energyClass) =>
            Device(name) is { } device
                ? new EconomicsDecisionInputs(
                    Zone,
                    prices.Import?.PerKwh,
                    prices.Export?.PerKwh,
                    PriceBasisOf(prices),
                    device.VEff.PerKwh,
                    device.VEff.Basis,
                    device.OrderKey,
                    energyClass,
                    device.Spec.Sources?.ToText(),
                    setup.Settlement.Id)
                : null;

        /// <summary>
        /// The one basis a decision records. A dynamic price must never be hidden behind a static one: with either price at its
        /// fallback the basis is fallback, else with either live it is live, else it is the import price's (else the export price's).
        /// </summary>
        private static PriceBasis? PriceBasisOf(PriceBook prices) =>
            prices.Import?.Basis == PriceBasis.Fallback || prices.Export?.Basis == PriceBasis.Fallback
                ? PriceBasis.Fallback
                : prices.Import?.Basis == PriceBasis.Live || prices.Export?.Basis == PriceBasis.Live
                    ? PriceBasis.Live
                    : prices.Import?.Basis ?? prices.Export?.Basis;

        public EconomicsView ToView() => new(
            Zone,
            prices.Import,
            prices.Export,
            setup.Settlement.Id,
            [.. devices.Values.Select(d => new DeviceEconomicsView(d.Spec.Device, d.VEff, d.OrderKey, d.Spec.Sources, Classes.GetValueOrDefault(d.Spec.Device)))]);
    }
}
