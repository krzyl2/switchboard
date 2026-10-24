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
    public static ImmutableArray<DeviceSpec> For(IEnumerable<DeviceSpec> devices, Func<DeviceName, GroupName?> rotationGroup, ImmutableArray<Phase> ranking)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(rotationGroup);
        var list = devices.Where(d => !d.NoShed).ToList();
        list.Sort((a, b) => ShedComparison(a, b, rotationGroup, ranking));
        list.Reverse();
        return [.. list, .. devices.Where(d => d.NoShed).OrderBy(d => d.Id)];
    }

    /// <summary>The shed order of 04 §2 with the rotation tie-break: ascending value, faster first, then the phase ranked last first.</summary>
    private static int ShedComparison(DeviceSpec a, DeviceSpec b, Func<DeviceName, GroupName?> rotationGroup, ImmutableArray<Phase> ranking)
    {
        var byKey = a.ShedKey.CompareTo(b.ShedKey);
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
    private ImmutableArray<Phase> _candidate = [];
    private int _streak;

    public ImmutableArray<Phase> Adopted { get; private set; } = [];

    /// <summary>Phases by descending headroom (E), phase order on a tie: the most loaded phase is relieved first.</summary>
    public static ImmutableArray<Phase> Propose(IReadOnlyDictionary<Phase, double> headroom)
    {
        ArgumentNullException.ThrowIfNull(headroom);
        return [.. headroom.OrderByDescending(h => h.Value).ThenBy(h => h.Key).Select(h => h.Key)];
    }

    /// <summary>Counts one boundary's proposal; returns the ranking in force after it.</summary>
    public ImmutableArray<Phase> Observe(ImmutableArray<Phase> proposed, int stabilityCycles)
    {
        if (Adopted.IsEmpty)
        {
            Adopted = proposed;
            _candidate = proposed;
            _streak = 0;
            return Adopted;
        }

        if (proposed.SequenceEqual(Adopted))
        {
            _candidate = proposed;
            _streak = 0;
            return Adopted;
        }

        _streak = proposed.SequenceEqual(_candidate) ? _streak + 1 : 1;
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
        Adopted = [];
        _candidate = [];
        _streak = 0;
    }
}
