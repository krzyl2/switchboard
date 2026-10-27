using System.Collections.Immutable;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Reads the optional top-level <c>planner:</c> (15-PLANNER, Configuration): data only, the algorithm parameters live in
/// <c>control.planner</c>. Absent (or empty of a value) means the planner is off and the file is a 0.10 file; nothing here changes a decision
/// in this slice, whatever <c>dispatch</c> says.
/// </summary>
internal static class PlannerSectionParser
{
    public const string Key = "planner";

    private static readonly string[] SectionKeys = ["dispatch"];

    private static readonly ImmutableDictionary<string, PlannerDispatchMode> Modes = new Dictionary<string, PlannerDispatchMode>
    {
        ["off"] = PlannerDispatchMode.Off,
        ["narrow"] = PlannerDispatchMode.Narrow,
        ["full"] = PlannerDispatchMode.Full,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public static PlannerDefinition? Read(ConfigMapping root, ConfigIssues issues)
    {
        if (root.Find(Key) is not { } entry || ConfigRead.IsNull(entry.Value))
        {
            return null;
        }

        if (ConfigRead.Mapping(entry.Value, Key, issues) is not { } mapping)
        {
            return null;
        }

        var errorsBefore = issues.Errors.Count;
        ConfigRead.CheckKeys(mapping, Key, SectionKeys, issues);
        var dispatch = ConfigRead.Optional(mapping, "dispatch") is { } node
            ? ConfigRead.Choice(node, ConfigRead.Join(Key, "dispatch"), issues, Modes)
            : PlannerDispatchMode.Off;
        return issues.Errors.Count > errorsBefore || dispatch is null ? null : new PlannerDefinition(dispatch.Value);
    }
}
