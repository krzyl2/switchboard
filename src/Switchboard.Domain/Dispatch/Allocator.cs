using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Dispatch;

/// <summary>One device in an allocation, in start order.</summary>
/// <param name="Constraints">Every main phase and circuit-group phase the device draws on.</param>
/// <param name="Basis">Its current target (or applied state when no target is set): what an increase or decrease is measured against.</param>
/// <param name="Levels">The levels it may be put at, ascending (<see cref="DeviceLevels.All"/>).</param>
/// <param name="MinAllowed">Index of the lowest level reachable now (a min_on lockout keeps a relay on; a modulating device without a stop path keeps its minimum).</param>
/// <param name="MaxAllowed">Index of the highest level reachable now: above the basis only when an increase is eligible this cycle.</param>
public sealed record AllocationDevice(
    DeviceSpec Spec,
    ImmutableArray<ConstraintKey> Constraints,
    DeviceLevel Basis,
    ImmutableArray<DeviceLevel> Levels,
    int MinAllowed,
    int MaxAllowed);

/// <param name="Available">A per constraint: E plus the estimates of the dispatchable devices on it, i.e. what dispatch may give its own devices.</param>
public sealed record AllocationRequest(
    ImmutableArray<AllocationDevice> Devices,
    ImmutableDictionary<ConstraintKey, Amperes> Available,
    Amperes StartHysteresis,
    Amperes StopHysteresis);

/// <summary>Why a device did not get its maximum.</summary>
public enum AllocationLimit
{
    /// <summary>It got its maximum level.</summary>
    None,

    /// <summary>Not enough headroom even if it came first.</summary>
    Headroom,

    /// <summary>Devices worth more per kWh took the headroom it would otherwise have had (value_rank_below_cutoff).</summary>
    Rank,
}

/// <param name="Wish">Index of the highest level that fits within [lowest, MaxAllowed]; the lowest when none fits.</param>
/// <param name="Uncapped">The same without the eligibility cap: what the device would take if it could increase now (confirmation counts on it).</param>
/// <param name="Allocated">max(Wish, MinAllowed): what is reserved for it, whatever a lockout lets happen.</param>
/// <param name="Headroom">What was left on each of its constraints when its turn came, before its own level.</param>
public sealed record DeviceAllocation(
    DeviceName Device,
    int Wish,
    int Uncapped,
    int Allocated,
    AllocationLimit Limit,
    ImmutableDictionary<ConstraintKey, Amperes> Headroom);

public sealed record AllocationResult(ImmutableArray<DeviceAllocation> Devices, ImmutableDictionary<ConstraintKey, Amperes> Remaining);

/// <summary>
/// Top-down allocation of 04 §3 (step 6, with step 7's hysteresis), pure. Devices are walked in
/// start order; each takes the highest level whose ceiling fits under what is left on every
/// constraint it draws on, less the start hysteresis for an increase or the (negative) stop
/// hysteresis for keeping or lowering its level:
/// <code>
/// fits(L) = ∀ k: Ceiling(L, k) ≤ remaining(k) − (Total(L) &gt; Total(basis) ? H_start : H_stop)
/// </code>
/// Ceilings are budgeted, not draws, so every allocated setpoint can be drawn at once and still
/// stay under the threshold (an idle charger cannot lend its headroom to a heater). What a device
/// cannot give up now (its <see cref="AllocationDevice.MinAllowed"/> level) is reserved before the
/// walk starts, so no device worth more can be planned into headroom a locked device still holds.
/// </summary>
public static class Allocator
{
    public static AllocationResult Allocate(AllocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var remaining = new Dictionary<ConstraintKey, double>();
        foreach (var device in request.Devices)
        {
            foreach (var key in device.Constraints)
            {
                remaining[key] = request.Available.TryGetValue(key, out var available) ? available.Value : double.NegativeInfinity;
            }
        }

        foreach (var device in request.Devices)
        {
            foreach (var key in device.Constraints)
            {
                remaining[key] -= Reserved(device, key);
            }
        }

        var alone = new Dictionary<ConstraintKey, double>(remaining);
        var results = ImmutableArray.CreateBuilder<DeviceAllocation>(request.Devices.Length);
        foreach (var device in request.Devices)
        {
            foreach (var key in device.Constraints)
            {
                remaining[key] += Reserved(device, key);
            }

            var top = device.Levels.Length - 1;
            var headroom = device.Constraints.ToImmutableDictionary(k => k, k => new Amperes(remaining[k]));
            var uncapped = Best(device, top, remaining, request);
            var wish = Best(device, Math.Min(device.MaxAllowed, top), remaining, request);
            var allocated = Math.Max(wish, device.MinAllowed);
            var limit = uncapped == top ? AllocationLimit.None
                : Best(device, top, Alone(device, alone), request) > uncapped ? AllocationLimit.Rank
                : AllocationLimit.Headroom;

            foreach (var key in device.Constraints)
            {
                remaining[key] -= DeviceLevels.Ceiling(device.Spec, device.Levels[allocated], key.Phase).Value;
            }

            results.Add(new DeviceAllocation(device.Spec.Id, wish, uncapped, allocated, limit, headroom));
        }

        return new AllocationResult(results.MoveToImmutable(), remaining.ToImmutableDictionary(r => r.Key, r => new Amperes(r.Value)));
    }

    private static double Reserved(AllocationDevice device, ConstraintKey key) =>
        DeviceLevels.Ceiling(device.Spec, device.Levels[device.MinAllowed], key.Phase).Value;

    private static Dictionary<ConstraintKey, double> Alone(AllocationDevice device, Dictionary<ConstraintKey, double> alone) =>
        device.Constraints.ToDictionary(k => k, k => alone[k] + Reserved(device, k));

    /// <summary>The highest index up to <paramref name="cap"/> that fits everywhere; 0 when none does.</summary>
    private static int Best(AllocationDevice device, int cap, IReadOnlyDictionary<ConstraintKey, double> remaining, AllocationRequest request)
    {
        var basis = DeviceLevels.Total(device.Spec, device.Basis);
        for (var i = cap; i > 0; i--)
        {
            var level = device.Levels[i];
            var hysteresis = DeviceLevels.Total(device.Spec, level) > basis ? request.StartHysteresis : request.StopHysteresis;
            if (device.Constraints.All(k => DeviceLevels.Ceiling(device.Spec, level, k.Phase).Value <= remaining[k] - hysteresis.Value))
            {
                return i;
            }
        }

        return 0;
    }
}
