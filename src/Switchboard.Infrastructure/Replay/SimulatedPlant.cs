using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Infrastructure.Replay;

/// <summary>
/// One simulated load of the development plant: the phase it really loads (which may differ from
/// what the configuration declares: a deliberately swapped phase) and its step. A relay draws
/// <see cref="PowerW"/> and <see cref="CurrentA"/> while on; a setpoint actuator draws its value in
/// amperes at <see cref="Volts"/>.
/// </summary>
public sealed record PlantLoad(Phase Phase, double? PowerW, double? CurrentA, double? Volts);

/// <summary>
/// The development plant (SWITCHBOARD_DEV_PLANT, a JSON file):
/// <c>{"switch.x": {"phase": "L1", "power_w": 1700, "current_a": 7.0}, "number.y": {"phase": "L1", "volts": 230}}</c>.
/// Read strictly: anything else is refused at start.
/// </summary>
public static class PlantDefinition
{
    public static ImmutableDictionary<EntityId, PlantLoad> Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{path}: the plant is an object of entity IDs.");
        }

        var loads = ImmutableDictionary.CreateBuilder<EntityId, PlantLoad>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!EntityId.TryParse(property.Name, out var entity) || property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"{path}: '{property.Name}' is not an entity ID with an object.");
            }

            double? Number(string key) => property.Value.TryGetProperty(key, out var value) ? value.GetDouble() : null;
            var phaseText = property.Value.TryGetProperty("phase", out var phaseValue) ? phaseValue.GetString() : null;
            if (!Enum.TryParse<Phase>(phaseText, ignoreCase: false, out var phase) || !Enum.IsDefined(phase)
                || property.Value.EnumerateObject().Any(p => p.Name is not ("phase" or "power_w" or "current_a" or "volts")))
            {
                throw new FormatException($"{path}: '{property.Name}' needs a phase (L1, L2, L3) and only power_w, current_a or volts.");
            }

            loads.Add(entity.Value, new PlantLoad(phase, Number("power_w"), Number("current_a"), Number("volts")));
        }

        return loads.ToImmutable();
    }
}

/// <summary>
/// A development stand-in for the house's loads in replay: a command the development sink
/// acknowledged switches the simulated load, its entity reports the new state at once (as a Modbus
/// relay does), and from the next replayed meter sample on, that phase's power and current carry
/// the difference between the load as commanded and the load as recorded: a relay the recording
/// shows on (its draw already in the meter) and that was switched off takes its step away, one the
/// recording shows off and that was switched on adds it. A setpoint load's recorded draw is not
/// known to the plant: it adds its commanded value (meant for loads the recording leaves idle).
/// Power follows the loaded signal's sign convention (positive_export subtracts); an unsigned
/// current is signed by the same poll's power first, the step added, and the magnitude written, so
/// a step during export lowers the reading as on the real meter. Refused in add-on mode (replay
/// only); never in a production path.
/// </summary>
public sealed class SimulatedPlant : IReplayOverlay
{
    private readonly ImmutableDictionary<EntityId, PlantLoad> _loads;
    private readonly ReplayData _data;
    private readonly Func<SwitchboardConfiguration?> _configuration;
    private readonly object _gate = new();
    private readonly Dictionary<EntityId, (string State, double Watts, double Amps)> _switched = [];
    private ReplaySignalSource? _source;

    /// <param name="configuration">The configuration in force, for the meter's phase signals (entity, unit, sign).</param>
    public SimulatedPlant(ImmutableDictionary<EntityId, PlantLoad> loads, ReplayData data, Func<SwitchboardConfiguration?> configuration)
    {
        _loads = loads;
        _data = data;
        _configuration = configuration;
    }

    /// <summary>The replay source the plant reports through; set when both exist.</summary>
    public void Attach(ReplaySignalSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        source.Overlay = this;
    }

    /// <summary>A call the sink acknowledged: the simulated load follows it, and its entity reports the new state now.</summary>
    public void Apply(ActuatorCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_loads.TryGetValue(call.Entity, out var load))
        {
            return;
        }

        string state;
        double watts, amps;
        switch (call.Service)
        {
            case ActuatorService.TurnOn:
                (state, watts, amps) = ("on", load.PowerW ?? 0, load.CurrentA ?? 0);
                break;
            case ActuatorService.TurnOff:
                (state, watts, amps) = ("off", 0, 0);
                break;
            default:
                var value = call.Value ?? 0;
                (state, watts, amps) = (value.ToString("0.###", CultureInfo.InvariantCulture), value * (load.Volts ?? 0), value);
                break;
        }

        lock (_gate)
        {
            _switched[call.Entity] = (state, watts, amps);
        }

        _source?.Inject(call.Entity, state);
    }

    public string Transform(EntityId entity, string state, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_switched.TryGetValue(entity, out var own))
            {
                // Once commanded, the simulated load's own state wins over the recording.
                return own.State;
            }

            if (_switched.Count == 0 || _configuration() is not { } configuration)
            {
                return state;
            }

            var signal = configuration.Signals.FirstOrDefault(s => s.Entity == entity && s.Role is SignalRole.PhasePower or SignalRole.PhaseCurrent);
            if (signal?.Phase is not { } phase || !double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
            {
                return state;
            }

            var steps = _switched.Where(s => _loads[s.Key].Phase == phase).ToList();
            var stepW = steps.Sum(s => s.Value.Watts - Recorded(s.Key, at).Watts);
            var stepA = steps.Sum(s => s.Value.Amps - Recorded(s.Key, at).Amps);
            var factor = SignalUnits.Factor(signal.Unit ?? (signal.Role == SignalRole.PhasePower ? "W" : "A"));
            if (signal.Role == SignalRole.PhasePower)
            {
                var import = Import(raw * factor, signal.Sign) + stepW;
                return Format(Raw(import, signal.Sign) / factor);
            }

            var magnitude = Math.Abs(raw * factor);
            double signed;
            if (signal.Sign == SignConvention.Unsigned)
            {
                // Signed by the same poll's power (the direction source), as the signal layer does.
                var power = configuration.Signals.FirstOrDefault(s => s.Role == SignalRole.PhasePower && s.Phase == phase);
                var powerRow = power is null ? null : _data.LatestAtOrBefore(power.Entity, at);
                var direction = powerRow is not null && double.TryParse(powerRow.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                    ? Import(p * SignalUnits.Factor(power!.Unit ?? "W"), power.Sign)
                    : 0;
                signed = (direction < 0 ? -magnitude : magnitude) + stepA;
                return Format(Math.Abs(signed) / factor);
            }

            signed = Import(raw * factor, signal.Sign) + stepA;
            return Format(Raw(signed, signal.Sign) / factor);
        }
    }

    /// <summary>The load's draw the recording already holds at <paramref name="at"/>: a relay's step while the recording shows it on; nothing for a setpoint load.</summary>
    private (double Watts, double Amps) Recorded(EntityId entity, DateTimeOffset at)
    {
        var load = _loads[entity];
        if (load.Volts is not null)
        {
            return (0, 0);
        }

        return _data.LatestAtOrBefore(entity, at)?.State == "on" ? (load.PowerW ?? 0, load.CurrentA ?? 0) : (0, 0);
    }

    private static double Import(double value, SignConvention? sign) => sign == SignConvention.PositiveExport ? -value : value;

    private static double Raw(double import, SignConvention? sign) => sign == SignConvention.PositiveExport ? -import : import;

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
