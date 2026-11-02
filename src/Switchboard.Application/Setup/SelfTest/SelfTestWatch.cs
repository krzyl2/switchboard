using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup.SelfTest;

/// <summary>What a self-test is asked to watch: a device of the draft, or a test load the wizard describes (exactly one of the two).</summary>
/// <param name="DeviceIndex">The index of a device in the draft; null for a test load.</param>
/// <param name="LoadStateEntity">The test load's switched entity.</param>
/// <param name="LoadPhase">The test load's phase (L1, L2 or L3).</param>
/// <param name="LoadRatedPowerW">The test load's rated power.</param>
public sealed record SelfTestSubject(int? DeviceIndex, string? LoadStateEntity, string? LoadPhase, double? LoadRatedPowerW);

/// <summary>The watch a self-test request describes, or why it cannot be watched.</summary>
public static class SelfTestWatch
{
    /// <summary>The watch for <paramref name="subject"/> from the draft's meter rows; or the issues that say why it cannot be watched.</summary>
    public static (SelfTestRequest? Watch, ImmutableArray<SetupIssue> Issues) Of(SetupDraft draft, SelfTestSubject subject, ControlParameters control, PhaseStepParameters parameters)
    {
        var profile = SetupValidator.Profile(draft.Connection, control.Connection.DefaultReserve);
        var phases = profile?.Phases ?? [Phase.L1, Phase.L2, Phase.L3];
        var power = SetupValidator.MeterTerms(draft, phases, power: true);
        var current = power is null ? SetupValidator.MeterTerms(draft, phases, power: false) : null;
        if (power is null && current is null)
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, "meter", "Map the meter of every phase (current or power, with unit and sign) before the self-test.")]);
        }

        string? stateText;
        string? phaseText;
        double? rating;
        double? ratedCurrent;
        double? ratedPower;
        string field;
        if (subject.DeviceIndex is { } index)
        {
            field = $"devices[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (index < 0 || index >= draft.Devices.Count)
            {
                return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, "devices", $"There is no device {index} in the draft.")]);
            }

            var d = draft.Devices[index];
            if (d.Kind != DeviceType.OnOff || d.Phase is not ("L1" or "L2" or "L3"))
            {
                return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.phase", "The self-test watches an on/off device on one phase (a charger or a three-phase device cannot be judged this way).")]);
            }

            stateText = d.StateEntity ?? d.Actuator;
            phaseText = d.Phase;
            rating = power is not null ? d.RatedPowerW : d.RatedCurrentA;
            ratedCurrent = d.RatedCurrentA ?? (d.RatedPowerW is { } w && profile is not null ? w / profile.Voltage.Value : null);
            ratedPower = d.RatedPowerW ?? (d.RatedCurrentA is { } a && profile is not null ? a * profile.Voltage.Value : null);
        }
        else
        {
            field = "testLoad";
            stateText = subject.LoadStateEntity;
            phaseText = subject.LoadPhase;
            rating = subject.LoadRatedPowerW is { } watts && power is null && profile is not null ? watts / profile.Voltage.Value : subject.LoadRatedPowerW;
            ratedPower = subject.LoadRatedPowerW;
            ratedCurrent = subject.LoadRatedPowerW is { } loadWatts && profile is not null ? loadWatts / profile.Voltage.Value : null;
        }

        if (!EntityId.TryParse(stateText, out var state))
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.{(subject.DeviceIndex is null ? "stateEntity" : "actuator")}", "The switched entity is required.")]);
        }

        if (!Enum.TryParse<Phase>(phaseText, out var phase) || !phases.Contains(phase))
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.phase", "The device's phase (L1, L2 or L3) is required.")]);
        }

        if (rating is not > 0)
        {
            return (null, [SetupIssue.Error(SetupIssueCodes.RequiredField, $"{field}.{(power is not null ? "ratedPowerW" : "ratedCurrentA")}", "The rating is required: the step is judged against it.")]);
        }

        var terms = power ?? current!;
        var kind = power is not null ? SignalKind.Power : SignalKind.Current;

        // The draft's other devices and draws: one of them changing in the window is not this device's step.
        var (switches, draws) = SelfTestIsolation.Of(
            draft,
            subject.DeviceIndex,
            state.Value,
            ratedCurrent ?? (power is null ? rating.Value : 0),
            ratedPower ?? (power is not null ? rating.Value : 0),
            control,
            parameters);
        return (new SelfTestRequest(
            state.Value,
            phase,
            rating.Value,
            power is not null ? "W" : "A",
            power is null && draft.Meter.CurrentSign == "unsigned",
            terms,
            control.Signals.DefaultSigma.GetValueOrDefault(kind))
        {
            OtherSwitches = switches,
            OtherDraws = draws,
        }, []);
    }
}

/// <summary>One on/off device on one phase with the evidence the add-on holds for its switched entity, judged.</summary>
public sealed record DeviceEvidence(string Device, string StateEntity, Phase Phase, PhaseJudgement Judgement);

/// <summary>Judges the step evidence gathered per switched entity against the configured devices' phases and ratings.</summary>
public static class PhaseEvidenceReport
{
    /// <summary>Every on/off device with one single-phase stage, judged on the evidence of its state entity; none without a valid configuration.</summary>
    public static IReadOnlyList<DeviceEvidence> Judge(PhaseEvidenceStore evidence, ConfigurationResult? result, SetupDefaults defaults)
    {
        var configuration = result?.Configuration;
        if (configuration is null)
        {
            return [];
        }

        var phases = configuration.Connection.Phases;
        var powerTerms = Terms(configuration, phases, SignalRole.PhasePower);
        var currentTerms = Terms(configuration, phases, SignalRole.PhaseCurrent);
        return [.. configuration.Devices
            .Where(d => d.Type == DeviceType.OnOff && d.Stages.Length == 1 && d.Stages[0].Phases.Length == 1)
            .Select(d =>
            {
                var stage = d.Stages[0];
                var judgement = PhaseEvidence.Judge(
                    evidence.For(stage.StateEntity),
                    stage.Phases[0],
                    stage.RatedPower?.Value,
                    stage.RatedCurrent.Value,
                    powerTerms,
                    currentTerms,
                    defaults.SelfTest.Classifier);
                return new DeviceEvidence(d.Id.Value, stage.StateEntity.Value, stage.Phases[0], judgement);
            })];
    }

    private static ImmutableDictionary<Phase, MeterTerm>? Terms(SwitchboardConfiguration configuration, IEnumerable<Phase> phases, SignalRole role)
    {
        var builder = ImmutableDictionary.CreateBuilder<Phase, MeterTerm>();
        foreach (var phase in phases)
        {
            if (configuration.Signals.FirstOrDefault(s => s.Role == role && s.Phase == phase) is not { } signal)
            {
                return null;
            }

            builder[phase] = new MeterTerm(signal.Entity, signal.Unit ?? string.Empty, MeterValues.SignName(signal.Sign));
        }

        return builder.ToImmutable();
    }
}
