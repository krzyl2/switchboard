using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Signals;
using Switchboard.Host.Composition;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// The derived-sensor check of a hand-written file (07-PITFALLS, maintainer survey 2026-10-25): the
/// wizard refuses a derived sensor as a measurement input, a file edited by hand is warned about at
/// start and after every reload. A warning is never a safe state and never a refusal; a registry
/// that cannot be read says so instead of passing the inputs as primary. The runner checks the
/// configuration the signal store actually publishes (at start it may not be there yet, and the file
/// watcher reports a reload before the store applied it), and retries while the registry cannot be
/// read, so a derived input is never left unflagged until the next edit of the file.
/// </summary>
public sealed class RuntimeDerivedCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_derived_sensor_mapped_as_a_meter_input_is_a_warning_with_its_platform()
    {
        var configuration = Configuration("sensor.solaredge_m1_ac_power_a", "sensor.usage");

        var warning = Assert.Single(RuntimeDerivedCheck.Run(configuration, SetupValidatorTests.Fixture(), SetupFixtures.Defaults, Now), w => w.Signal?.Value == "grid_power_l1");

        Assert.Equal((ConfigurationWarningCodes.DerivedMeasurementInput, EntityId.Parse("sensor.usage"), Now), (warning.Code, warning.Entity, warning.CheckedAt));
        Assert.NotNull(warning.Platform);
    }

    [Fact]
    public void A_sensor_in_no_registry_cannot_be_verified_and_says_so()
    {
        var configuration = Configuration("sensor.solaredge_m1_ac_power_a", "sensor.house_load_yaml");

        var warning = Assert.Single(RuntimeDerivedCheck.Run(configuration, SetupValidatorTests.Fixture(), SetupFixtures.Defaults, Now), w => w.Signal?.Value == "grid_power_l1");

        Assert.Equal((ConfigurationWarningCodes.MeasurementNotInRegistry, (string?)null), (warning.Code, warning.Platform));
    }

    [Fact]
    public void The_primary_meter_inputs_of_the_example_raise_no_derived_warning()
    {
        var warnings = RuntimeDerivedCheck.Run(Configuration(), SetupValidatorTests.Fixture(), SetupFixtures.Defaults, Now);

        Assert.DoesNotContain(warnings, w => w.Code == ConfigurationWarningCodes.DerivedMeasurementInput);
        Assert.DoesNotContain(warnings, w => w.Entity?.Value.StartsWith("sensor.solaredge_m1_ac_", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Switchboards_own_entity_declared_as_a_stop_actuator_is_a_warning()
    {
        // A hand-written file that declares Switchboard's own override select as the charger's stop
        // would loop every stop back into Switchboard instead of reaching the charger (0.7.1).
        var yaml = TestConfigurations.Example.Replace("stop_actuator: select.goe_422752_frc", "stop_actuator: select.switchboard_ev_charger_override", StringComparison.Ordinal);
        Assert.NotEqual(TestConfigurations.Example, yaml);
        var configuration = TestConfigurations.Valid(yaml).Configuration!;

        var warning = Assert.Single(RuntimeDerivedCheck.Run(configuration, SetupValidatorTests.Fixture(), SetupFixtures.Defaults, Now), w => w.Code == ConfigurationWarningCodes.OwnEntityActuator);

        Assert.Equal(EntityId.Parse("select.switchboard_ev_charger_override"), warning.Entity);
        Assert.Contains("devices[3].stop_actuator", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeDerivedCheck.Run(Configuration(), SetupValidatorTests.Fixture(), SetupFixtures.Defaults, Now), w => w.Code == ConfigurationWarningCodes.OwnEntityActuator);
    }

    [Fact]
    public void A_registry_that_cannot_be_read_is_one_warning_never_a_pass()
    {
        Assert.Equal(
            [ConfigurationWarningCodes.DerivedCheckUnavailable],
            RuntimeDerivedCheck.Run(Configuration(), SetupValidatorTests.Fixture(registry: false), SetupFixtures.Defaults, Now).Select(w => w.Code));
        Assert.Equal(
            [ConfigurationWarningCodes.DerivedCheckUnavailable],
            RuntimeDerivedCheck.Run(Configuration(), null, SetupFixtures.Defaults, Now).Select(w => w.Code));
    }

    [Fact]
    public void Actuators_and_device_states_are_not_measurement_inputs()
    {
        // A relay's state or a setpoint is what the device says it is set to, not a measurement.
        var warnings = RuntimeDerivedCheck.Run(Configuration(), SetupValidatorTests.Fixture(), SetupFixtures.Defaults, Now);

        Assert.DoesNotContain(warnings, w => w.Entity?.Value.StartsWith("switch.", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(warnings, w => w.Entity?.Value.StartsWith("number.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_configuration_published_after_the_first_check_is_still_checked()
    {
        // At add-on start the check can run before the store published the configuration: it finds
        // none, and must run again once the store publishes one.
        var h = new RunnerHarness();
        var run = h.Runner.RunAsync(h.Stop.Token);
        await h.UntilAsync(() => h.Checked);
        Assert.Equal(0, h.Source.Reads);

        h.Publish(Yaml("sensor.solaredge_m1_ac_power_a", "sensor.usage"));

        await h.UntilAsync(() => h.Warnings.Current.Any(w => w.Code == ConfigurationWarningCodes.DerivedMeasurementInput));
        await h.StopAsync(run);
    }

    [Fact]
    public async Task Each_configuration_the_store_applies_is_checked_again()
    {
        var h = new RunnerHarness();
        h.Publish(TestConfigurations.Example);
        var run = h.Runner.RunAsync(h.Stop.Token);
        await h.UntilAsync(() => h.Source.Reads == 1);
        Assert.DoesNotContain(h.Warnings.Current, w => w.Code == ConfigurationWarningCodes.DerivedMeasurementInput);

        h.Publish(Yaml("sensor.solaredge_m1_ac_power_a", "sensor.usage"));
        await h.UntilAsync(() => h.Warnings.Current.Any(w => w.Code == ConfigurationWarningCodes.DerivedMeasurementInput));

        // Store cycles of the same configuration do not read the registry again; a new one does.
        var reads = h.Source.Reads;
        h.Republish();
        h.Publish(TestConfigurations.Example);
        await h.UntilAsync(() => h.Warnings.Current.All(w => w.Code != ConfigurationWarningCodes.DerivedMeasurementInput));
        Assert.Equal(reads + 1, h.Source.Reads);
        await h.StopAsync(run);
    }

    [Fact]
    public async Task A_registry_that_cannot_be_read_at_start_is_read_again_later()
    {
        // Home Assistant Core restarting at add-on start: derived_check_unavailable, then a retry
        // after Setup:DiscoveryCacheTtl finds the derived input.
        var h = new RunnerHarness { Unavailable = true };
        h.Publish(Yaml("sensor.solaredge_m1_ac_power_a", "sensor.usage"));
        var run = h.Runner.RunAsync(h.Stop.Token);
        await h.UntilAsync(() => h.Warnings.Current.Any(w => w.Code == ConfigurationWarningCodes.DerivedCheckUnavailable));
        await h.Time.WaitForTimerAsync(RunnerHarness.RetryAfter);

        h.Unavailable = false;
        h.Time.Advance(RunnerHarness.RetryAfter);

        await h.UntilAsync(() => h.Warnings.Current.Any(w => w.Code == ConfigurationWarningCodes.DerivedMeasurementInput));
        Assert.DoesNotContain(h.Warnings.Current, w => w.Code == ConfigurationWarningCodes.DerivedCheckUnavailable);
        await h.StopAsync(run);
    }

    private static string Yaml(string replace, string with)
    {
        var yaml = TestConfigurations.Example;
        var replaced = yaml.Replace($"entity: {replace}\n", $"entity: {with}\n", StringComparison.Ordinal).Replace($"entity: {replace}\r\n", $"entity: {with}\r\n", StringComparison.Ordinal);
        Assert.NotEqual(yaml, replaced);
        return replaced;
    }

    /// <summary>The runner over a real snapshot holder (what the store publishes), the discovery cache over a scripted registry, and a fake host clock for its retry.</summary>
    private sealed class RunnerHarness
    {
        public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

        private readonly SignalStore _store = new(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        private readonly SignalSnapshotHolder _holder;
        private int _checked;

        public RunnerHarness()
        {
            _holder = new SignalSnapshotHolder(new SignalDiagnostics(_store.Snapshot(Time.GetUtcNow()), null, [], []));
            Source = new ScriptedRegistry(this);
            Runner = new RuntimeDerivedCheckRunner(
                new EntityDiscoveryCache(Source, Time, RetryAfter),
                new CheckedView(new DiagnosticsRuntimeView(_holder), this),
                _holder,
                SetupFixtures.Defaults,
                Warnings,
                RetryAfter,
                Time,
                NullLogger<RuntimeDerivedCheckRunner>.Instance);
        }

        public ObservableTimeProvider Time { get; } = new();

        public ConfigurationWarningsHolder Warnings { get; } = new();

        public ScriptedRegistry Source { get; }

        public RuntimeDerivedCheckRunner Runner { get; }

        public CancellationTokenSource Stop { get; } = new();

        public bool Unavailable { get; set; }

        /// <summary>The runner has read the configuration in force at least once.</summary>
        public bool Checked => Volatile.Read(ref _checked) > 0;

        /// <summary>The store applied <paramref name="yaml"/> and published a cycle with it.</summary>
        public void Publish(string yaml)
        {
            _store.ApplyConfiguration(TestConfigurations.Valid(yaml), Time.GetUtcNow());
            Republish();
        }

        /// <summary>Another store cycle of the configuration in force.</summary>
        public void Republish() => _holder.Publish(new SignalDiagnostics(_store.Snapshot(Time.GetUtcNow()), _store.ConfigurationResult, [], []));

        /// <summary>Hang-guarded: a passing test leaves as soon as the condition holds.</summary>
        public async Task UntilAsync(Func<bool> condition)
        {
            for (var tries = 0; !condition(); tries++)
            {
                Assert.True(tries < 2000, "The derived-sensor check never got there.");
                await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
            }
        }

        public async Task StopAsync(Task run)
        {
            await Stop.CancelAsync();
            await run.Soon();
        }

        private void MarkChecked() => Interlocked.Increment(ref _checked);

        public sealed class ScriptedRegistry(RunnerHarness owner) : IEntityDiscoverySource
        {
            private int _reads;

            public int Reads => Volatile.Read(ref _reads);

            public Task<DiscoverySnapshot> ReadAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _reads);
                return owner.Unavailable
                    ? Task.FromException<DiscoverySnapshot>(new DiscoveryUnavailableException(DiscoveryUnavailableException.HomeAssistantUnavailable, "Home Assistant is restarting"))
                    : Task.FromResult(SetupValidatorTests.Fixture());
            }
        }

        /// <summary>The runtime view, noting that the runner has read it.</summary>
        private sealed class CheckedView(ISetupRuntimeView inner, RunnerHarness owner) : ISetupRuntimeView
        {
            public SwitchboardConfiguration? Configuration
            {
                get
                {
                    var configuration = inner.Configuration;
                    owner.MarkChecked();
                    return configuration;
                }
            }

            public IReadOnlyDictionary<EntityId, TimeSpan> DetectedRefresh => inner.DetectedRefresh;
        }
    }

    private static Switchboard.Application.Configuration.SwitchboardConfiguration Configuration(string? replace = null, string? with = null)
    {
        var yaml = TestConfigurations.Example;
        if (replace is not null)
        {
            var replaced = yaml.Replace($"entity: {replace}\n", $"entity: {with}\n", StringComparison.Ordinal).Replace($"entity: {replace}\r\n", $"entity: {with}\r\n", StringComparison.Ordinal);
            Assert.NotEqual(yaml, replaced);
            yaml = replaced;
        }

        return TestConfigurations.Valid(yaml).Configuration!;
    }
}
