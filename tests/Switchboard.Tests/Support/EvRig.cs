using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Ev;
using Switchboard.Application.Goals;
using Switchboard.Application.Guard;
using Switchboard.Application.Options;
using Switchboard.Application.Planning;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// The EV module end to end on a virtual clock, without the guard: a real signal store feeds the EV inputs, the charger's power is a stub the test sets (the guard
/// would publish it every cycle), <c>ev.json</c> is in memory (or a real <see cref="EvStateFile"/>), and the engine, the keeper and the goal source are the real
/// ones. The clock runs in Europe/Warsaw.
/// </summary>
internal sealed class EvRig : IEvInputSource, IPresentationSource, IPresentationStore<EvState>
{
    public const string Charger =
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    sources: pv_and_zone:offpeak\n";

    private const string Tariff = """
        economics:
          currency: PLN
          tariff:
            default_zone: offpeak
            zones:
              - id: peak
                price: 1.60
                when:
                  - { days: [workday], hours: ["07:00-13:00"] }
              - id: offpeak
                price: 0.60
                when: []
          settlement:
            model: hourly_netting
            export_price: 0.35

        """;

    public const string EvBlock = """
        ev:
          device: ev_charger
          capacity_kwh: 18
          soc: { entity: sensor.car_battery_level, max_age: 30m }
          range: { entity: sensor.car_electric_range, max_age: 1h }
          plugged: { entity: sensor.charger_car_state, states: [Charging, "Wait for car", complete] }
          calendar: { entity: calendar.kursy, max_age: 24h }

        """;

    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private readonly Func<EvState?> _initial;
    private readonly IPresentationStore<EvState>? _store;
    private readonly Func<IEvInputSource> _inputs;

    public EvRig(string planner = "planner:\n  dispatch: off\n", string ev = EvBlock, IPresentationStore<EvState>? store = null, EvState? initial = null, Func<IEvInputSource>? inputs = null, string extraDevices = "")
    {
        H = new StoreHarness(TestDevices.Document(Charger + extraDevices, extra: Tariff + "\n" + planner + "\n" + ev, defaultStart: "headroom"));
        H.Time.SetLocalTimeZone(Warsaw);
        _store = store;
        _initial = () => initial;
        _inputs = inputs ?? (() => this);
        Clock = new SignalClock(H.Time);
        Engine = null!;
        Keeper = null!;
        Source = null!;
        Manual = null!;
        Control = null!;
        GoalStore = new ManualGoalStore(
            new GoalInputsOf(H),
            Clock,
            NullLogger<ManualGoalStore>.Instance,
            new EvModuleSocSource(() => Keeper, Clock));
        Restart();
    }

    /// <summary>A restart of the add-on: a new engine, keeper and source over the same signals, with ev.json read back as it was written.</summary>
    public void Restart()
    {
        var options = Options.Create(new StatisticsOptions
        {
            BucketLength = TimeSpan.FromMinutes(5),
            Retention = TimeSpan.FromHours(26),
            MaxIntegrationGap = TimeSpan.FromMinutes(2),
            SaveInterval = TimeSpan.FromMinutes(1),
            MaxFileBytes = 1024 * 1024,
            VoltageLossFraction = 0.5,
            ExcursionPreRoll = TimeSpan.FromMinutes(2),
            ExcursionPostRoll = TimeSpan.FromMinutes(2),
            ExcursionMaxSamples = 720,
            ExcursionMaxDecisions = 50,
        });
        var delegating = new Delegating(() => _inputs().Current);
        Manual = new EvManualState();
        Engine = new EvEngine(delegating, this, Plans, _store ?? this, device => ManualGoal || GoalStore.Current.Goals.ContainsKey(device), Clock, options, NullLogger<EvEngine>.Instance, Manual);
        Keeper = new EvSnapshotKeeper(Engine, delegating, Clock, Log.For<EvSnapshotKeeper>());
        Source = new EvGoalSource(Keeper, device => ManualGoal || GoalStore.Current.Goals.ContainsKey(device));
        Control = new EvControl(Manual, delegating, Keeper, GoalStore, Trigger, Clock, NullLogger<EvControl>.Instance);
    }

    public StoreHarness H { get; }

    public SignalClock Clock { get; }

    public PlanHolder Plans { get; } = new();

    public EvEngine Engine { get; private set; }

    public EvSnapshotKeeper Keeper { get; private set; }

    public EvGoalSource Source { get; private set; }

    public EvManualState Manual { get; private set; }

    public EvControl Control { get; private set; }

    /// <summary>The manual goals of the add-on (the real store); charge-now is a goal in it.</summary>
    public ManualGoalStore GoalStore { get; }

    public CountingTrigger Trigger { get; } = new();

    /// <summary>What the manual goal of the EV device states now (the plan's view of charge-now).</summary>
    public GoalOffer ManualOffer() =>
        GoalStore.Goals(new GoalContext(Now, Configuration, GoalInputs.Empty, Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance)));

    public LogCapture Log { get; } = new();

    public bool ManualGoal { get; set; }

    /// <summary>The charger's power the guard would publish each cycle; null means no usable power picture (a stale draw).</summary>
    public double? ChargerWatts { get; set; } = 0;

    public EvState? Saved { get; private set; }

    public int Saves { get; private set; }

    public DateTimeOffset Now => H.Now;

    public SwitchboardConfiguration Configuration => H.Configuration.Configuration!;

    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Warsaw), TimeSpan.Zero);

    public void Advance(TimeSpan by) => H.Advance(by);

    public void AdvanceTo(DateTimeOffset when) => H.AdvanceTo(when);

    // A report newer than the previous one, as Home Assistant stamps them: the store ignores one that is not.
    private void Tock() => H.Advance(TimeSpan.FromMilliseconds(1));

    public void PushSoc(double percent)
    {
        Tock();
        H.Push("sensor.car_battery_level", percent.ToString(System.Globalization.CultureInfo.InvariantCulture), unit: "%");
    }

    public void PushRange(double km)
    {
        Tock();
        H.Push("sensor.car_electric_range", km.ToString(System.Globalization.CultureInfo.InvariantCulture), unit: "km");
    }

    public void PushPlugged(string state = "Charging")
    {
        Tock();
        H.Push("sensor.charger_car_state", state, unit: null);
    }

    public void PushCalendar(CalendarEvent? calendarEvent, string state = "on")
    {
        Tock();
        H.Store.Apply(
            EntityUpdateBatch.Of(
                BatchOrigin.Change,
                new EntityUpdate(EntityId.Parse("calendar.kursy"), state, null, Now, Now) { Calendar = new ParsedCalendar(calendarEvent, CalendarProblem.None) }),
            Now);
    }

    public CalendarEvent Trip(TimeSpan fromNow, double km, string title = "Praca") =>
        new(title, $"km: {km.ToString(System.Globalization.CultureInfo.InvariantCulture)}", Now + fromNow, Now + fromNow + TimeSpan.FromHours(1), false);

    /// <summary>A car that is plugged in, reporting its SoC, with an empty calendar: the quiet baseline most rows start from.</summary>
    public void PlugInCar(double soc = 40)
    {
        PushSoc(soc);
        PushPlugged();
        PushCalendar(null);
    }

    /// <summary>Lets time pass one minute at a time with a computation each: the guard publishes the charger's power every cycle, and a longer jump would be a gap in it.</summary>
    public void Cruise(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromMinutes(1))
        {
            Advance(TimeSpan.FromMinutes(1));
            Keeper.Tick();
        }
    }

    public EvSnapshot Tick()
    {
        Keeper.Tick();
        return Keeper.Current.Snapshot ?? throw new InvalidOperationException("the module published no snapshot");
    }

    public GoalOffer Offer(DateTimeOffset? at = null) =>
        Source.Goals(new GoalContext(at ?? Now, Configuration, GoalInputs.Empty, Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance)));

    /// <summary>Ticks, then offers at the same instant: the offer the guard's step would see.</summary>
    public GoalOffer TickAndOffer()
    {
        Tick();
        return Offer();
    }

    // IEvInputSource
    EvInputView IEvInputSource.Current => new(H.Configuration, EvInputs.From(H.Snapshot(), Configuration.Ev!));

    // IPresentationSource
    public GuardStep? LastStep => null;

    public PowerView? Power => ChargerWatts is { } watts ? TestSteps.View(Now, 0, 0, ("ev_charger", watts, PowerSource.Measured)) : null;

    public StatisticsSnapshot Statistics => throw new NotSupportedException();

    public ExcursionsSnapshot Excursions => throw new NotSupportedException();

    public PresentationFileReport Report => throw new NotSupportedException();

    // IPresentationStore<EvState>
    public PresentationLoad<EvState> Load()
    {
        var state = Saved ?? _initial();
        return new PresentationLoad<EvState>(state, state is null ? StateFileStatus.Fresh : StateFileStatus.Loaded, null);
    }

    public void Save(EvState state)
    {
        Saved = state;
        Saves++;
    }

    internal sealed class CountingTrigger : IPlanTrigger
    {
        public int Wakes { get; private set; }

        public void Wake() => Wakes++;
    }

    private sealed class GoalInputsOf(StoreHarness harness) : IGoalInputSource
    {
        public GoalInputView Current => new(harness.Configuration, GoalInputs.From(harness.Snapshot()));
    }

    private sealed class Delegating(Func<EvInputView> current) : IEvInputSource
    {
        public EvInputView Current => current();
    }
}
