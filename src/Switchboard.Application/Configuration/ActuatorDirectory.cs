using System.Collections.Immutable;
using Switchboard.Application.Effector;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Configuration;

/// <summary>
/// The service a command asks Home Assistant for, by meaning; the wire names and the domains each
/// is allowed on live in the outbound allow-list (Infrastructure) and nowhere else.
/// </summary>
public enum ActuatorService
{
    TurnOn,
    TurnOff,
    SetValue,

    /// <summary>A select stop actuator set to one of its two declared options (stop or resume).</summary>
    SelectOption,
}

/// <summary>One service call for one declared actuator: its entity, its domain, the service and, for a setpoint, the value in the actuator's own unit.</summary>
public sealed record ActuatorCall(EntityId Entity, string Domain, ActuatorService Service, double? Value)
{
    /// <summary>A select stop actuator's option (select_option only, never together with a value).</summary>
    public string? Option { get; init; }
}

/// <summary>
/// The second, independent check at send time, as the allow-list asks it of the serialized call:
/// whether <paramref name="entity"/> is a declared actuator (or stop actuator) of the configuration
/// loaded now that allows <paramref name="service"/> with <paramref name="value"/> (a setpoint, in the
/// actuator's unit) or <paramref name="option"/> (a select stop's option).
/// </summary>
public delegate bool ActuatorDeclaredCheck(EntityId entity, string domain, ActuatorService service, double? value, string? option);

public enum ActuatorRefusal
{
    /// <summary>No actuator of the configuration loaded now answers the command (or not with this service or value).</summary>
    NotDeclared,

    /// <summary>The device was re-pointed at another entity since the command was decided.</summary>
    Superseded,
}

/// <summary>A command resolved against the configuration loaded now: a call, or why there is none.</summary>
public sealed record ActuatorResolution(ActuatorCall? Call, ActuatorRefusal? Refusal, string? Detail);

/// <summary>Where the command path looks up a device's actuators at send time (SAFE-05).</summary>
public interface IActuatorDirectory
{
    /// <summary>The call for a command under the configuration loaded now; during an invalid configuration only offs and minimums of the last valid one.</summary>
    ActuatorResolution Resolve(DeviceCommand command);

    /// <summary>
    /// The second, independent check at send time: whether <paramref name="entity"/> is a declared
    /// actuator (or stop actuator) of the configuration loaded now that allows <paramref name="service"/>
    /// with <paramref name="value"/> (in the actuator's unit, within its declared range, on its step
    /// grid) or <paramref name="option"/> (one of a select stop's two declared options, offered by the
    /// entity when Home Assistant reported its options).
    /// </summary>
    bool IsDeclared(EntityId entity, string domain, ActuatorService service, double? value, string? option);
}

/// <summary>
/// The actuators of one configuration, by device: each relay stage's actuator, a modulating
/// device's stop actuator (a switch, or a select with its stop and resume options), a setpoint
/// device's actuator with its range and unit. Built in the configuration layer, the only place where
/// a device meets an entity ID.
/// </summary>
public sealed class ActuatorDirectory
{
    // Numerical tolerance on the step grid, not a control parameter (a decimal step is not exact in binary).
    private const double GridTolerance = 1e-6;

    // Decimal places a setpoint is sent with in its own unit: 16 A to mA is 16000, not 15999.999999999998.
    private const int ValueDigits = 6;

    private static readonly ImmutableArray<string> RelayDomains = DeviceSectionParser.ActuatorDomainsFor(DeviceType.OnOff);
    private static readonly ImmutableArray<string> SetpointDomains = DeviceSectionParser.ActuatorDomainsFor(DeviceType.Modulating);
    private static readonly ImmutableArray<string> SelectDomains = DeviceSectionParser.SelectStopDomains;

    private readonly ImmutableDictionary<DeviceName, DeviceActuators> _devices;

    private ActuatorDirectory(ImmutableDictionary<DeviceName, DeviceActuators> devices)
    {
        _devices = devices;
    }

    public static ActuatorDirectory Empty { get; } = new(ImmutableDictionary<DeviceName, DeviceActuators>.Empty);

    public IEnumerable<DeviceName> Devices => _devices.Keys;

    /// <summary>The off-capable actuators of a device: each relay stage, and the stop actuator of a modulating device.</summary>
    public IEnumerable<(DeviceCommandKind Kind, int Stage)> OffSlots(DeviceName device)
    {
        if (!_devices.TryGetValue(device, out var actuators))
        {
            yield break;
        }

        if (actuators.Type is DeviceType.OnOff or DeviceType.Stepped)
        {
            for (var stage = 1; stage <= actuators.Stages.Length; stage++)
            {
                yield return (DeviceCommandKind.Relay, stage);
            }
        }

        if (actuators.Stop is not null)
        {
            yield return (DeviceCommandKind.Enable, 1);
        }
    }

    public static ActuatorDirectory From(SwitchboardConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new ActuatorDirectory(configuration.Devices.ToImmutableDictionary(d => d.Id, d => new DeviceActuators(
            d.Id,
            d.Type,
            d.ActuatorRevision,
            [.. d.Stages.Select(s => s.Actuator)],
            d.StopActuator,
            d.StopOptions,
            d.Range,
            d.RangeUnit ?? DeviceSectionParser.DefaultUnit)));
    }

    /// <summary>The select stop actuators of this configuration with their declared options, for the options check.</summary>
    public IEnumerable<(DeviceName Device, EntityId Entity, StopOptions Options)> SelectStops =>
        _devices.Values.Where(d => d.Stop is not null && d.StopOptions is not null).Select(d => (d.Id, d.Stop!.Value, d.StopOptions!));


    /// <param name="decreasesOnly">An invalid configuration: only offs and a setpoint at its floor (never an increase, 04 §8).</param>
    /// <param name="offOnly">A retiring device: exactly the off of a stage or stop actuator (a select's stop option), nothing else.</param>
    /// <param name="offered">The options Home Assistant reported per entity: a select stop's option it does not offer is never sent.</param>
    public ActuatorResolution Resolve(DeviceCommand command, bool decreasesOnly, bool offOnly = false, IReadOnlyDictionary<EntityId, ImmutableArray<string>>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_devices.TryGetValue(command.Device, out var device))
        {
            return Refused(ActuatorRefusal.NotDeclared, $"{command.Device} is not a declared device");
        }

        if (command.Revision != device.Revision)
        {
            return Refused(ActuatorRefusal.Superseded, $"{command.Device} was re-pointed at another entity");
        }

        switch (command.Kind)
        {
            case DeviceCommandKind.Relay when device.Type is DeviceType.OnOff or DeviceType.Stepped:
                if (command.Stage < 1 || command.Stage > device.Stages.Length)
                {
                    return Refused(ActuatorRefusal.NotDeclared, $"{command.Device} has no stage {command.Stage}");
                }

                if ((decreasesOnly || offOnly) && command.On)
                {
                    return Refused(ActuatorRefusal.NotDeclared, "only offs are sent now");
                }

                var stage = device.Stages[command.Stage - 1];
                return Call(stage, command.On ? ActuatorService.TurnOn : ActuatorService.TurnOff, null);
            case DeviceCommandKind.Enable when device.Stop is { } stop:
                if ((decreasesOnly || offOnly) && command.On)
                {
                    return Refused(ActuatorRefusal.NotDeclared, "only offs are sent now");
                }

                if (device.StopOptions is not { } options)
                {
                    return Call(stop, command.On ? ActuatorService.TurnOn : ActuatorService.TurnOff, null);
                }

                // A select stop: its declared stop option stops it, its resume option resumes it.
                var option = (command.On ? options.ResumeOption : options.StopOption).Value;
                if (NotOffered(stop, option, offered) is { } notOffered)
                {
                    return Refused(ActuatorRefusal.NotDeclared, notOffered);
                }

                return Call(stop, ActuatorService.SelectOption, null, option);
            case DeviceCommandKind.Setpoint when !offOnly && device.Type is DeviceType.Modulating or DeviceType.Storage && command.Setpoint is { } setpoint && device.Range is { } range:
                var amps = OnGrid(setpoint.Value, range, Floor(device));
                if (decreasesOnly && Math.Abs(amps - Floor(device)) > GridTolerance * range.Step.Value)
                {
                    return Refused(ActuatorRefusal.NotDeclared, "only the floor setpoint is sent now");
                }

                return Call(device.Stages[0], ActuatorService.SetValue, Math.Round(amps / SignalUnits.Factor(device.Unit), ValueDigits));
            default:
                return Refused(ActuatorRefusal.NotDeclared, $"{command.Device} has no {command.Kind} actuator that allows this command");
        }
    }

    /// <param name="offered">The options Home Assistant reported per entity (see <see cref="Resolve"/>).</param>
    public bool IsDeclared(EntityId entity, string domain, ActuatorService service, double? value, string? option, bool decreasesOnly, IReadOnlyDictionary<EntityId, ImmutableArray<string>>? offered = null)
    {
        // Only select_option carries an option; every other service carries none.
        if (service == ActuatorService.SelectOption ? option is null || value is not null : option is not null)
        {
            return false;
        }

        foreach (var device in _devices.Values)
        {
            var relay = device.Type is DeviceType.OnOff or DeviceType.Stepped;
            if (relay && device.Stages.Contains(entity))
            {
                // A relay stage: on and off only, never a value or an option.
                return Domain(entity) == domain && RelayDomains.Contains(domain) && value is null
                    && (service == ActuatorService.TurnOff || (service == ActuatorService.TurnOn && !decreasesOnly));
            }

            if (device.Stop == entity)
            {
                if (device.StopOptions is not { } options)
                {
                    // A switch stop: on and off only, never a value or an option.
                    return Domain(entity) == domain && RelayDomains.Contains(domain) && value is null
                        && (service == ActuatorService.TurnOff || (service == ActuatorService.TurnOn && !decreasesOnly));
                }

                // A select stop: select_option with its stop option, or its resume option outside an
                // invalid configuration, and only an option the entity offers when its options are known.
                return service == ActuatorService.SelectOption && Domain(entity) == domain && SelectDomains.Contains(domain)
                    && (option == options.StopOption.Value || (option == options.ResumeOption.Value && !decreasesOnly))
                    && NotOffered(entity, option!, offered) is null;
            }

            if (!relay && device.Stages[0] == entity)
            {
                // A setpoint actuator: set_value only, within [floor, max] in its own unit, on its step grid.
                if (service != ActuatorService.SetValue || value is not { } raw || !double.IsFinite(raw) || Domain(entity) != domain
                    || !SetpointDomains.Contains(domain) || device.Range is not { } range)
                {
                    return false;
                }

                var amps = raw * SignalUnits.Factor(device.Unit);
                var floor = Floor(device);
                var tolerance = GridTolerance * range.Step.Value;
                var steps = (amps - range.Min.Value) / range.Step.Value;
                return amps >= floor - tolerance
                    && amps <= range.Max.Value + tolerance
                    && Math.Abs(steps - Math.Round(steps)) <= GridTolerance * Math.Max(1, Math.Abs(steps))
                    && (!decreasesOnly || Math.Abs(amps - floor) <= tolerance);
            }
        }

        return false;
    }

    /// <summary>
    /// The setpoint on the device's grid: snapped to the nearest step when within a millionth of a
    /// step (6.3 A with a 0.1 A step stays 6.3, never 6.2 from 2.9999… steps), floored otherwise,
    /// and clamped to [floor, max]: storage is never commanded into discharge, a modulating device
    /// never below its minimum.
    /// </summary>
    public static double OnGrid(double amps, ModulatingRange range, double floor)
    {
        ArgumentNullException.ThrowIfNull(range);
        var steps = (amps - range.Min.Value) / range.Step.Value;
        var nearest = Math.Round(steps);
        var whole = Math.Abs(steps - nearest) <= GridTolerance ? nearest : Math.Floor(steps);
        var value = range.Min.Value + (whole * range.Step.Value);
        return Math.Clamp(value, floor, range.Max.Value);
    }

    /// <summary>
    /// Why an option is not sent: Home Assistant reported the entity's options and this one is not
    /// among them (a typo, 1.0 for 1). Null when it is offered, or when the options are not known: a
    /// protective stop is never held back for want of an options list.
    /// </summary>
    public static string? NotOffered(EntityId entity, string option, IReadOnlyDictionary<EntityId, ImmutableArray<string>>? offered) =>
        offered is not null && offered.TryGetValue(entity, out var options) && !options.Contains(option, StringComparer.Ordinal)
            ? $"option '{option}' is not offered by {entity} (it offers {string.Join(", ", options.Select(o => $"'{o}'"))})"
            : null;

    private static double Floor(DeviceActuators device) => device.Type == DeviceType.Storage ? 0 : device.Range?.Min.Value ?? 0;

    private static ActuatorResolution Call(EntityId entity, ActuatorService service, double? value, string? option = null) =>
        new(new ActuatorCall(entity, Domain(entity), service, value) { Option = option }, null, null);

    private static ActuatorResolution Refused(ActuatorRefusal refusal, string detail) => new(null, refusal, detail);

    private static string Domain(EntityId entity) => entity.Value[..entity.Value.IndexOf('.', StringComparison.Ordinal)];

    private sealed record DeviceActuators(DeviceName Id, DeviceType Type, long Revision, ImmutableArray<EntityId> Stages, EntityId? Stop, StopOptions? StopOptions, ModulatingRange? Range, string Unit);
}

/// <summary>
/// The actuator directory of the configuration loaded now, published by the signal store runner
/// from the same reload that builds the protection setup. During an invalid or missing
/// configuration it answers from the last valid one, decreases only (offs, floor setpoints, a
/// select's stop option). A device a valid reload removed keeps a single-use retiring entry until
/// the next reload: exactly one off of a stage or its stop actuator (a select's stop option), for the
/// final off of a device Switchboard started. The options Home Assistant reports for each select are
/// observed too: an option the entity does not offer is never sent. Thread-safe: published on the
/// store's thread, read by the command path's sender.
/// </summary>
public sealed class ActuatorDirectoryHolder : IActuatorDirectory
{
    private readonly object _gate = new();
    private ActuatorDirectory _current = ActuatorDirectory.Empty;
    private ActuatorDirectory _retiring = ActuatorDirectory.Empty;
    private HashSet<(DeviceName Device, DeviceCommandKind Kind, int Stage)> _retiringLeft = [];
    private readonly List<(EntityId Entity, ActuatorService Service, string? Option)> _permits = [];
    private ImmutableDictionary<EntityId, ImmutableArray<string>> _offered = ImmutableDictionary<EntityId, ImmutableArray<string>>.Empty;
    private SwitchboardConfiguration? _configuration;
    private bool _valid;

    /// <summary>
    /// The options Home Assistant reported for the entities it reports (absent: not known, never
    /// "none"); published by the signal store runner whenever they change.
    /// </summary>
    public void ObserveOptions(ImmutableDictionary<EntityId, ImmutableArray<string>> reported)
    {
        ArgumentNullException.ThrowIfNull(reported);
        lock (_gate)
        {
            _offered = reported;
        }
    }

    /// <summary>A configuration result was applied: <paramref name="configuration"/> is null while the document is invalid or missing.</summary>
    public void Observe(SwitchboardConfiguration? configuration)
    {
        lock (_gate)
        {
            if (ReferenceEquals(configuration, _configuration) && _valid == (configuration is not null))
            {
                return;
            }

            _permits.Clear();
            if (configuration is null)
            {
                // Keep the last valid declarations: the safe state's offs still reach them (04 §8).
                _valid = false;
                _retiring = ActuatorDirectory.Empty;
                _retiringLeft = [];
                return;
            }

            var next = ActuatorDirectory.From(configuration);
            var previous = _current;
            _retiringLeft = [.. previous.Devices.Where(d => !next.Devices.Contains(d)).SelectMany(d => previous.OffSlots(d).Select(slot => (d, slot.Kind, slot.Stage)))];
            _retiring = _retiringLeft.Count > 0 ? previous : ActuatorDirectory.Empty;
            _current = next;
            _configuration = configuration;
            _valid = true;
        }
    }

    public ActuatorResolution Resolve(DeviceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (command.Retiring)
            {
                var slot = (command.Device, command.Kind, command.Stage);
                if (command.On || !_retiringLeft.Contains(slot))
                {
                    return new ActuatorResolution(null, ActuatorRefusal.NotDeclared, $"{command.Device} has no retiring entry for this command");
                }

                var retired = _retiring.Resolve(command, decreasesOnly: true, offOnly: true, _offered);
                if (retired.Call is { } final)
                {
                    // Single use: one off per actuator of the removed device (a turn_off, or a select's
                    // stop option), then it is gone.
                    _permits.Add((final.Entity, final.Service, final.Option));
                    _retiringLeft.Remove(slot);
                }

                return retired;
            }

            return _current.Resolve(command, decreasesOnly: !_valid || command.Final, offered: _offered);
        }
    }

    public bool IsDeclared(EntityId entity, string domain, ActuatorService service, double? value, string? option)
    {
        lock (_gate)
        {
            // A permit is exactly the call its retiring entry resolved: the same entity, the off
            // service and, for a select, the same stop option; a value never passes.
            var permit = _permits.FindIndex(p => p.Entity == entity && p.Service == service && p.Option == option);
            if (permit >= 0 && service is ActuatorService.TurnOff or ActuatorService.SelectOption && value is null)
            {
                _permits.RemoveAt(permit);
                return true;
            }

            return _current.IsDeclared(entity, domain, service, value, option, decreasesOnly: !_valid, _offered);
        }
    }
}
