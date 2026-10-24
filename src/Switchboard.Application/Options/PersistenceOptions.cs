using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>
/// Bound from <c>Switchboard:Persistence</c>: how runtime state (state.json) and the decision log
/// are written. Both are written by their own writer tasks; the guard never waits for a disk.
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Switchboard:Persistence";

    /// <summary>Without a threshold crossing or a kill switch change, state.json is written at most this often, and only when something changed.</summary>
    public required TimeSpan StateSaveInterval { get; init; }

    /// <summary>A budget drop of at least this much (fraction of 1) since the last save is written at once.</summary>
    public required double StateBudgetSaveStep { get; init; }

    /// <summary>A larger state.json is refused unread and replaced: the file is untrusted input.</summary>
    public required int StateMaxFileBytes { get; init; }

    /// <summary>Recent decisions kept in memory for the API (reloaded from the newest daily files at start).</summary>
    public required int DecisionMemoryCapacity { get; init; }

    /// <summary>Decisions held while the log cannot be written; beyond it the oldest are dropped and counted.</summary>
    public required int DecisionPendingCapacity { get; init; }

    /// <summary>The largest page GET api/decisions answers.</summary>
    public required int DecisionMaxPageSize { get; init; }

    /// <summary>After a failed write (disk full, read-only), both writers try again this much later.</summary>
    public required TimeSpan WriteRetryInterval { get; init; }

    /// <summary>
    /// Limit for the guard's stop: shutdown safe state recorded, decision log flushed, state.json
    /// saved. It runs before the MQTT and Home Assistant links close, inside the container's stop grace.
    /// </summary>
    public required TimeSpan ShutdownTimeout { get; init; }
}

public sealed class PersistenceOptionsValidator : IValidateOptions<PersistenceOptions>
{
    private const int MinStateBytes = 4096;
    private const int MaxStateBytes = 1024 * 1024;
    private const int MaxDecisionCapacity = 100_000;

    public ValidateOptionsResult Validate(string? name, PersistenceOptions options)
    {
        const string section = PersistenceOptions.SectionName;
        var failures = new List<string>();

        OptionsValidation.RequirePositive(failures, $"{section}:StateSaveInterval", options.StateSaveInterval);
        if (!(options.StateBudgetSaveStep > 0 && options.StateBudgetSaveStep <= 1))
        {
            failures.Add($"{section}:StateBudgetSaveStep must be greater than 0 and at most 1.");
        }

        if (options.StateMaxFileBytes < MinStateBytes || options.StateMaxFileBytes > MaxStateBytes)
        {
            failures.Add($"{section}:StateMaxFileBytes must be between {MinStateBytes} and {MaxStateBytes}.");
        }

        RequireCount(failures, $"{section}:DecisionMemoryCapacity", options.DecisionMemoryCapacity);
        RequireCount(failures, $"{section}:DecisionPendingCapacity", options.DecisionPendingCapacity);
        RequireCount(failures, $"{section}:DecisionMaxPageSize", options.DecisionMaxPageSize);
        if (options.DecisionMaxPageSize > options.DecisionMemoryCapacity)
        {
            failures.Add($"{section}:DecisionMaxPageSize must not exceed DecisionMemoryCapacity.");
        }

        OptionsValidation.RequirePositive(failures, $"{section}:WriteRetryInterval", options.WriteRetryInterval);
        OptionsValidation.RequirePositive(failures, $"{section}:ShutdownTimeout", options.ShutdownTimeout);
        return OptionsValidation.Result(failures);
    }

    private static void RequireCount(List<string> failures, string key, int value)
    {
        if (value < 1 || value > MaxDecisionCapacity)
        {
            failures.Add($"{key} must be between 1 and {MaxDecisionCapacity}.");
        }
    }
}
