using System.Collections.Frozen;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Configuration;

/// <summary>
/// How the file spells an enum, in one place: the tables more than one section reads (a breaker characteristic, a phase, whether a state reports by age or on
/// change) and the builder every other table is made with. A second copy of a table is a second spelling that can drift from the first.
/// </summary>
internal static class ConfigSpelling
{
    public static readonly FrozenDictionary<string, BreakerCharacteristic> Characteristics = Of(
        ("B", BreakerCharacteristic.B), ("C", BreakerCharacteristic.C), ("unknown", BreakerCharacteristic.Unknown));

    public static readonly FrozenDictionary<string, Phase> Phases = Of(("L1", Phase.L1), ("L2", Phase.L2), ("L3", Phase.L3));

    public static readonly FrozenDictionary<string, StateFreshness> StateReports = Of(("age", StateFreshness.Age), ("on_change", StateFreshness.OnChange));

    /// <summary>A table of spellings; the keys are compared exactly (case and all), as the file's keys are.</summary>
    public static FrozenDictionary<string, T> Of<T>(params (string Spelling, T Value)[] spellings) =>
        spellings.ToFrozenDictionary(s => s.Spelling, s => s.Value, StringComparer.Ordinal);
}
