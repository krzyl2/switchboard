#!/usr/bin/env python3
"""Generates the synthetic-house discovery fixtures (standard library only, deterministic).

What Home Assistant would answer the setup wizard's three read-only queries in the house of
synthetic-dispatch-day.csv (tests/fixtures/replay): get_states (states.json), config/entity_registry/list
(entity_registry.json) and config/device_registry/list (device_registry.json), in HA's own shapes.
Synthetic: no real tokens, ids or household values. Development replay reads them through
SWITCHBOARD_DEV_DISCOVERY; .NET tests read them directly.

Besides the primary measurements it holds the derived sensors the wizard must refuse as inputs
(template, statistics, powercalc) and one YAML template sensor without a unique_id, which is not in
the entity registry, so its source cannot be verified. The charger's force-state select (its stop,
0.7.1) carries its options, and one of Switchboard's own MQTT selects shows that the add-on's own
entities are recognised (never offered as an actuator).

Run: python tests/fixtures/discovery/make-synthetic-house.py   (rewrites synthetic-house/ next to this script)
"""

import json
import os

AT = "2026-10-22T06:00:00.000000+00:00"

# entity_id, state, unit, device_class, state_class, friendly name, platform (None: not in the registry), device
ENTITIES = [
    ("sensor.solaredge_m1_ac_current_a", "2.61", "A", "current", "measurement", "M1 AC current A", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_current_b", "1.74", "A", "current", "measurement", "M1 AC current B", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_current_c", "2.17", "A", "current", "measurement", "M1 AC current C", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_power_a", "-600.0", "W", "power", "measurement", "M1 AC power A", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_power_b", "-400.0", "W", "power", "measurement", "M1 AC power B", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_power_c", "-500.0", "W", "power", "measurement", "M1 AC power C", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_power", "-1500.0", "W", "power", "measurement", "M1 AC power", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_voltage_an", "230.0", "V", "voltage", "measurement", "M1 AC voltage AN", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_voltage_bn", "230.0", "V", "voltage", "measurement", "M1 AC voltage BN", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_m1_ac_voltage_cn", "230.0", "V", "voltage", "measurement", "M1 AC voltage CN", "solaredge_modbus", "dev_m1"),
    ("sensor.solaredge_ac_power", "unavailable", "W", "power", "measurement", "Inverter AC power", "solaredge_modbus", "dev_inverter"),
    ("sensor.solaredge_ac_current_a", "unavailable", "A", "current", "measurement", "Inverter AC current A", "solaredge_modbus", "dev_inverter"),
    ("sensor.solaredge_ac_current_b", "unavailable", "A", "current", "measurement", "Inverter AC current B", "solaredge_modbus", "dev_inverter"),
    ("sensor.solaredge_ac_current_c", "unavailable", "A", "current", "measurement", "Inverter AC current C", "solaredge_modbus", "dev_inverter"),
    ("switch.grzalka_1", "off", None, "switch", None, "Grzałka 1", "modbus", "dev_relays"),
    ("switch.grzalka_2", "off", None, "switch", None, "Grzałka 2", "modbus", "dev_relays"),
    ("switch.grzalka_3", "off", None, "switch", None, "Grzałka 3", "modbus", "dev_relays"),
    ("number.goe_422752_amp", "20", "A", "current", None, "go-e charging current", "goecharger_api2", "dev_goe"),
    ("sensor.goe_422752_nrg_4", "0.0", "A", "current", "measurement", "go-e current L1", "goecharger_api2", "dev_goe"),
    ("sensor.goe_422752_nrg_5", "0.0", "A", "current", "measurement", "go-e current L2", "goecharger_api2", "dev_goe"),
    ("sensor.goe_422752_nrg_6", "0.0", "A", "current", "measurement", "go-e current L3", "goecharger_api2", "dev_goe"),
    ("sensor.goe_422752_nrg_7", "0", "W", "power", "measurement", "go-e power L1", "goecharger_api2", "dev_goe"),
    ("binary_sensor.goe_422752_car_0", "off", None, "plug", None, "go-e car connected", "goecharger_api2", "dev_goe"),
    # The go-e force state: its options are what the wizard offers as stop and resume options.
    ("select.goe_422752_frc", "0", None, None, None, "go-e force state", "goecharger_api2", "dev_goe"),
    # One of Switchboard's own MQTT entities (unique_id switchboard_<object_id>): never an actuator.
    ("select.switchboard_ev_charger_override", "auto", None, None, None, "Switchboard ev_charger override", "mqtt", "dev_switchboard"),
    # Derived sensors: never a control input (docs/07-PITFALLS.md).
    ("sensor.usage", "1500.0", "W", "power", "measurement", "Usage", "template", None),
    ("sensor.moc_grzalek_cwu", "0.0", "W", "power", "measurement", "Moc grzałek CWU", "template", None),
    ("sensor.zuzycie_srednie_5m", "1480.0", "W", "power", "measurement", "Zużycie średnie 5 min", "statistics", None),
    ("sensor.grzalki_srednie_5m", "0.0", "W", "power", "measurement", "Grzałki średnie 5 min", "statistics", None),
    ("sensor.kettle_power", "0.0", "W", "power", "measurement", "Kettle power (estimated)", "powercalc", None),
    # A YAML template sensor without a unique_id: not in the entity registry, so it cannot be verified.
    ("sensor.house_load_yaml", "1500.0", "W", "power", "measurement", "House load (YAML)", None, None),
    # Unrelated entities the pickers must filter out.
    ("light.kitchen", "on", None, None, None, "Kitchen", "hue", None),
    ("binary_sensor.front_door", "off", None, "door", None, "Front door", "zha", None),
    ("sensor.outdoor_temperature", "7.5", "°C", "temperature", "measurement", "Outdoor temperature", "met", None),
]

DEVICES = [
    {"id": "dev_m1", "name": "SolarEdge Modbus Meter M1", "name_by_user": None, "manufacturer": "SolarEdge", "model": "WND-3Y-400-MB"},
    {"id": "dev_inverter", "name": "SolarEdge SE7K", "name_by_user": None, "manufacturer": "SolarEdge", "model": "SE7K-RWS"},
    {"id": "dev_relays", "name": "Modbus relays", "name_by_user": "Przekaźniki CWU", "manufacturer": None, "model": None},
    {"id": "dev_goe", "name": "go-e Charger 422752", "name_by_user": None, "manufacturer": "go-e", "model": "Gemini flex"},
    {"id": "dev_switchboard", "name": "Switchboard", "name_by_user": None, "manufacturer": None, "model": "Home Assistant add-on"},
]

# The options attribute of the selects (what HA reports with their state).
OPTIONS = {
    "select.goe_422752_frc": ["0", "1", "2"],
    "select.switchboard_ev_charger_override": ["auto", "force_on", "force_off"],
}

# Registry unique_ids that follow an integration's own scheme instead of the synthetic default.
UNIQUE_IDS = {
    "select.switchboard_ev_charger_override": "switchboard_ev_charger_override",
}


def state(entity_id, value, unit, device_class, state_class, name):
    attributes = {"friendly_name": name}
    if unit is not None:
        attributes["unit_of_measurement"] = unit
    if device_class is not None:
        attributes["device_class"] = device_class
    if state_class is not None:
        attributes["state_class"] = state_class
    if entity_id in OPTIONS:
        attributes["options"] = OPTIONS[entity_id]
    return {
        "entity_id": entity_id,
        "state": value,
        "attributes": attributes,
        "last_changed": AT,
        "last_reported": AT,
        "last_updated": AT,
        "context": {"id": "01JSYNTHETIC0000000000000000", "parent_id": None, "user_id": None},
    }


def registry(entity_id, platform, device_id, name):
    return {
        "area_id": None,
        "categories": {},
        "config_entry_id": None if platform in ("template", "statistics") and device_id is None else "01JSYNTHETICENTRY0000000000",
        "device_id": device_id,
        "disabled_by": None,
        "entity_category": None,
        "entity_id": entity_id,
        "has_entity_name": False,
        "hidden_by": None,
        "icon": None,
        "id": entity_id.replace(".", "_"),
        "labels": [],
        "name": None,
        "options": {},
        "original_name": name,
        "platform": platform,
        "translation_key": None,
        "unique_id": UNIQUE_IDS.get(entity_id, entity_id.replace(".", "_") + "_uid"),
    }


states = [state(e[0], e[1], e[2], e[3], e[4], e[5]) for e in ENTITIES]
entities = [registry(e[0], e[6], e[7], e[5]) for e in ENTITIES if e[6] is not None]
target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "synthetic-house")
os.makedirs(target, exist_ok=True)
for name, payload in (("states.json", states), ("entity_registry.json", entities), ("device_registry.json", DEVICES)):
    with open(os.path.join(target, name), "w", newline="\n", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
print(f"{len(states)} states, {len(entities)} registry entries, {len(DEVICES)} devices -> {target}")
