using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup;

/// <summary>The closed list of configuration warning codes of the running add-on's derived-sensor check.</summary>
public static class ConfigurationWarningCodes
{
    /// <summary>A measurement input is provided by a derived platform (template, statistics, ...): 07-PITFALLS.</summary>
    public const string DerivedMeasurementInput = "derived_measurement_input";

    /// <summary>A measurement input is in no entity registry (YAML without a unique_id): its source cannot be verified.</summary>
    public const string MeasurementNotInRegistry = "measurement_not_in_registry";

    /// <summary>Home Assistant's entity registry could not be read, so no input could be checked.</summary>
    public const string DerivedCheckUnavailable = "derived_check_unavailable";
}

/// <summary>One warning about the configuration in force; never a safe state, never a refusal of the file.</summary>
/// <param name="Signal">The logical signal concerned; null for derived_check_unavailable.</param>
/// <param name="Platform">The registry platform of a derived input.</param>
public sealed record ConfigurationWarning(string Code, SignalName? Signal, EntityId? Entity, string? Platform, DateTimeOffset CheckedAt, string Message);

/// <summary>
/// The derived-sensor check for a hand-written file (maintainer survey 2026-10-25): the wizard
/// refuses a derived sensor as a measurement input, a file edited by hand is only warned about. Every
/// measurement input (meter, inverter, totals, device draws, circuit-group sub-meters) is looked up
/// in Home Assistant's entity registry: a derived platform, or no registry entry, is a warning; a
/// registry that cannot be read is one derived_check_unavailable warning.
/// </summary>
public static class RuntimeDerivedCheck
{
    private static readonly ImmutableHashSet<SignalRole> MeasurementRoles =
    [
        SignalRole.PhaseCurrent,
        SignalRole.PhasePower,
        SignalRole.PhaseVoltage,
        SignalRole.InverterPower,
        SignalRole.InverterPhaseCurrent,
        SignalRole.TotalCurrent,
        SignalRole.TotalPower,
        SignalRole.DeviceCurrent,
        SignalRole.DevicePower,
        SignalRole.GroupCurrent,
    ];

    /// <param name="snapshot">Home Assistant's entities and registry; null when it could not be read.</param>
    public static ImmutableArray<ConfigurationWarning> Run(SwitchboardConfiguration configuration, DiscoverySnapshot? snapshot, SetupDefaults defaults, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(defaults);
        if (snapshot is not { RegistryAvailable: true })
        {
            return [new ConfigurationWarning(
                ConfigurationWarningCodes.DerivedCheckUnavailable,
                null,
                null,
                null,
                now,
                "Home Assistant's entity registry could not be read, so the measurement inputs could not be checked for derived (template) sensors.")];
        }

        var warnings = ImmutableArray.CreateBuilder<ConfigurationWarning>();
        foreach (var signal in configuration.Signals.Where(s => MeasurementRoles.Contains(s.Role)))
        {
            switch (DerivedSensorRule.Check(signal.Entity, snapshot, defaults))
            {
                case DerivedCheck.Derived:
                    var platform = DerivedSensorRule.PlatformOf(signal.Entity, snapshot);
                    warnings.Add(new ConfigurationWarning(
                        ConfigurationWarningCodes.DerivedMeasurementInput,
                        signal.Name,
                        signal.Entity,
                        platform,
                        now,
                        $"{signal.Entity} ({signal.Path}) is computed by the {platform} integration from other sensors; map the primary measurement instead (a derived sensor is never a control input)."));
                    break;
                case DerivedCheck.NotInRegistry:
                    warnings.Add(new ConfigurationWarning(
                        ConfigurationWarningCodes.MeasurementNotInRegistry,
                        signal.Name,
                        signal.Entity,
                        null,
                        now,
                        $"{signal.Entity} ({signal.Path}) is in no entity registry, so its source cannot be verified; give it a unique_id, or map the primary measurement."));
                    break;
            }
        }

        return warnings.ToImmutable();
    }
}

/// <summary>The latest warnings of the derived-sensor check, for the API; written by the check, read by anyone.</summary>
public sealed class ConfigurationWarningsHolder
{
    private ImmutableArray<ConfigurationWarning> _current = [];

    public ImmutableArray<ConfigurationWarning> Current => _current;

    public void Publish(ImmutableArray<ConfigurationWarning> warnings) => _current = warnings;
}

/// <summary>
/// Runs the derived-sensor check whenever the signal store publishes another configuration (the
/// first one at start, then each reload it applied), over the read-only discovery link (refreshed
/// each time). It follows the configuration actually in force, not the file watcher, which reports
/// a reload before the store has applied it. While Home Assistant's registry cannot be read (Core
/// restarting at add-on start), the check is retried every <c>Setup:DiscoveryCacheTtl</c>. Every new
/// warning goes to the add-on log once. A failed or missing read is a derived_check_unavailable
/// warning, never a safe state.
/// </summary>
public sealed class RuntimeDerivedCheckRunner
{
    private readonly EntityDiscoveryCache _discovery;
    private readonly ISetupRuntimeView _runtime;
    private readonly IControlInputSource _published;
    private readonly SetupDefaults _defaults;
    private readonly ConfigurationWarningsHolder _holder;
    private readonly TimeSpan _retryAfter;
    private readonly TimeProvider _time;
    private readonly ILogger<RuntimeDerivedCheckRunner> _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private ImmutableHashSet<(string Code, string? Entity)> _logged = [];
    private SwitchboardConfiguration? _checked;
    private bool _everChecked;

    /// <param name="published">The store's publications: a configuration other than the one last checked wakes the check.</param>
    /// <param name="retryAfter">How long after an unreadable registry the check runs again (operational, appsettings).</param>
    public RuntimeDerivedCheckRunner(
        EntityDiscoveryCache discovery,
        ISetupRuntimeView runtime,
        IControlInputSource published,
        SetupDefaults defaults,
        ConfigurationWarningsHolder holder,
        TimeSpan retryAfter,
        TimeProvider time,
        ILogger<RuntimeDerivedCheckRunner> logger)
    {
        _discovery = discovery;
        _runtime = runtime;
        _published = published;
        _defaults = defaults;
        _holder = holder;
        _retryAfter = retryAfter;
        _time = time;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        void OnPublished(object? sender, ControlInputs inputs)
        {
            if (!Volatile.Read(ref _everChecked) || !ReferenceEquals(_runtime.Configuration, Volatile.Read(ref _checked)))
            {
                _wake.Writer.TryWrite(true);
            }
        }

        _published.Published += OnPublished;
        using var retry = _time.CreateTimer(_ => _wake.Writer.TryWrite(true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            _wake.Writer.TryWrite(true);
            while (await _wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _wake.Reader.TryRead(out _);
                var unavailable = await CheckAsync(cancellationToken).ConfigureAwait(false);
                retry.Change(unavailable ? _retryAfter : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            _published.Published -= OnPublished;
        }
    }

    /// <summary>One check of the configuration in force now; true when the registry could not be read (a retry is due). Public for tests.</summary>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        var configuration = _runtime.Configuration;
        Volatile.Write(ref _checked, configuration);
        Volatile.Write(ref _everChecked, true);
        if (configuration is null)
        {
            _holder.Publish([]);
            return false;
        }

        DiscoverySnapshot? snapshot = null;
        try
        {
            snapshot = await _discovery.GetAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscoveryUnavailableException ex)
        {
            _logger.LogInformation("The derived-sensor check could not read Home Assistant's registry: {Reason}", ex.Message);
        }

        var warnings = RuntimeDerivedCheck.Run(configuration, snapshot, _defaults, _time.GetUtcNow());
        _holder.Publish(warnings);
        var keys = warnings.Select(w => (w.Code, w.Entity?.Value)).ToImmutableHashSet();
        foreach (var warning in warnings.Where(w => !_logged.Contains((w.Code, w.Entity?.Value))))
        {
            _logger.LogWarning("Configuration warning {Code}: {Message}", warning.Code, warning.Message);
        }

        _logged = keys;
        return warnings.Any(w => w.Code == ConfigurationWarningCodes.DerivedCheckUnavailable);
    }
}
