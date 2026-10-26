using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Infrastructure.Mqtt;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// Which entities the command path refuses as Switchboard's own: exactly the objects the add-on
/// announced over MQTT discovery (the ledger) and its watchdog entity, by exact entity ID, so an
/// entity of another integration with a similar name still passes.
/// </summary>
public sealed class OwnMqttEntitiesTests
{
    private static readonly string[] Announced = ["switch/control_allowed", "switch/kill_switch", "select/device_heater_l1_override", "sensor/mode"];

    [Theory]
    [InlineData("switch.switchboard_control_allowed")]
    [InlineData("switch.switchboard_kill_switch")]
    [InlineData("select.switchboard_device_heater_l1_override")]
    [InlineData("sensor.switchboard_mode")]
    [InlineData("binary_sensor.switchboard_healthy")]
    public void An_object_the_add_on_announced_is_its_own(string entity)
    {
        Assert.True(new OwnMqttEntities(new Ledger(Announced)).IsOwn(EntityId.Parse(entity)));
    }

    [Theory]
    [InlineData("switch.switchboard_control_allowed_2")] // another integration's switch named alike
    [InlineData("switch.my_switchboard_control_allowed")]
    [InlineData("switch.control_allowed")]
    [InlineData("sensor.switchboard_control_allowed")] // the announced object, but under another domain
    [InlineData("select.switchboard_device_heater_l2_override")] // not announced (yet)
    [InlineData("switch.switchboard_heater")]
    [InlineData("switch.heater_1")]
    public void A_similar_name_of_another_entity_is_not(string entity)
    {
        Assert.False(new OwnMqttEntities(new Ledger(Announced)).IsOwn(EntityId.Parse(entity)));
    }

    [Fact]
    public void The_watchdog_entity_is_own_before_anything_else_was_announced_and_the_rest_is_not()
    {
        var own = new OwnMqttEntities(new Ledger([]));

        Assert.True(own.IsOwn(EntityId.Parse("binary_sensor.switchboard_healthy")));
        Assert.False(own.IsOwn(EntityId.Parse("switch.switchboard_kill_switch")));
    }

    [Theory]
    [InlineData("switch.switchboard_kill_switch", "switch/kill_switch")]
    [InlineData("select.switchboard_device_heater_l1_override", "select/device_heater_l1_override")]
    [InlineData("switch.heater_1", null)]
    [InlineData("switchboard_kill_switch", null)]
    public void The_ledger_key_of_a_default_entity_id(string entityId, string? key)
    {
        Assert.Equal(key, MqttIdentity.LedgerKeyOf(entityId));
        if (key is { } known)
        {
            var parts = known.Split('/');
            Assert.Equal(entityId, MqttIdentity.DefaultEntityId(parts[0], parts[1]));
        }
    }

    private sealed class Ledger(IEnumerable<string> keys) : IDiscoveryLedger
    {
        private ImmutableSortedSet<string> _keys = ImmutableSortedSet.CreateRange(StringComparer.Ordinal, keys);

        public ImmutableSortedSet<string> Discovered => _keys;

        public void Replace(IEnumerable<string> discovered) => _keys = ImmutableSortedSet.CreateRange(StringComparer.Ordinal, discovered);
    }
}
