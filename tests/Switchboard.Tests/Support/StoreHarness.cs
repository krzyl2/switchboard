using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// Drives a <see cref="SignalStore"/> directly on a virtual clock: push states as Home Assistant
/// would, answer probes, read the snapshot. Units default to the declared ones.
/// </summary>
internal sealed class StoreHarness
{
    public static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public StoreHarness(string yaml, bool connect = true, TimeSpan? probeLatency = null)
    {
        Store = new SignalStore(new SignalStoreSettings(probeLatency ?? TimeSpan.FromSeconds(5)));
        Configuration = TestConfigurations.Parse(yaml);
        Store.ApplyConfiguration(Configuration, Now);
        if (connect)
        {
            Store.ApplySourceStatus(SignalSourceStatus.Connected);
        }
    }

    public FakeTimeProvider Time { get; } = new(Start);

    public SignalStore Store { get; }

    public ConfigurationResult Configuration { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public void Advance(TimeSpan by) => Time.Advance(by);

    public void AdvanceTo(DateTimeOffset when) => Time.Advance(when - Now);

    /// <summary>A pushed state change: last_updated and the report time both default to now.</summary>
    public void Push(string entity, string state, DateTimeOffset? at = null, string? unit = Declared, BatchOrigin origin = BatchOrigin.Change) =>
        Store.Apply(EntityUpdateBatch.Of(origin, Update(entity, state, at ?? Now, at ?? Now, unit)), Now);

    /// <summary>A pushed state of a series entity: the whitelisted attribute as the transport parsed it travels with the state.</summary>
    public void PushSeries(string entity, string state, ParsedSeries series, DateTimeOffset? at = null, string? unit = null) =>
        Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Change, new EntityUpdate(EntityId.Parse(entity), state, unit, at ?? Now, at ?? Now) { Series = series }), Now);

    /// <summary>A probe answer: the state identified by <paramref name="lastUpdated"/>, confirmed at <paramref name="reported"/>.</summary>
    public void ProbeAnswer(string entity, string state, DateTimeOffset lastUpdated, DateTimeOffset reported, string? unit = Declared) =>
        Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Probe, Update(entity, state, lastUpdated, reported, unit)), Now);

    public void Initial(params (string Entity, string State, DateTimeOffset LastUpdated)[] states) =>
        Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Initial, [.. states.Select(s => Update(s.Entity, s.State, s.LastUpdated, s.LastUpdated, Declared))]), Now);

    public SignalSnapshot Snapshot() => Store.Snapshot(Now);

    public SignalReading Reading(string name) => Snapshot().Signals.Single(s => s.Name?.Value == name);

    public PhaseProtection Protection(Phase phase) => Snapshot().Protection.For(phase)!;

    public PhaseDispatch Dispatch(Phase phase) => Snapshot().Dispatch.For(phase)!;

    public IReadOnlyList<string> DueProbes() => [.. Store.TakeDueProbes(Now).Select(e => e.Value)];

    /// <summary>Marker for "use the unit the configuration declares for this entity".</summary>
    public const string Declared = "\u0000declared";

    private EntityUpdate Update(string entity, string state, DateTimeOffset lastUpdated, DateTimeOffset reported, string? unit)
    {
        var id = EntityId.Parse(entity);
        var resolvedUnit = unit == Declared
            ? Configuration.Configuration!.Signals.Single(s => s.Entity == id).Unit
            : unit;
        return new EntityUpdate(id, state, resolvedUnit, lastUpdated, reported);
    }
}
