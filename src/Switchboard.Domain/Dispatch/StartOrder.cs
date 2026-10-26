using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Dispatch;

/// <summary>
/// The order dispatch gives headroom in (DISP-03): the exact reverse of the shed order
/// (<see cref="Guard.ShedOrder.Rank"/>: value per kWh or shed priority, then reaction time, then
/// name), so what is shed first is restored last. Identical elements of one rotation group tie on
/// every key; among them the adopted phase ranking decides (DISP-10): the element on the phase with
/// the most headroom starts first, and is shed last.
/// </summary>
public static class StartOrder
{
    /// <param name="rotationGroup">The rotation group of a device, or null.</param>
    /// <param name="ranking">The adopted phase ranking, most headroom first.</param>
    /// <param name="key">
    /// The shed key of a device, ascending = shed first (so started last). Null is the static key of the guard's shed order
    /// (<see cref="DeviceSpec.ShedKey"/>); dispatch passes a live one when values follow prices (14-ECONOMICS). The guard
    /// never calls this: its shed order stays <see cref="Guard.ShedOrder.Rank"/>.
    /// </param>
    public static ImmutableArray<DeviceSpec> For(IEnumerable<DeviceSpec> devices, Func<DeviceName, GroupName?> rotationGroup, ImmutableArray<Phase> ranking, Func<DeviceSpec, double>? key = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(rotationGroup);
        var shedKey = key ?? (d => d.ShedKey);
        var list = devices.Where(d => !d.NoShed).ToList();
        list.Sort((a, b) => ShedComparison(a, b, rotationGroup, ranking, shedKey));
        list.Reverse();
        return [.. list, .. devices.Where(d => d.NoShed).OrderBy(d => d.Id)];
    }

    /// <summary>The shed order of 04 §2 with the rotation tie-break: ascending value, faster first, then the phase ranked last first.</summary>
    private static int ShedComparison(DeviceSpec a, DeviceSpec b, Func<DeviceName, GroupName?> rotationGroup, ImmutableArray<Phase> ranking, Func<DeviceSpec, double> shedKey)
    {
        var byKey = shedKey(a).CompareTo(shedKey(b));
        if (byKey != 0)
        {
            return byKey;
        }

        var byReaction = a.ReactionTime.CompareTo(b.ReactionTime);
        if (byReaction != 0)
        {
            return byReaction;
        }

        if (rotationGroup(a.Id) is { } group && rotationGroup(b.Id) == group)
        {
            // Shed first = ranked last: the reverse puts the best-ranked phase at the start.
            var byPhase = Rank(b, ranking).CompareTo(Rank(a, ranking));
            if (byPhase != 0)
            {
                return byPhase;
            }
        }

        return a.Id.CompareTo(b.Id);
    }

    private static int Rank(DeviceSpec device, ImmutableArray<Phase> ranking)
    {
        var index = ranking.IsDefault ? -1 : ranking.IndexOf(device.Phases[0]);
        return index < 0 ? int.MaxValue : index;
    }
}

/// <summary>
/// Phase priority for multi-element loads (04 §5, DISP-10), stabilised: a proposed ranking is
/// adopted only after <c>stabilityCycles</c> consecutive cycle boundaries proposing exactly it, so
/// a kettle switching on somewhere for one cycle does not move a heater between phases. The first
/// proposal is adopted at once (there is nothing to flap from).
/// </summary>
public sealed class PhaseRanking
{
    private readonly StableAdoption<ImmutableArray<Phase>> _adoption = new([], new RankingComparer(), ranking => ranking.IsEmpty);

    public ImmutableArray<Phase> Adopted => _adoption.Adopted;

    /// <summary>Phases by descending headroom (E), phase order on a tie: the most loaded phase is relieved first.</summary>
    public static ImmutableArray<Phase> Propose(IReadOnlyDictionary<Phase, double> headroom)
    {
        ArgumentNullException.ThrowIfNull(headroom);
        return [.. headroom.OrderByDescending(h => h.Value).ThenBy(h => h.Key).Select(h => h.Key)];
    }

    /// <summary>Counts one boundary's proposal; returns the ranking in force after it.</summary>
    public ImmutableArray<Phase> Observe(ImmutableArray<Phase> proposed, int stabilityCycles) => _adoption.Observe(proposed, stabilityCycles);

    /// <summary>A new configuration or a restart: the next proposal is adopted at once.</summary>
    public void Reset() => _adoption.Reset();

    private sealed class RankingComparer : IEqualityComparer<ImmutableArray<Phase>>
    {
        public bool Equals(ImmutableArray<Phase> x, ImmutableArray<Phase> y) => x.SequenceEqual(y);

        public int GetHashCode(ImmutableArray<Phase> obj) => obj.Length;
    }
}
