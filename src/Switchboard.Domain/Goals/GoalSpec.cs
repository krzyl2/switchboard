using System.Globalization;
using System.Text;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Goals;

/// <summary>A goal number that is a literal in the file or read from a device-scoped signal; the entity behind a signal is not known here.</summary>
public sealed record GoalNumber(double? Literal, SignalName? Signal)
{
    public static GoalNumber Of(double value) => new(value, null);

    public static GoalNumber From(SignalName signal) => new(null, signal);

    public bool IsSignal => Signal is not null;
}

/// <summary>
/// When a goal is due: a recurring local time of day (<see cref="At"/>) or an instant read from a signal, less a margin (the
/// plan aims to finish that long before the deadline). A goal without a deadline has no such spec at all.
/// </summary>
public sealed record GoalDeadlineSpec(TimeOnly? At, SignalName? Signal, TimeSpan Margin);

/// <summary>
/// One device's <c>goal:</c> as the configuration reads it (15-PLANNER). It names signals, never entities, and holds no clock: what
/// is true at a moment is a <see cref="GoalRequest"/>.
/// </summary>
/// <param name="Hard">A hard deadline must be met (no value test, may escalate); a soft goal lapses at its deadline.</param>
/// <param name="LossFraction">Energy drawn = needed x (1 + loss_fraction), in [0, 1).</param>
/// <param name="CapacityKwh">A SoC goal's battery capacity.</param>
/// <param name="Soc">A SoC goal's progress input (percent).</param>
/// <param name="Target">The target: percent for a SoC goal, kWh for an energy goal.</param>
/// <param name="Deadline">Null means the horizon end: wants energy, cheapest first, whenever.</param>
/// <param name="Want">An optional gate (a state signal); off means no goal.</param>
/// <param name="Sources">The classes the planner may schedule the goal on, after narrowing to the tariff's zones; null means the device's.</param>
/// <param name="Escalate">A hard goal at risk may use any zone.</param>
public sealed record GoalSpec(
    DeviceName Device,
    GoalKind Kind,
    bool Hard,
    double LossFraction,
    double? CapacityKwh,
    GoalNumber? Soc,
    GoalNumber Target,
    GoalDeadlineSpec? Deadline,
    SignalName? Want,
    SourceSet? Sources,
    bool Escalate,
    GoalOnMet OnMet)
{
    /// <summary>A goal whose target, deadline or want comes from a mapped entity is an entity goal; one made of literals is a config goal.</summary>
    public GoalOrigin Origin => Target.IsSignal || Deadline?.Signal is not null || Want is not null ? GoalOrigin.Entity : GoalOrigin.Config;

    /// <summary>
    /// A fingerprint of what the goal is (FNV-1a over its canonical text): a plan entry made for another revision is not applied. It
    /// names signals, never entities, and does not move with the values read now.
    /// </summary>
    public long Revision
    {
        get
        {
            var text = new StringBuilder();
            void Add(string? part) => text.Append(part).Append('\n');
            Add(Device.Value);
            Add(Kind.ToString());
            Add(Hard ? "hard" : "soft");
            Add(LossFraction.ToString("R", CultureInfo.InvariantCulture));
            Add(CapacityKwh?.ToString("R", CultureInfo.InvariantCulture));
            Add(Soc?.Signal?.Value ?? Soc?.Literal?.ToString("R", CultureInfo.InvariantCulture));
            Add(Target.Signal?.Value ?? Target.Literal?.ToString("R", CultureInfo.InvariantCulture));
            Add(Deadline?.Signal?.Value ?? Deadline?.At?.ToString("HH:mm", CultureInfo.InvariantCulture));
            Add(Deadline?.Margin.ToString("c", CultureInfo.InvariantCulture));
            Add(Want?.Value);
            Add(Sources?.ToText());
            Add(Escalate ? "escalate" : "stay");
            Add(OnMet.ToString());

            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            var hash = offset;
            foreach (var b in Encoding.UTF8.GetBytes(text.ToString()))
            {
                hash = (hash ^ b) * prime;
            }

            return unchecked((long)hash);
        }
    }
}
