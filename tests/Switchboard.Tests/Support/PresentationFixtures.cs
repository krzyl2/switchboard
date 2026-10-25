using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Support;

/// <summary>The shipped Switchboard:Statistics values (src/Switchboard.Host/appsettings.json), for presentation tests.</summary>
internal static class TestStatistics
{
    public static StatisticsOptions Options(
        TimeSpan? bucketLength = null,
        TimeSpan? retention = null,
        int maxFileBytes = 1024 * 1024,
        int maxSamples = 720,
        int maxDecisions = 50) => new()
    {
        BucketLength = bucketLength ?? TimeSpan.FromMinutes(5),
        Retention = retention ?? TimeSpan.FromHours(26),
        MaxIntegrationGap = TimeSpan.FromMinutes(2),
        SaveInterval = TimeSpan.FromMinutes(1),
        MaxFileBytes = maxFileBytes,
        VoltageLossFraction = 0.5,
        ExcursionPreRoll = TimeSpan.FromMinutes(2),
        ExcursionPostRoll = TimeSpan.FromMinutes(2),
        ExcursionMaxSamples = maxSamples,
        ExcursionMaxDecisions = maxDecisions,
    };

    /// <param name="logger">Pass a <see cref="LogCapture"/> logger to see failures the hub isolates from the guard (it never throws).</param>
    public static PresentationHub Hub(StatisticsOptions? options = null, Microsoft.Extensions.Logging.ILogger<PresentationHub>? logger = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()), logger ?? NullLogger<PresentationHub>.Instance);
}

/// <summary>
/// Hand-built control steps and power pictures for the accumulator's unit tests: the reference
/// devices (three 1.5 kW heaters, a charger on L1) from a real parsed configuration, and a
/// one-phase picture whose parts sum as the builder's do.
/// </summary>
internal static class TestSteps
{
    public static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<SwitchboardConfiguration> Reference = new(() => TestConfigurations.Valid(TestDevices.Document()).Configuration!);

    public static DispatchSetup Setup => DispatchSetupFactory.Build(Reference.Value);

    public static Switchboard.Domain.Guard.ProtectionSetup Protection => ProtectionSetupFactory.Build(Reference.Value);

    /// <summary>A step at <paramref name="at"/> carrying <paramref name="decisions"/>, with phase voltages if given (null = not fresh).</summary>
    public static GuardStep Step(
        DateTimeOffset at,
        ImmutableArray<Decision> decisions = default,
        IReadOnlyDictionary<Phase, double?>? voltages = null,
        bool voltageMapped = true,
        DispatchStatus? status = null)
    {
        var mode = TestModes.Shadow;
        var phases = new[] { Phase.L1, Phase.L2, Phase.L3 };
        var dispatch = new DispatchInputs(0, at, [.. phases.Select(p => new PhaseDispatch(
            p, true, null, Watts.Zero, Watts.Zero, Watts.Zero, voltages is not null && voltages.TryGetValue(p, out var v) && v is { } volts ? new Volts(volts) : null))]);
        var inputs = new ControlInputs(new ProtectionInputs(0, at, null, [], null, Protection, [], null), dispatch, Setup)
        {
            Presentation = new PresentationInputs(
                new Volts(230),
                GenerationState.Measured,
                at,
                phases.ToImmutableDictionary(p => p, _ => voltageMapped),
                ImmutableDictionary<DeviceName, string>.Empty),
        };
        var evaluation = new GuardEvaluation(at, GuardStatus.Initial(at, mode), [], [], null, 0) { Setup = Protection, ConfigurationValid = true };
        return new GuardStep(evaluation, decisions.IsDefault ? [] : decisions, GuardMemory.Fresh) { Inputs = inputs, DispatchStatus = status };
    }

    /// <summary>
    /// A picture of L1 only: net import and generation give the load; the given devices are
    /// controlled (null power = unknown); the house's own part is the residual.
    /// </summary>
    public static PowerView View(DateTimeOffset at, double? netImport, double? generation = 0, params (string Device, double? Watts, PowerSource Source)[] devices)
    {
        var load = netImport is { } n && generation is { } g ? n + g : (double?)null;
        var known = devices.All(d => d.Watts is not null);
        var controlled = known ? devices.Sum(d => d.Watts!.Value) : (double?)null;
        var uncontrolled = load is { } l && controlled is { } c ? l - c : (double?)null;
        Quantity Q(double? value, PowerUnknownReason reason) => value is { } x ? Quantity.Known(x, at) : Quantity.Missing(reason);
        var balance = new BalanceParts(
            Q(generation, PowerUnknownReason.GenerationUnknown),
            null,
            Q(netImport, PowerUnknownReason.PowerNotFresh),
            Q(load, PowerUnknownReason.PowerNotFresh),
            Q(controlled, PowerUnknownReason.DevicePowerUnknown),
            [.. devices.Where(d => d.Watts is null).Select(d => DeviceName.Parse(d.Device))],
            Quantity.Missing(PowerUnknownReason.NotDeclared),
            Q(uncontrolled, PowerUnknownReason.DevicePowerUnknown),
            uncontrolled < 0);
        var phase = new PhasePower(Phase.L1, new Amperes((netImport ?? 0) / 230), FlowDirection.Import, at, new Amperes(14.13), new Amperes(25), new Amperes(28.25), new Volts(230), false, balance, null, null, null, [], false, false);
        var views = devices.Select(d => new DevicePowerView(
            DeviceName.Parse(d.Device),
            [new DevicePhasePowerView(Phase.L1, d.Watts is { } w ? new Watts(w) : null, d.Watts is null ? PowerSource.Unknown : d.Source, null)],
            d.Watts is { } total ? new Watts(total) : null,
            d.Watts is null ? PowerSource.Unknown : d.Source));
        return new PowerView(at, load is not null, load is null ? DispatchInvalidReason.PowerNotFresh : null, balance, [phase], [.. views], [], TimeSpan.FromSeconds(30));
    }

    /// <summary>A device decision as the gate records it.</summary>
    public static Decision Device(DateTimeOffset at, string device, DecisionAction action, ReasonCode reason, DeviceLevel? target, int? stage = null, bool commanded = false, DecisionOrigin origin = DecisionOrigin.Guard, DecisionInputs? inputs = null) =>
        new($"t-{Guid.NewGuid():N}"[..20], at, DecisionScope.Device, DeviceName.Parse(device), stage, Phase.L1, action, reason, null, OperatingMode.Shadow, commanded, commanded ? null : ReasonCode.ModeShadow, inputs ?? DecisionInputs.None, origin, null, target);

    /// <summary>The dispatch inputs of a change that waited for a lockout since <paramref name="since"/> (EffectorGate records them on the deferral and on the change applied at expiry).</summary>
    public static DecisionInputs DeferredSince(DateTimeOffset since) => DecisionInputs.None with
    {
        Dispatch = new DispatchDecisionInputs(
            [], Amperes.Zero, Amperes.Zero, null, ImmutableDictionary<Phase, Amperes>.Empty, ImmutableDictionary<ConstraintKey, Amperes>.Empty,
            null, null, TimeSpan.Zero, TimeSpan.Zero, since, OverrideMode.Auto),
    };

    /// <summary>A dispatch status naming each device's intended level (what a shed is compared against).</summary>
    public static DispatchStatus Intended(DateTimeOffset at, params (string Device, DeviceLevel Level)[] devices)
    {
        var setup = Setup;
        return new DispatchStatus(
            at,
            TestModes.Shadow,
            false,
            [],
            [.. devices.Select(d =>
            {
                var spec = setup.Device(DeviceName.Parse(d.Device))!.Spec;
                return new DispatchDeviceStatus(
                    spec.Id, spec.Type, spec.Phases, null, null, d.Level, d.Level, true, true, null, null, null, null,
                    TimeSpan.Zero, TimeSpan.Zero, null, SettlingStatus.None, null, 0, DeviceOverride.Auto, null, [])
                {
                    Start = spec.Start,
                };
            })]);
    }
}
