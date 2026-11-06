using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Ev;
using Switchboard.Application.Health;
using Switchboard.Domain.Ev;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The EV module's manual controls as MQTT entities (EV-09) through the real link and the real topic guard. Why it matters: two selects and a number on
/// Home Assistant's dashboard are a way into the add-on, so they exist and are listened to only while an <c>ev:</c> section is in force, a command is exactly one of
/// a select's options or a number in (0, 100], a retained message never acts on its own, and the add-on can never publish to a command topic of its own (it would
/// command itself). Without the add-on option <c>mqtt_enabling_commands</c> MQTT may only take the grid away (PV only, hold, the end of a charge-now) and set the
/// number: any client of the broker can publish there, and auto, full charge, charge now and the end of a hold can buy grid power.
/// </summary>
public sealed class EvEntitiesTests : IAsyncDisposable
{
    private const string ModeTopic = "switchboard/ev_mode/set";
    private const string TargetTopic = "switchboard/ev_charge_now_target/set";
    private const string CommandTopic = "switchboard/ev_command/set";

    private readonly Control _control = new();
    private readonly ObservableTimeProvider _time = new();
    private readonly FakeMqttTransport _transport;
    private readonly HomeAssistantConnectionState _homeAssistant = new();
    private readonly MqttConnectionState _mqttState = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly MemoryLedger _ledger = new();
    private Task? _run;

    public EvEntitiesTests()
    {
        _transport = new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard", null, () => _control.Configured == true));
    }

    private sealed class Control : IEvControl
    {
        public EvManual Manual { get; set; } = EvManual.Default;

        public bool? Configured { get; set; } = true;

        public double? DefaultChargeNowTargetPercent => 90;

        public List<string> Calls { get; } = [];

        public event EventHandler? Changed;

        public EvControlResult SetMode(EvMode mode)
        {
            Calls.Add($"mode {mode}");
            Manual = Manual with { Mode = mode };
            Changed?.Invoke(this, EventArgs.Empty);
            return EvControlResult.Done;
        }

        public EvControlResult StartChargeNow(double? targetPercent, TimeSpan? duration)
        {
            Calls.Add($"start {targetPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
            return EvControlResult.Done;
        }

        public EvControlResult CancelChargeNow()
        {
            Calls.Add("cancel charge now");
            Manual = Manual with { ChargeNow = null };
            Changed?.Invoke(this, EventArgs.Empty);
            return EvControlResult.Done;
        }

        public EvControlResult StartHold(TimeSpan? duration)
        {
            Calls.Add("hold");
            Manual = Manual with { ChargeNow = null, Hold = new EvHold(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(3)) };
            Changed?.Invoke(this, EventArgs.Empty);
            return EvControlResult.Done;
        }

        public EvControlResult CancelHold()
        {
            Calls.Add("cancel hold");
            Manual = Manual with { Hold = null };
            Changed?.Invoke(this, EventArgs.Empty);
            return EvControlResult.Done;
        }
    }

    private static EvChargeNow Running => new(DateTimeOffset.UnixEpoch, 80, DateTimeOffset.UnixEpoch.AddHours(12));

    [Fact]
    public async Task The_three_entities_are_announced_with_the_frozen_identity_and_their_command_topics_are_subscribed()
    {
        var session = await StartAsync();

        var configs = session.Where(o => o.Topic.EndsWith("/config", StringComparison.Ordinal) && o.Payload.Length > 0 && o.Topic.Contains("/ev_", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["homeassistant/number/switchboard/ev_charge_now_target/config", "homeassistant/select/switchboard/ev_command/config", "homeassistant/select/switchboard/ev_mode/config"],
            configs.Select(o => o.Topic).Order());
        Assert.All(configs, o => Assert.True(o.Retain));
        var select = JsonDocument.Parse(configs.Single(o => o.Topic.Contains("select/switchboard/ev_mode", StringComparison.Ordinal)).Payload).RootElement;
        Assert.Equal(["auto", "pv_only", "full_charge"], select.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal("switchboard_ev_mode", select.GetProperty("unique_id").GetString());
        Assert.Equal(ModeTopic, select.GetProperty("command_topic").GetString());
        Assert.False(select.GetProperty("retain").GetBoolean());
        var command = JsonDocument.Parse(configs.Single(o => o.Topic.Contains("select/switchboard/ev_command", StringComparison.Ordinal)).Payload).RootElement;
        Assert.Equal(["auto", "charge_now", "hold"], command.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal("switchboard_ev_command", command.GetProperty("unique_id").GetString());
        Assert.Equal(CommandTopic, command.GetProperty("command_topic").GetString());
        Assert.False(command.GetProperty("optimistic").GetBoolean());
        Assert.Equal(
            new[] { ModeTopic, TargetTopic, CommandTopic }.Order(StringComparer.Ordinal),
            session.Where(o => o.Kind == "subscribe" && o.Topic.Contains("/ev_", StringComparison.Ordinal)).Select(o => o.Topic).Order(StringComparer.Ordinal));
        Assert.Contains(session, o => o.Topic == "switchboard/ev_mode/state" && o.Payload == "auto");
        Assert.Contains(session, o => o.Topic == "switchboard/ev_command/state" && o.Payload == "auto");
        Assert.Contains(session, o => o.Topic == "switchboard/ev_charge_now_target/state" && o.Payload == "90");
    }

    [Fact]
    public async Task The_command_select_shows_the_command_in_force_and_auto_once_it_ended()
    {
        // Why: the select is not optimistic; it must say what runs (a charge-now that ended at its target shows auto again, without anyone choosing it).
        _control.Manual = _control.Manual with { ChargeNow = Running };
        var session = await StartAsync(enablingCommands: true);
        Assert.Contains(session, o => o.Topic == "switchboard/ev_command/state" && o.Payload == "charge_now");

        await _transport.DeliverAsync(CommandTopic, "hold");
        await _transport.UntilAsync(o => o.Topic == "switchboard/ev_command/state" && o.Payload == "hold");

        await _transport.DeliverAsync(CommandTopic, "auto");
        await _transport.UntilAsync(o => o.Topic == "switchboard/ev_command/state" && o.Payload == "auto");
        Assert.Equal(["hold", "cancel hold"], _control.Calls);
    }

    [Fact]
    public async Task An_upgrade_retracts_the_old_charge_now_button()
    {
        // Why: 2026-11-06 the button became the command select; a button left in Home Assistant would publish to a topic nobody listens to.
        var ledger = new MemoryLedger("select/ev_mode", "number/ev_charge_now_target", "button/ev_charge_now");

        var session = await StartAsync(ledger);

        Assert.Contains(session, o => o.Topic == "homeassistant/button/switchboard/ev_charge_now/config" && o.Payload.Length == 0 && o.Retain);
        Assert.Contains(session, o => o.Topic == "homeassistant/select/switchboard/ev_command/config" && o.Payload.Length > 0);
    }

    [Fact]
    public async Task A_mode_command_is_exactly_one_of_the_options()
    {
        await StartAsync(enablingCommands: true);

        foreach (var payload in new[] { "off", "AUTO", "pv-only", "", "auto\n", "force_on" })
        {
            await _transport.DeliverAsync(ModeTopic, payload);
        }

        Assert.Empty(_control.Calls);

        await _transport.DeliverAsync(ModeTopic, "full_charge");

        Assert.Equal(["mode FullCharge"], _control.Calls);
        await _transport.UntilAsync(o => o.Topic == "switchboard/ev_mode/state" && o.Payload == "full_charge");
    }

    [Fact]
    public async Task Charge_now_starts_a_charge_now_with_the_number_that_was_set_and_nothing_else_does()
    {
        await StartAsync(enablingCommands: true);

        await _transport.DeliverAsync(TargetTopic, "75");
        await _transport.UntilAsync(o => o.Topic == "switchboard/ev_charge_now_target/state" && o.Payload == "75");
        foreach (var payload in new[] { "PRESS", "Charge_now", "charge-now", "", "charge_now\n", "force_on" })
        {
            await _transport.DeliverAsync(CommandTopic, payload);
        }

        Assert.Empty(_control.Calls);

        await _transport.DeliverAsync(CommandTopic, "charge_now");

        Assert.Equal(["start 75"], _control.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("101")]
    [InlineData("NaN")]
    [InlineData("many")]
    [InlineData("")]
    public async Task A_target_outside_zero_to_one_hundred_is_ignored_and_the_default_stays(string payload)
    {
        await StartAsync(enablingCommands: true);

        await _transport.DeliverAsync(TargetTopic, payload);
        await _transport.DeliverAsync(CommandTopic, "charge_now");

        Assert.Equal(["start 90"], _control.Calls);
    }

    [Fact]
    public async Task Without_the_option_MQTT_can_only_take_the_grid_away_and_set_the_target()
    {
        // auto and full_charge can charge from the grid, charge_now starts a grid charge at once and ending a hold gives the grid back: by default no broker
        // client may do any of it. PV only, hold and ending a charge-now lower what the module draws, and the number changes nothing by itself, so they stay open.
        _control.Manual = _control.Manual with { Mode = EvMode.PvOnly };
        await StartAsync();

        await _transport.DeliverAsync(ModeTopic, "full_charge");

        // The select is set back to the mode in force.
        await _transport.UntilAsync(o => o.Topic == "switchboard/ev_mode/state" && o.Payload == "pv_only");
        await _transport.DeliverAsync(ModeTopic, "auto");
        await _transport.DeliverAsync(TargetTopic, "75");
        await _transport.UntilAsync(o => o.Topic == "switchboard/ev_charge_now_target/state" && o.Payload == "75");
        await _transport.DeliverAsync(CommandTopic, "charge_now");
        Assert.Empty(_control.Calls);

        await _transport.DeliverAsync(ModeTopic, "pv_only");
        await _transport.DeliverAsync(CommandTopic, "hold");
        await _transport.DeliverAsync(CommandTopic, "auto");
        await _transport.UntilAsync(o => o.Topic == "switchboard/ev_command/state" && o.Payload == "hold");

        Assert.Equal(["mode PvOnly", "hold"], _control.Calls);

        _control.Manual = _control.Manual with { Hold = null, ChargeNow = Running };
        await _transport.DeliverAsync(CommandTopic, "auto");

        Assert.Equal(["mode PvOnly", "hold", "cancel charge now"], _control.Calls);
    }

    [Fact]
    public async Task With_the_option_MQTT_can_choose_every_mode_and_every_command()
    {
        await StartAsync(enablingCommands: true);

        await _transport.DeliverAsync(ModeTopic, "pv_only");
        await _transport.DeliverAsync(ModeTopic, "auto");
        await _transport.DeliverAsync(ModeTopic, "full_charge");
        await _transport.DeliverAsync(CommandTopic, "charge_now");
        await _transport.DeliverAsync(CommandTopic, "hold");
        await _transport.DeliverAsync(CommandTopic, "auto");

        Assert.Equal(["mode PvOnly", "mode Auto", "mode FullCharge", "start 90", "hold", "cancel hold"], _control.Calls);
    }

    [Fact]
    public async Task A_retained_command_never_acts()
    {
        // Why: a command left in the broker (by anyone) must not change the car's mode or start a charge at the next connect, even with the option on.
        await StartAsync(enablingCommands: true);

        await _transport.DeliverAsync(ModeTopic, "pv_only", retained: true);
        await _transport.DeliverAsync(CommandTopic, "charge_now", retained: true);
        await _transport.DeliverAsync(CommandTopic, "hold", retained: true);
        await _transport.DeliverAsync(TargetTopic, "50", retained: true);

        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task Without_an_ev_section_the_entities_are_retracted_and_no_command_topic_is_subscribed()
    {
        // Why: the module is not configured, so its controls must not exist; entities no longer declared are retracted like any other (identity is an API).
        _control.Configured = false;
        var ledger = new MemoryLedger("select/ev_mode", "number/ev_charge_now_target", "select/ev_command");

        var session = await StartAsync(ledger);

        Assert.DoesNotContain(session, o => o.Kind == "subscribe" && o.Topic.Contains("/ev_", StringComparison.Ordinal));
        Assert.Contains(session, o => o.Topic == "homeassistant/select/switchboard/ev_mode/config" && o.Payload.Length == 0 && o.Retain);
        Assert.Contains(session, o => o.Topic == "homeassistant/select/switchboard/ev_command/config" && o.Payload.Length == 0 && o.Retain);
    }

    [Fact]
    public async Task While_no_valid_configuration_is_known_nothing_is_retracted_and_nothing_is_listened_to()
    {
        // Why: a broken switchboard.yaml must never delete the user's entities, and must not open a command topic either.
        _control.Configured = null;
        var ledger = new MemoryLedger("select/ev_mode", "number/ev_charge_now_target", "select/ev_command");

        var session = await StartAsync(ledger);

        Assert.DoesNotContain(session, o => o.Kind == "publish" && o.Topic.Contains("/ev_", StringComparison.Ordinal) && o.Payload.Length == 0);
        Assert.DoesNotContain(session, o => o.Kind == "subscribe" && o.Topic.Contains("/ev_", StringComparison.Ordinal));
    }

    [Fact]
    public void The_topic_guard_lets_in_exactly_the_three_command_topics_and_only_with_an_ev_section()
    {
        var with = new MqttTopicGuard("homeassistant", "switchboard", null, () => true);
        var without = new MqttTopicGuard("homeassistant", "switchboard", null, () => false);
        var plain = new MqttTopicGuard("homeassistant", "switchboard");

        Assert.All(new[] { ModeTopic, TargetTopic, CommandTopic }, topic =>
        {
            Assert.True(with.IsSubscriptionAllowed(topic), topic);
            Assert.False(without.IsSubscriptionAllowed(topic), topic);
            Assert.False(plain.IsSubscriptionAllowed(topic), topic);

            // The add-on may never publish to a command topic of its own: a retained ON there would command itself.
            Assert.False(with.IsAllowed(topic), topic);
        });
        Assert.All(
            new[] { "switchboard/+/set", "switchboard/#", "switchboard/ev_mode/state", "switchboard/ev_mode/set/x", "switchboard/ev_other/set", "switchboard/ev_charge_now_target/set/", "other/ev_mode/set", "switchboard/ev_mode" },
            topic => Assert.False(with.IsSubscriptionAllowed(topic), topic));
        Assert.True(with.IsAllowed("switchboard/ev_mode/state"));
    }

    private async Task<List<MqttOperation>> StartAsync(MemoryLedger? ledger = null, bool enablingCommands = false)
    {
        var entities = new EvEntities(_control, MsOptions.Create(TestOptions.Mqtt()), new AppVersion("0.11.9"), NullLogger<EvEntities>.Instance, enablingCommands);
        var link = new MqttLink(
            _transport,
            new FakeCredentialSource(),
            _mqttState,
            new HealthEvaluator(_homeAssistant, _mqttState),
            MsOptions.Create(TestOptions.Mqtt()),
            TestOptions.MqttClientId,
            MsOptions.Create(TestOptions.Heartbeat()),
            new AppVersion("0.11.9"),
            [entities],
            ledger ?? _ledger,
            _time,
            NullLogger<MqttLink>.Instance);
        _run = link.RunAsync(_stop.Token);

        // The session ends its set-up with the command subscriptions: Home Assistant's birth topic, then the sources' topics (the last of them in ordinal order is
        // the mode select's); without any of those, the birth topic is the last.
        var last = _control.Configured == true ? ModeTopic : "homeassistant/status";
        return await _transport.UntilAsync(o => o.Kind == "subscribe" && o.Topic == last);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.Soon();
        }

        _stop.Dispose();
    }
}
