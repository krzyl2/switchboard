using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>
/// Bound from <c>Switchboard:Setup</c>: operational timing and memory bounds of the setup wizard's
/// backend (discovery, candidate readouts, saving), never a control parameter. Leases, the link's
/// idle time and request timeouts run on the host clock; readout ages run on the signal clock.
/// </summary>
public sealed class SetupOptions
{
    public const string SectionName = "Switchboard:Setup";

    /// <summary>A discovery answer is reused this long unless the wizard asks for a refresh.</summary>
    public required TimeSpan DiscoveryCacheTtl { get; init; }

    /// <summary>The discovery link (a second, read-only Home Assistant connection) closes after this long without a setup request, lease or self-test.</summary>
    public required TimeSpan DiscoveryLinkIdleTimeout { get; init; }

    /// <summary>How long a discovery query, or the link's connection, may take before the wizard is told Home Assistant is unavailable.</summary>
    public required TimeSpan DiscoveryRequestTimeout { get; init; }

    /// <summary>At most this many entities are kept from one discovery; the answer says when it was truncated.</summary>
    public required int MaxDiscoveredEntities { get; init; }

    /// <summary>At most this many entities are subscribed for live readouts at once.</summary>
    public required int MaxReadoutEntities { get; init; }

    /// <summary>A readout stays subscribed this long after the wizard last asked for it.</summary>
    public required TimeSpan ReadoutLeaseTime { get; init; }

    /// <summary>
    /// Reports kept per candidate entity, for its refresh detection and the self-test: enough to span
    /// twice the self-test window (2 × 60 s) for a meter reporting every second.
    /// </summary>
    public required int ReadoutHistoryLength { get; init; }

    /// <summary>Candidate entities whose history is kept for the link's lifetime (least recently asked for go first).</summary>
    public required int MaxRetainedReadouts { get; init; }

    /// <summary>A subscribed candidate silent this long gets its last_reported read once (a repeated value pushes nothing).</summary>
    public required TimeSpan ReadoutProbeInterval { get; init; }

    /// <summary>Timestamped backups of switchboard.yaml kept next to it; the oldest beyond this are deleted.</summary>
    public required int MaxBackups { get; init; }

    /// <summary>After a save, how long the wizard waits for the running add-on to load the new file.</summary>
    public required TimeSpan ReloadConfirmTimeout { get; init; }

    /// <summary>Phase observations kept per switched entity (in memory and in phase-evidence.json), for the self-test verdict.</summary>
    public required int EvidencePerDevice { get; init; }

    /// <summary>A larger phase-evidence.json is refused unread (set aside); a write above it drops the oldest observations first.</summary>
    public required int EvidenceMaxFileBytes { get; init; }
}

public sealed class SetupOptionsValidator : IValidateOptions<SetupOptions>
{
    // Memory bounds, not control parameters.
    private const int MaxEntities = 100_000;
    private const int MaxReadouts = 500;
    private const int MaxHistory = 10_000;
    private const int MaxBackupFiles = 1000;
    private const int MinEvidenceBytes = 4096;
    private const int MaxEvidenceBytes = 16 * 1024 * 1024;

    public ValidateOptionsResult Validate(string? name, SetupOptions options)
    {
        const string section = SetupOptions.SectionName;
        var failures = new List<string>();
        OptionsValidation.RequirePositive(failures, $"{section}:DiscoveryCacheTtl", options.DiscoveryCacheTtl);
        OptionsValidation.RequirePositive(failures, $"{section}:DiscoveryLinkIdleTimeout", options.DiscoveryLinkIdleTimeout);
        OptionsValidation.RequirePositive(failures, $"{section}:DiscoveryRequestTimeout", options.DiscoveryRequestTimeout);
        OptionsValidation.RequirePositive(failures, $"{section}:ReadoutLeaseTime", options.ReadoutLeaseTime);
        OptionsValidation.RequirePositive(failures, $"{section}:ReadoutProbeInterval", options.ReadoutProbeInterval);
        OptionsValidation.RequirePositive(failures, $"{section}:ReloadConfirmTimeout", options.ReloadConfirmTimeout);
        Range(failures, $"{section}:MaxDiscoveredEntities", options.MaxDiscoveredEntities, MaxEntities);
        Range(failures, $"{section}:MaxReadoutEntities", options.MaxReadoutEntities, MaxReadouts);
        Range(failures, $"{section}:ReadoutHistoryLength", options.ReadoutHistoryLength, MaxHistory);
        Range(failures, $"{section}:MaxRetainedReadouts", options.MaxRetainedReadouts, MaxEntities);
        Range(failures, $"{section}:MaxBackups", options.MaxBackups, MaxBackupFiles);
        Range(failures, $"{section}:EvidencePerDevice", options.EvidencePerDevice, MaxHistory);
        if (options.EvidenceMaxFileBytes < MinEvidenceBytes || options.EvidenceMaxFileBytes > MaxEvidenceBytes)
        {
            failures.Add($"{section}:EvidenceMaxFileBytes must be between {MinEvidenceBytes} and {MaxEvidenceBytes}.");
        }
        if (options.MaxRetainedReadouts < options.MaxReadoutEntities)
        {
            failures.Add($"{section}:MaxRetainedReadouts must be at least MaxReadoutEntities.");
        }

        if (options.ReadoutLeaseTime > options.DiscoveryLinkIdleTimeout)
        {
            failures.Add($"{section}:ReadoutLeaseTime must not exceed DiscoveryLinkIdleTimeout.");
        }

        return OptionsValidation.Result(failures);
    }

    private static void Range(List<string> failures, string key, int value, int max)
    {
        if (value < 1 || value > max)
        {
            failures.Add($"{key} must be between 1 and {max}.");
        }
    }
}
