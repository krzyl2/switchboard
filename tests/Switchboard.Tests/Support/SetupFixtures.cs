using System.Collections.Immutable;
using System.Text;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.Configuration;

namespace Switchboard.Tests.Support;

/// <summary>Documents and drafts for the setup wizard's tests, read and written through the real YAML reader and writer.</summary>
internal static class SetupFixtures
{
    public static readonly SetupDefaults Defaults = EmbeddedSetupDefaults.Load();

    public static readonly ControlParameters Control = SwitchboardDocumentParser.BindControl(null, TestConfigurations.Defaults)!;

    public static ConfigNode Tree(string yaml)
    {
        var read = YamlDocumentReader.Read(yaml, ConfigOrigin.Document);
        Assert.True(read.Errors.IsEmpty, string.Join("; ", read.Errors));
        return read.Root!;
    }

    public static string Write(ConfigNode root) => YamlDocumentWriter.Write(root);

    /// <summary>A draft applied to a document, written and parsed back by the runtime parser.</summary>
    public static (string Text, ConfigurationResult Result, MappedDocument Mapped) Apply(string? yaml, SetupDraft draft)
    {
        var mapped = SetupDocumentMapper.Apply(yaml is null ? null : Tree(yaml), draft);
        var text = Write(mapped.Root);
        return (text, TestConfigurations.Parse(text), mapped);
    }

    /// <summary>A structural rendering of a tree (keys, order, values, quoting), without positions.</summary>
    public static string Canon(ConfigNode? node)
    {
        var text = new StringBuilder();
        Render(node, text, 0);
        return text.ToString();
    }

    /// <summary>The maintainer's meter as a clean install would enter it, with three heaters and the charger.</summary>
    public static SetupDraft MaintainerDraft(bool acknowledged = true) => new()
    {
        Connection = new ConnectionDraft
        {
            Topology = "three_phase",
            BreakerRatingA = 25,
            Characteristic = "B",
            VoltageV = 230,
            ReserveW = new PhaseValues { L1 = 2500, L2 = 2500, L3 = 2500 },
        },
        Meter = new MeterDraft
        {
            CurrentSign = "unsigned",
            CurrentDirection = "phase_power",
            PowerSign = "positive_export",
            Phases = new MeterPhases
            {
                L1 = Row("a", "an"),
                L2 = Row("b", "bn"),
                L3 = Row("c", "cn"),
            },
        },
        Sources = new SourcesDraft
        {
            Inverter = new InverterDraft
            {
                Power = Signal("sensor.solaredge_ac_power", "W"),
                Sign = "positive_import",
                UnavailableAsZero = true,
                PhaseCurrents = new InverterPhaseCurrents
                {
                    L1 = Signal("sensor.solaredge_ac_current_a", "A"),
                    L2 = Signal("sensor.solaredge_ac_current_b", "A"),
                    L3 = Signal("sensor.solaredge_ac_current_c", "A"),
                },
            },
        },
        Devices =
        [
            Heater("heater_l1", "switch.grzalka_2", "L1"),
            Heater("heater_l2", "switch.grzalka_1", "L2"),
            Heater("heater_l3", "switch.grzalka_3", "L3"),
            new DeviceDraft
            {
                Id = "ev_charger",
                Name = "Ładowarka go-e",
                Type = "modulating",
                Actuator = "number.goe_422752_amp",
                Phase = "L1",
                Min = 6,
                Max = 20,
                Step = 1,
                ValuePerKwh = 2.03,
                StateReports = "on_change",
                Draw = new DrawDraft { Current = "sensor.goe_422752_nrg_4", Power = "sensor.goe_422752_nrg_7", MaxAge = "60s" },
            },
        ],
        Acknowledgements = new AcknowledgementsDraft { NotProtective = acknowledged },
    };

    public static DeviceDraft Heater(string id, string actuator, string phase) => new()
    {
        Id = id,
        Name = $"Grzałka CWU {phase}",
        Type = "onoff",
        Actuator = actuator,
        Phase = phase,
        RatedCurrentA = 7,
        RatedPowerW = 1700,
        ValuePerKwh = 0.35,
        StateReports = "on_change",
        RotationGroup = "cwu_heaters",
    };

    public static SignalDraft Signal(string entity, string unit, string maxAge = "90s") => new() { Entity = entity, Unit = unit, MaxAge = maxAge };

    private static MeterPhaseDraft Row(string letter, string voltage) => new()
    {
        Current = Signal($"sensor.solaredge_m1_ac_current_{letter}", "A"),
        Power = Signal($"sensor.solaredge_m1_ac_power_{letter}", "W"),
        Voltage = Signal($"sensor.solaredge_m1_ac_voltage_{voltage}", "V"),
    };

    private static void Render(ConfigNode? node, StringBuilder text, int depth)
    {
        var indent = new string(' ', depth * 2);
        switch (node)
        {
            case ConfigMapping mapping:
                foreach (var entry in mapping.Entries)
                {
                    text.Append(indent).Append(entry.Key).Append(':').Append('\n');
                    Render(entry.Value, text, depth + 1);
                }

                break;
            case ConfigSequence sequence:
                foreach (var item in sequence.Items)
                {
                    text.Append(indent).Append('-').Append('\n');
                    Render(item, text, depth + 1);
                }

                break;
            case ConfigScalar scalar:
                text.Append(indent).Append(scalar.IsNull ? "<null>" : (scalar.Quoted ? $"\"{scalar.Value}\"" : scalar.Value)).Append('\n');
                break;
            default:
                text.Append(indent).Append("<none>\n");
                break;
        }
    }
}

/// <summary>Readouts a test sets directly: a state, a unit, a report time and an optional detected refresh per entity.</summary>
internal sealed class FakeReadouts(TimeProvider time) : ICandidateReadouts
{
    private readonly Dictionary<EntityId, (string State, string? Unit, DateTimeOffset Reported, TimeSpan? Refresh)> _values = [];
    private readonly Dictionary<EntityId, List<EntityUpdate>> _history = [];

    public List<IReadOnlyCollection<EntityId>> Leases { get; } = [];

    public int Holds { get; private set; }

    public void Set(string entity, string state, string? unit = null, DateTimeOffset? reported = null, TimeSpan? refresh = null)
    {
        var id = EntityId.Parse(entity);
        var at = reported ?? time.GetUtcNow();
        _values[id] = (state, unit, at, refresh);
        Report(entity, state, unit, at);
    }

    public void Report(string entity, string state, string? unit, DateTimeOffset at)
    {
        var id = EntityId.Parse(entity);
        if (!_history.TryGetValue(id, out var list))
        {
            list = [];
            _history[id] = list;
        }

        list.Add(new EntityUpdate(id, state, unit, at, at));
        if (_values.TryGetValue(id, out var current))
        {
            _values[id] = (state, unit ?? current.Unit, at, current.Refresh);
        }
    }

    public ImmutableArray<CandidateReadout> Lease(IReadOnlyCollection<EntityId> entities)
    {
        Leases.Add(entities);
        return [.. entities.Select(e => Peek(e) ?? new CandidateReadout(e, ReadoutStatus.Pending, null, null, null, null, null, 0, 8))];
    }

    public IDisposable Hold(IReadOnlyCollection<EntityId> entities)
    {
        Holds++;
        return new Released(() => Holds--);
    }

    public CandidateReadout? Peek(EntityId entity) =>
        _values.TryGetValue(entity, out var value)
            ? new CandidateReadout(
                entity,
                value.State is "unavailable" or "unknown" ? ReadoutStatus.Unavailable : ReadoutStatus.Found,
                value.State,
                value.Unit,
                value.Reported,
                time.GetUtcNow() - value.Reported,
                value.Refresh,
                value.Refresh is null ? 0 : 8,
                8)
            : null;

    public ImmutableArray<EntityUpdate> History(EntityId entity) => _history.TryGetValue(entity, out var list) ? [.. list] : [];

    private sealed class Released(Action release) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                release();
            }
        }
    }
}
