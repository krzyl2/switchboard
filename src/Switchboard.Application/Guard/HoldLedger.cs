using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Guard;

internal readonly record struct StageKey(DeviceName Device, int Stage);

/// <summary>What protection holds one stage to, why, and which phase (circuit group) produced it.</summary>
internal sealed record Limit(GuardTarget Target, ReasonCode Reason, Phase? Phase, GroupName? Group);

/// <summary>
/// The holds the guard has put on device stages: the one table of what protection currently limits.
/// It decides only whether a hold changes the table (never relaxing Off to AtMinimum, nothing for the
/// same target and reason); the engine builds the intent that goes with a change, in the same call,
/// so the table and the intents of one evaluation never disagree. Not thread safe: the engine's one
/// caller drives it.
/// </summary>
internal sealed class HoldLedger
{
    private readonly Dictionary<StageKey, Limit> _limits = [];

    public Limit? Find(DeviceName device, int stage) => _limits.GetValueOrDefault(new StageKey(device, stage));

    /// <summary>Records a hold; false (nothing changed) when the stage is already held to this target for this reason, or Off would be relaxed to AtMinimum.</summary>
    public bool Hold(StageKey stage, GuardTarget target, ReasonCode reason, ConstraintKey? source)
    {
        if (_limits.TryGetValue(stage, out var held) && (held.Target == GuardTarget.Off && target == GuardTarget.AtMinimum || (held.Target == target && held.Reason == reason)))
        {
            return false;
        }

        _limits[stage] = new Limit(target, reason, source?.Phase, source?.Group);
        return true;
    }

    /// <summary>The held stages in the order they are released: by device, then stage (a snapshot; releasing removes from the table).</summary>
    public List<StageKey> Held() => [.. _limits.Keys.OrderBy(k => k.Device).ThenBy(k => k.Stage)];

    public GroupName? GroupOf(StageKey stage) => _limits[stage].Group;

    public void Release(StageKey stage) => _limits.Remove(stage);

    /// <summary>Drops the holds of stages the setup no longer declares (they can never be commanded again) and returns them.</summary>
    public List<StageKey> DropUndeclared(ProtectionSetup setup)
    {
        var gone = _limits.Keys.Where(k => setup.Device(k.Device) is not { } d || k.Stage > d.Stages.Length).ToList();
        foreach (var key in gone)
        {
            _limits.Remove(key);
        }

        return gone;
    }

    public ImmutableDictionary<int, StageLimit> LimitsOf(DeviceName device) =>
        _limits
            .Where(l => l.Key.Device == device && l.Value.Target != GuardTarget.Released)
            .ToImmutableDictionary(l => l.Key.Stage, l => l.Value.Target == GuardTarget.Off ? StageLimit.Off : StageLimit.AtMinimum);

    public ImmutableDictionary<DeviceName, ImmutableDictionary<int, StageLimit>> Current() =>
        _limits
            .Where(l => l.Value.Target != GuardTarget.Released)
            .GroupBy(l => l.Key.Device)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableDictionary(l => l.Key.Stage, l => l.Value.Target == GuardTarget.Off ? StageLimit.Off : StageLimit.AtMinimum));
}
