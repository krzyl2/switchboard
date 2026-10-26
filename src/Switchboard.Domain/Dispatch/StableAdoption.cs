using System.Collections.Immutable;
using Switchboard.Domain.Devices;

namespace Switchboard.Domain.Dispatch;

/// <summary>
/// The adoption rule of DISP-10 as one mechanism: a proposal replaces what is adopted only after
/// <c>stabilityCycles</c> consecutive cycle boundaries proposing exactly it, so a one-boundary blip (a kettle on
/// somewhere, a price tick) moves nothing. The first proposal is adopted at once, there is nothing to flap from.
/// <see cref="PhaseRanking"/> and <see cref="ValueOrder"/> both stand on it.
/// </summary>
/// <typeparam name="T">What is proposed: a phase ranking, a tier assignment.</typeparam>
public sealed class StableAdoption<T>
{
    private readonly T _empty;
    private readonly IEqualityComparer<T> _same;
    private readonly Func<T, bool> _isEmpty;
    private T _candidate;
    private int _streak;

    /// <param name="empty">What is adopted before the first proposal.</param>
    /// <param name="same">When two proposals are "exactly it".</param>
    /// <param name="isEmpty">Whether the adopted value is the empty one: the next proposal is then adopted at once.</param>
    public StableAdoption(T empty, IEqualityComparer<T> same, Func<T, bool> isEmpty)
    {
        ArgumentNullException.ThrowIfNull(same);
        ArgumentNullException.ThrowIfNull(isEmpty);
        _empty = empty;
        _same = same;
        _isEmpty = isEmpty;
        _candidate = empty;
        Adopted = empty;
    }

    public T Adopted { get; private set; }

    /// <summary>Counts one boundary's proposal; returns what is in force after it.</summary>
    public T Observe(T proposed, int stabilityCycles)
    {
        if (_isEmpty(Adopted))
        {
            Adopted = proposed;
            _candidate = proposed;
            _streak = 0;
            return Adopted;
        }

        if (_same.Equals(proposed, Adopted))
        {
            _candidate = proposed;
            _streak = 0;
            return Adopted;
        }

        _streak = _same.Equals(proposed, _candidate) ? _streak + 1 : 1;
        _candidate = proposed;
        if (_streak >= stabilityCycles)
        {
            Adopted = proposed;
            _streak = 0;
        }

        return Adopted;
    }

    /// <summary>A new configuration or a restart: the next proposal is adopted at once.</summary>
    public void Reset()
    {
        Adopted = _empty;
        _candidate = _empty;
        _streak = 0;
    }
}

/// <summary>
/// The order of value in which dispatch gives energy out when the keys follow prices (14-ECONOMICS, "How value feeds the
/// merit order"): tiers of devices, best first. Devices whose keys differ by less than a fraction of the key (measured from the
/// best key of the tier) share a tier and so tie, so a price tick that moves two keys a little does not reshuffle them; the
/// ordinary ties (reaction time, rotation, name) then decide inside the tier. A new tier assignment is adopted by the
/// <see cref="StableAdoption{T}"/> rule, the same as the phase ranking. The guard never sees it: its shed order stays static.
/// </summary>
public sealed class ValueOrder
{
    private readonly StableAdoption<ImmutableDictionary<DeviceName, int>> _adoption = new(
        ImmutableDictionary<DeviceName, int>.Empty,
        new TierComparer(),
        tiers => tiers.IsEmpty);

    /// <summary>Tier index per device, 0 the most valuable; empty before the first proposal.</summary>
    public ImmutableDictionary<DeviceName, int> Adopted => _adoption.Adopted;

    /// <param name="keys">order_key per device: the shed priority where one is set, else the effective value now.</param>
    /// <param name="gapFraction">Keys closer than this fraction of the tier's best key share a tier (economics.order_min_gap_fraction).</param>
    public static ImmutableDictionary<DeviceName, int> Propose(IReadOnlyDictionary<DeviceName, double> keys, double gapFraction)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var sorted = keys.OrderByDescending(k => k.Value).ThenBy(k => k.Key).ToList();
        var tiers = ImmutableDictionary.CreateBuilder<DeviceName, int>();
        var tier = 0;
        var anchor = 0.0;
        for (var i = 0; i < sorted.Count; i++)
        {
            var key = sorted[i].Value;
            if (i == 0)
            {
                anchor = key;
            }
            else
            {
                var distance = anchor - key;
                var scale = Math.Max(Math.Abs(anchor), Math.Abs(key));
                if (distance > 0 && distance >= gapFraction * scale)
                {
                    tier++;
                    anchor = key;
                }
            }

            tiers[sorted[i].Key] = tier;
        }

        return tiers.ToImmutable();
    }

    /// <summary>Counts one boundary's keys; returns the tiers in force after it.</summary>
    public ImmutableDictionary<DeviceName, int> Observe(IReadOnlyDictionary<DeviceName, double> keys, double gapFraction, int stabilityCycles) =>
        _adoption.Observe(Propose(keys, gapFraction), stabilityCycles);

    /// <summary>A new configuration or a restart: the next proposal is adopted at once.</summary>
    public void Reset() => _adoption.Reset();

    private sealed class TierComparer : IEqualityComparer<ImmutableDictionary<DeviceName, int>>
    {
        public bool Equals(ImmutableDictionary<DeviceName, int>? x, ImmutableDictionary<DeviceName, int>? y) =>
            x is not null && y is not null && x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var tier) && tier == p.Value);

        public int GetHashCode(ImmutableDictionary<DeviceName, int> obj) => obj.Count;
    }
}
