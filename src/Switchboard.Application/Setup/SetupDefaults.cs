using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Setup;

/// <summary>
/// The setup wizard's own rules (the embedded setup-defaults.yaml): what it refuses as a derived
/// sensor, its CONN-06 plausibility bounds, the passive self-test thresholds, prefills and device
/// templates. Not control parameters and not overridable: they decide what the wizard accepts,
/// never what the guard or dispatch do.
/// </summary>
public sealed record SetupDefaults(
    ImmutableArray<string> DerivedPlatforms,
    SetupValidationRules Validation,
    SelfTestRules SelfTest,
    double PrefillVoltage,
    ImmutableArray<DeviceTemplate> Templates);

/// <param name="PlausibleCurrentMaxMultiple">A current above this multiple of the breaker rating is not this connection's.</param>
/// <param name="PlausiblePowerMaxMultiple">A power above this multiple of rating × voltage is not plausible.</param>
/// <param name="PlausibleVoltageBand">A voltage outside ±this fraction of the nominal voltage is not plausible.</param>
/// <param name="PhaseSumToleranceFraction">The phases sum to the total within this fraction (plus k·σ of every term).</param>
/// <param name="PhaseSumMinSamples">Aligned samples needed before the phase sum is judged.</param>
/// <param name="SuggestedMaxAgeIntervals">A suggested max_age is this many detected refresh intervals.</param>
/// <param name="MaxAgeAboveRefreshIntervals">A meter max_age longer than this many detected refresh intervals is flagged.</param>
public sealed record SetupValidationRules(
    double PlausibleCurrentMaxMultiple,
    double PlausiblePowerMaxMultiple,
    double PlausibleVoltageBand,
    double PhaseSumToleranceFraction,
    int PhaseSumMinSamples,
    int SuggestedMaxAgeIntervals,
    int MaxAgeAboveRefreshIntervals);

/// <param name="Isolation">The shortest window around a switching (see <see cref="SelfTest.StepWindow"/>).</param>
/// <param name="WatchTimeout">A guided watch ends after this long without a verdict (a switching inside it is still judged when its window has passed).</param>
public sealed record SelfTestRules(PhaseStepParameters Classifier, TimeSpan Isolation, TimeSpan WatchTimeout);

/// <summary>
/// One template card of step 4. <see cref="Prefill"/> holds device keys of switchboard.yaml only
/// (checked against the device parser); everything else the device inherits is not written.
/// </summary>
public sealed record DeviceTemplate(string Id, DeviceType Type, ImmutableArray<TemplateValue> Prefill, bool DrawRecommended);

/// <summary>One prefilled key: its document key and its value as written (a number, a duration or text).</summary>
public sealed record TemplateValue(string Key, string Text, TemplateValueKind Kind);

public enum TemplateValueKind
{
    Number,
    Duration,
    Text,
}

/// <summary>One template card as the wizard shows it: what it prefills (draft field names) and what the device inherits.</summary>
/// <param name="Prefill">Draft fields and values (numbers or text) the template sets; written to the file.</param>
/// <param name="ReactionTime">Inherited from control-defaults (not written, so a later default still applies).</param>
public sealed record TemplateDescription(
    string Id,
    string Type,
    IReadOnlyDictionary<string, object> Prefill,
    TimeSpan ReactionTime,
    TimeSpan MinOn,
    TimeSpan MinOff,
    ImmutableArray<string> ActuatorDomains,
    ImmutableArray<string> StateDomains,
    bool DrawRecommended);

public static class DeviceTemplates
{
    /// <summary>The templates with the values a new device of each inherits under <paramref name="control"/>; domains from the device parser.</summary>
    public static ImmutableArray<TemplateDescription> Describe(SetupDefaults defaults, ControlParameters control)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(control);
        return [.. defaults.Templates.Select(t =>
        {
            var prefill = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = DeviceSectionParser.TypeName(t.Type) };
            foreach (var value in t.Prefill)
            {
                prefill[FieldName(value.Key)] = value.Kind == TemplateValueKind.Number
                    ? double.Parse(value.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
                    : value.Text;
            }

            var setpoint = t.Type is DeviceType.Modulating or DeviceType.Storage;
            return new TemplateDescription(
                t.Id,
                DeviceSectionParser.TypeName(t.Type),
                prefill,
                control.Devices.ReactionTime[t.Type],
                setpoint ? control.Devices.ModulatingMinOn : control.Dispatch.RelayMinOn,
                setpoint ? control.Devices.ModulatingMinOff : control.Dispatch.RelayMinOff,
                DeviceSectionParser.ActuatorDomainsFor(t.Type),
                DeviceSectionParser.StateDomainsFor(t.Type),
                t.DrawRecommended);
        })];
    }

    /// <summary>Whether a document key is a key a device of this type may carry (the template binder's test).</summary>
    public static bool IsDeviceKey(DeviceType type, string key) => DeviceSectionParser.KeysFor(type).Contains(key);

    /// <summary>The draft field of a device key: min_on → minOn.</summary>
    public static string FieldName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parts = key.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
