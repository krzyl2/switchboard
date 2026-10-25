using System.Text;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The one state-changing message (Stage C, SAFE-05): a call_service leaves only in its exact shape,
/// for one of eight domain/service pairs, on one entity of that domain, and only when the
/// configuration loaded at send time declares that entity as an actuator allowing that service,
/// value and option. Anything else is refused before the socket is touched: a second "type" key, a
/// list of targets, an area, a return_response, a value on a relay, an option anywhere but a
/// select's select_option, any other select service.
/// </summary>
public sealed class CallServiceAllowListTests
{
    /// <summary>The declared-actuator check as a test sees it: everything declared, unless the test says otherwise.</summary>
    private static readonly ActuatorDeclaredCheck Declared = (_, _, _, _, _) => true;

    [Theory]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_on","target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"input_boolean","service":"turn_on","target":{"entity_id":"input_boolean.pool"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"input_boolean","service":"turn_off","target":{"entity_id":"input_boolean.pool"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"number","service":"set_value","service_data":{"value":16},"target":{"entity_id":"number.charger_current"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"input_number","service":"set_value","service_data":{"value":6.5},"target":{"entity_id":"input_number.battery_current"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":"1"},"target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"input_select","service":"select_option","service_data":{"option":"stop"},"target":{"entity_id":"input_select.charger_mode"}}""")]
    public void Each_pair_passes_in_its_exact_shape(string payload)
    {
        HomeAssistantCommandAllowList.EnsureCallServiceAllowed(Encoding.UTF8.GetBytes(payload), Declared);
    }

    [Fact]
    public void The_list_holds_exactly_eight_pairs()
    {
        // Relays and switch stops on and off, setpoints set_value, select stops select_option (0.7.1);
        // nothing else (no toggle, no light, no script, no select_next, no set_options) can be asked for.
        Assert.Equal(
            [
                "input_boolean.turn_off", "input_boolean.turn_on", "input_number.set_value", "input_select.select_option", "number.set_value",
                "select.select_option", "switch.turn_off", "switch.turn_on",
            ],
            HomeAssistantCommandAllowList.CallServicePairs.Select(p => $"{p.Domain}.{p.Service}").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ActuatorService.TurnOff, null, null, """{"id":7,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData(ActuatorService.SetValue, 16.0, null, """{"id":7,"type":"call_service","domain":"number","service":"set_value","service_data":{"value":16},"target":{"entity_id":"number.charger_current"}}""")]
    [InlineData(ActuatorService.SelectOption, null, "1", """{"id":7,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":"1"},"target":{"entity_id":"select.charger_force"}}""")]
    public void The_one_serialization_writes_exactly_that_shape(ActuatorService service, double? value, string? option, string expected)
    {
        var entity = EntityId.Parse(service switch
        {
            ActuatorService.SetValue => "number.charger_current",
            ActuatorService.SelectOption => "select.charger_force",
            _ => "switch.heater_1",
        });
        var payload = CallServicePayload.Build(7, new ActuatorCall(entity, entity.Value[..entity.Value.IndexOf('.', StringComparison.Ordinal)], service, value) { Option = option });

        Assert.Equal(expected, Encoding.UTF8.GetString(payload.Span));
        HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload.Span, Declared);
    }

    [Theory]
    // Shape: a list of targets, an area or a device, a second entity, no target at all.
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":["switch.heater_1","switch.heater_2"]}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"area_id":"kitchen"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"device_id":"abc"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1","area_id":"kitchen"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off"}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"all"}}""")]
    // An extra key: return_response, service_data on a relay, entity_id beside target.
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"},"return_response":true}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_on","service_data":{},"target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_on","service_data":{"value":1},"target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_on","entity_id":"switch.heater_1","target":{"entity_id":"switch.heater_1"}}""")]
    // set_value without its value, with a text value, with a second key, beyond a double.
    [InlineData("""{"id":5,"type":"call_service","domain":"number","service":"set_value","target":{"entity_id":"number.charger_current"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"number","service":"set_value","service_data":{"value":"16"},"target":{"entity_id":"number.charger_current"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"number","service":"set_value","service_data":{"value":16,"entity_id":"number.other"},"target":{"entity_id":"number.charger_current"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"number","service":"set_value","service_data":{"value":1e400},"target":{"entity_id":"number.charger_current"}}""")]
    // A pair that is not on the list, or a domain that is not the entity's.
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"toggle","target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"light","service":"turn_on","target":{"entity_id":"light.kitchen"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"homeassistant","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"script","service":"turn_on","target":{"entity_id":"script.everything"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_on","target":{"entity_id":"input_boolean.pool"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"number","service":"set_value","service_data":{"value":16},"target":{"entity_id":"input_number.charger"}}""")]
    // select_option without service_data, with a number, an empty text, a value beside the option,
    // an entity_id inside service_data, a value instead of the option.
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":1},"target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":""},"target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":"1","value":1},"target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":"1","entity_id":"select.other"},"target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"value":1},"target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":["1"]},"target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":"1"},"entity_id":"select.charger_force","target":{"entity_id":"select.charger_force"}}""")]
    // An option where only a value belongs, or on a relay; other select services; select_option on a switch.
    [InlineData("""{"id":5,"type":"call_service","domain":"number","service":"set_value","service_data":{"option":"16"},"target":{"entity_id":"number.charger_current"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","service_data":{"option":"1"},"target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_next","target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_first","target":{"entity_id":"select.charger_force"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"input_select","service":"set_options","service_data":{"options":["1"]},"target":{"entity_id":"input_select.charger_mode"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"select_option","service_data":{"option":"1"},"target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":"1"},"target":{"entity_id":"input_select.charger_mode"}}""")]
    // Duplicate keys (Home Assistant keeps the last), another type, not one object.
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","domain":"light","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"call_service","type":"fire_event","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""")]
    [InlineData("""{"id":5,"type":"fire_event","event_type":"x"}""")]
    [InlineData("""{"id":5,"type":"execute_script","sequence":[]}""")]
    [InlineData("""[{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}}]""")]
    [InlineData("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}} {"id":6}""")]
    public void Anything_beyond_the_shape_is_refused(string payload)
    {
        Assert.Throws<HomeAssistantCommandRefusedException>(() => HomeAssistantCommandAllowList.EnsureCallServiceAllowed(Encoding.UTF8.GetBytes(payload), Declared));
    }

    [Fact]
    public void An_entity_the_configuration_loaded_now_does_not_declare_is_refused()
    {
        // The second, independent check at send time (SAFE-05): the shape alone never suffices.
        var payload = Encoding.UTF8.GetBytes("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""");
        (EntityId Entity, string Domain, ActuatorService Service, double? Value, string? Option)? asked = null;

        Assert.Throws<HomeAssistantCommandRefusedException>(() => HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload, (e, d, s, v, o) =>
        {
            asked = (e, d, s, v, o);
            return false;
        }));
        Assert.Equal((EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, (double?)null, (string?)null), asked);
    }

    [Fact]
    public void The_value_and_the_service_reach_the_declared_check()
    {
        var payload = Encoding.UTF8.GetBytes("""{"id":5,"type":"call_service","domain":"number","service":"set_value","service_data":{"value":12.5},"target":{"entity_id":"number.charger_current"}}""");
        (ActuatorService Service, double? Value)? asked = null;

        HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload, (_, _, s, v, _) =>
        {
            asked = (s, v);
            return true;
        });

        Assert.Equal((ActuatorService.SetValue, (double?)12.5), asked);
    }

    [Fact]
    public void The_option_reaches_the_declared_check()
    {
        // The send-time check judges the option itself (one of the device's two declared options), so
        // a correct shape with another option is still refused there.
        var payload = Encoding.UTF8.GetBytes("""{"id":5,"type":"call_service","domain":"select","service":"select_option","service_data":{"option":"2"},"target":{"entity_id":"select.charger_force"}}""");
        (EntityId Entity, ActuatorService Service, double? Value, string? Option)? asked = null;

        Assert.Throws<HomeAssistantCommandRefusedException>(() => HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload, (e, _, s, v, o) =>
        {
            asked = (e, s, v, o);
            return o != "2";
        }));
        Assert.Equal((EntityId.Parse("select.charger_force"), ActuatorService.SelectOption, (double?)null, "2"), asked);
    }

    [Fact]
    public void The_read_only_path_never_passes_a_service_call()
    {
        // A call_service has exactly one way out; the generic command path refuses it in any shape.
        var payload = Encoding.UTF8.GetBytes("""{"id":5,"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""");

        Assert.Throws<HomeAssistantCommandRefusedException>(() => HomeAssistantCommandAllowList.EnsurePayloadAllowed(payload));
    }
}
