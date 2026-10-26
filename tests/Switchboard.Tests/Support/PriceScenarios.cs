using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Support;

/// <summary>
/// Dispatch scenarios with a price entity (14-ECONOMICS, slice 4): the document gets one <c>kind: price</c> signal, and a real
/// <see cref="SignalStore"/> on the same document turns what the entity says into the <see cref="Switchboard.Domain.Economics.PriceInputs"/>
/// the scenario hands to dispatch, so the signal layer's rules (freshness from the report time, unit and label, unavailable and unknown)
/// are the ones under test, not a stand-in.
/// </summary>
internal sealed class PriceScenarios
{
    public const string Entity = "sensor.price_entity";
    public const string Signal = "market_price";

    private readonly SignalStore _store = new(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
    private readonly GuardScenario _scenario;

    private PriceScenarios(GuardScenario scenario, ConfigurationResult configuration)
    {
        _scenario = scenario;
        Configuration = configuration;
        _store.ApplyConfiguration(configuration, scenario.Now);
        _store.ApplySourceStatus(SignalSourceStatus.Connected);
    }

    public ConfigurationResult Configuration { get; }

    /// <summary>The signal under test: a per-MWh market price (the unit most markets publish), fresh for two minutes.</summary>
    public static string SignalYaml(string unit = "PLN/MWh", string maxAge = "2m", string role = "price_export") =>
        $"  {Signal}:\n    entity: {Entity}\n    kind: price\n    unit: {unit}\n    max_age: {maxAge}\n    role: {role}\n";

    /// <summary>The scenario's whole document: the dispatch devices, <paramref name="economics"/>, and the price signal in <c>signals:</c>.</summary>
    public static string Document(string devices, string economics, string signal, string control = OneBoundary)
    {
        const string Marker = "\nsignals:\n";
        var document = TestDevices.Document(devices, control, extra: "\n" + economics);
        var at = document.IndexOf(Marker, StringComparison.Ordinal);
        return document.Insert(at + Marker.Length, signal);
    }

    /// <param name="off">The devices reported off; the rest start on.</param>
    public static (GuardScenario Scenario, PriceScenarios Prices) Create(string devices, DateTimeOffset at, string[] off, string economics, string? signal = null, string control = OneBoundary)
    {
        var yaml = Document(devices, economics, signal ?? SignalYaml(), control);
        var s = new GuardScenario(yaml, start: at, mode: null, settledLockouts: false, dispatch: true);
        foreach (var name in off)
        {
            var device = s.Setup.Device(Switchboard.Domain.Devices.DeviceName.Parse(name))!;
            foreach (var stage in device.Stages)
            {
                s.SetState(device.Id, stage.Index, device.IsSetpoint ? new Switchboard.Domain.Devices.SetpointApplied(device.SetpointFloor) : new Switchboard.Domain.Devices.RelayApplied(false));
            }
        }

        s.Lockouts.Observe(s.Setup, [.. s.Inputs().DeviceStates], s.Now - TimeSpan.FromHours(1));
        s.Time.Zone = Warsaw;
        return (s, new PriceScenarios(s, TestConfigurations.Valid(yaml)));
    }

    /// <summary>The entity reports <paramref name="state"/> now (its report time is the scenario's clock); the unit is the one the signal declares unless given.</summary>
    public void Report(string state, string? unit = null, string? entity = null)
    {
        var now = _scenario.Now;
        var declared = Configuration.Configuration!.Signals.Single(s => s.Name.Value == Signal).Unit;
        _store.Apply(
            EntityUpdateBatch.Of(BatchOrigin.Change, new EntityUpdate(EntityId.Parse(entity ?? Entity), state, unit ?? declared, now, now)),
            now);
    }

    /// <summary>A repeat of the same state with a newer report time: freshness only (SIG-06), as Home Assistant does for a value that did not change.</summary>
    public void Confirm(string state, string? unit = null)
    {
        var now = _scenario.Now;
        var declared = Configuration.Configuration!.Signals.Single(s => s.Name.Value == Signal).Unit;
        var held = _store.Snapshot(now).Signals.Single(s => s.Name?.Value == Signal);
        _store.Apply(EntityUpdateBatch.Of(BatchOrigin.Probe, new EntityUpdate(EntityId.Parse(Entity), state, unit ?? declared, held.LastReported ?? now, now)), now);
    }

    /// <summary>Hands the store's price view at the scenario's clock to dispatch: call it at every poll (the price ages with the clock).</summary>
    public void Refresh() => _scenario.Prices = _store.Snapshot(_scenario.Now).Prices;

    /// <summary>The balance feed of <see cref="EconomicsScenarios.Feed"/> and the price refresh together.</summary>
    public Action FeedWith(double? wh) => () =>
    {
        Feed(_scenario, wh)();
        Refresh();
    };

    public SignalReading Reading() => _store.Snapshot(_scenario.Now).Signals.Single(s => s.Name?.Value == Signal);

    /// <summary>Runs <paramref name="minutes"/> with the balance and the prices fed and the phases quiet.</summary>
    public void Run(double minutes, double? wh = 600)
    {
        var feed = FeedWith(wh);
        feed();
        _scenario.PollUntil(_scenario.Now + TimeSpan.FromMinutes(minutes), 1, 1, 1, feed);
    }
}
