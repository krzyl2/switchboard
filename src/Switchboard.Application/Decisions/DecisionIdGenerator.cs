using System.Globalization;

namespace Switchboard.Application.Decisions;

/// <summary>
/// Decision ids: the process's boot id plus a monotonic sequence. Unlike a timestamp they stay
/// unique across clock steps and restarts; ordering and paging never rely on them.
/// </summary>
public sealed class DecisionIdGenerator
{
    private readonly string _bootId;
    private long _sequence;

    /// <param name="bootId">A random id chosen at process start (lower-case letters and digits).</param>
    public DecisionIdGenerator(string bootId)
    {
        ArgumentException.ThrowIfNullOrEmpty(bootId);
        if (!bootId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("A boot id is letters and digits only.", nameof(bootId));
        }

        _bootId = bootId;
    }

    public string BootId => _bootId;

    public string Next() => string.Create(CultureInfo.InvariantCulture, $"{_bootId}-{Interlocked.Increment(ref _sequence):D9}");
}
