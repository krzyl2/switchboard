#!/usr/bin/env python3
"""Generates synthetic-dispatch-day.csv (standard library only, deterministic).

A committed morning at the reference installation for the dispatcher's replay tests (VER-04,
VER-02) and for development replay (SWITCHBOARD_DEV_REPLAY): same shape and entity IDs as the live
recording, so deploy/examples/switchboard.yaml maps it as it is. Physically consistent: every load
below is part of its meter phase's power and current, so the balance check (SIG-09, with the
charger's own draw as a controlled load) never rejects a sample.

  * main meter (SolarEdge M1 shape) polled every 10 s: per-phase current (unsigned, the magnitude
    of the net flow) 10 ms after the poll, per-phase power (positive = export) 15 ms after,
    voltages once; every poll alternates the current by 0.01 A so history has a row for each;
  * inverter every 30 s: asleep (unavailable) until 06:30, then a ramp to 6 kW at 09:30, split
    evenly over the phases;
  * house base load L1 600 W, L2 400 W, L3 500 W;
  * heater relays switch.grzalka_1/2/3 (Modbus, on_change): reported "off" once at the start and
    then only when someone else switches them;
  * go-e charger: current setpoint number.goe_422752_amp at 20 A all morning; its own draw
    (nrg_4 in A, nrg_7 in W, every 2.5 s while it changes) is 0 except while the car charges.

Events (meter phase A = L1, B = L2, C = L3, as the example maps them):

  06:06-06:16  kettle 2 kW on L3       shortly after the L3 element's would-start: the reduction
                                        waits for its min_on (deferred), then the would-stop
  07:10-07:13  kettle 2 kW on L3       would-stop at once, would-start again after the kettle
  07:40:05     switch.grzalka_1 on      someone else (Node-RED) switches the L2 element: 1.7 kW on L2
  07:46:05     switch.grzalka_1 off     ... and off again: an observed step of -1.7 kW
  08:10:05     switch.grzalka_3 on      the L3 element switched by someone else, thermostat open:
  08:16:05     switch.grzalka_3 off     no power change at all (an observation that does not match)
  08:30-08:33  kettle 2 kW on L2
  09:00-09:40  car charges 16 A on L1   the idle charger's 20 A setpoint was a ceiling, not a draw
  09:20-09:23  kettle 2 kW on L1        while the car charges
  09:40-10:00  car charges 18 A on L1   above a 16 A carport breaker (a test declares that group)
  10:30-10:45  oven 3.5 kW on L3        with the L3 meter power silent 10:30:00-10:33:00 (fourth
                                        column last_reported): a settling window opened then
                                        times out (lost sample)

Run: python tests/fixtures/replay/make-synthetic-dispatch.py   (rewrites the CSV next to this script)
"""

import csv
import os
import random
from datetime import datetime, timedelta, timezone

START = datetime(2026, 10, 22, 6, 0, 0, tzinfo=timezone.utc)
END = START + timedelta(hours=5)
POLL = timedelta(seconds=10)
INVERTER_POLL = timedelta(seconds=30)
CHARGER_POLL = timedelta(milliseconds=2500)
VOLTS = 230.0
PHASES = ("a", "b", "c")
BASE_W = {"a": 600.0, "b": 400.0, "c": 500.0}
PV_FROM = START + timedelta(minutes=30)
PV_FULL_AT = START + timedelta(hours=3, minutes=30)
PV_PEAK_W = 6000.0
HEATER_W = 1700.0


def at(hours, minutes, seconds=0):
    return START.replace(hour=hours, minute=minutes, second=seconds)


# (phase, from, to, watts): loads of the house that Switchboard does not control.
LOADS = [
    ("c", at(6, 6), at(6, 16), 2000.0),
    ("c", at(7, 10), at(7, 13), 2000.0),
    ("b", at(8, 30), at(8, 33), 2000.0),
    ("a", at(9, 20), at(9, 23), 2000.0),
    ("c", at(10, 30), at(10, 45), 3500.0),
]
# The L2 element switched on and off by someone else: it really draws (switch.grzalka_1 is meter B).
EXTERNAL_HEATER = ("switch.grzalka_1", "b", at(7, 40, 5), at(7, 46, 5))
# The L3 element switched by someone else with its thermostat open: it draws nothing.
THERMOSTAT_OPEN = ("switch.grzalka_3", at(8, 10, 5), at(8, 16, 5))
# (from, to, amperes) the car draws on L1.
CHARGING = [(at(9, 0), at(9, 40), 16.0), (at(9, 40), at(10, 0), 18.0)]
FROZEN_POWER_C = (at(10, 30), at(10, 33))

rng = random.Random(20261022)
rows = []


def ts(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.") + f"{moment.microsecond // 1000:03d}Z"


def add(entity, moment, state, last_reported=None):
    rows.append((moment, entity, state, ts(last_reported) if last_reported else ""))


def production(moment):
    if moment < PV_FROM:
        return 0.0
    return PV_PEAK_W * min(1.0, (moment - PV_FROM) / (PV_FULL_AT - PV_FROM))


def charger_amps(moment):
    for start, end, amps in CHARGING:
        if start <= moment < end:
            return amps
    return 0.0


def load(phase, moment):
    watts = BASE_W[phase]
    for load_phase, start, end, extra in LOADS:
        if load_phase == phase and start <= moment < end:
            watts += extra
    _, heater_phase, on, off = EXTERNAL_HEATER
    if heater_phase == phase and on <= moment < off:
        watts += HEATER_W
    if phase == "a":
        watts += charger_amps(moment) * VOLTS
    return watts


# Device states: one row each at the start; history records changes only.
for heater in ("switch.grzalka_1", "switch.grzalka_2", "switch.grzalka_3"):
    add(heater, START + timedelta(milliseconds=1), "off")
add("number.goe_422752_amp", START + timedelta(milliseconds=2), "20")
add("sensor.goe_422752_nrg_4", START + timedelta(milliseconds=3), "0.0")
add("sensor.goe_422752_nrg_7", START + timedelta(milliseconds=3), "0")
entity, _, on, off = EXTERNAL_HEATER
add(entity, on, "on")
add(entity, off, "off")
entity, on, off = THERMOSTAT_OPEN
add(entity, on, "on")
add(entity, off, "off")

# Inverter: asleep (unavailable) until production starts, then every 30 s.
moment = START
asleep = True
while moment < END:
    watts = production(moment)
    if watts <= 0:
        if asleep and moment == START:
            add("sensor.solaredge_ac_power", moment + timedelta(milliseconds=4), "unavailable")
            for phase in PHASES:
                add(f"sensor.solaredge_ac_current_{phase}", moment + timedelta(milliseconds=5), "unavailable")
    else:
        asleep = False
        add("sensor.solaredge_ac_power", moment + timedelta(milliseconds=4), f"{watts:.0f}")
        for phase in PHASES:
            add(f"sensor.solaredge_ac_current_{phase}", moment + timedelta(milliseconds=5), f"{watts / 3 / VOLTS:.2f}")
    moment += INVERTER_POLL

# Charger draw: every 2.5 s while the car charges (small variation, so each is a row), and the
# step back to 0 once.
moment = START
was_charging = False
while moment < END:
    amps = charger_amps(moment)
    if amps > 0:
        drawn = amps + rng.uniform(-0.15, 0.15)
        add("sensor.goe_422752_nrg_4", moment + timedelta(milliseconds=1), f"{drawn:.2f}")
        add("sensor.goe_422752_nrg_7", moment + timedelta(milliseconds=2), f"{drawn * VOLTS:.0f}")
        was_charging = True
    elif was_charging:
        add("sensor.goe_422752_nrg_4", moment + timedelta(milliseconds=1), "0.0")
        add("sensor.goe_422752_nrg_7", moment + timedelta(milliseconds=2), "0")
        was_charging = False
    moment += CHARGER_POLL

# Main meter: every 10 s.
index = 0
moment = START
while moment < END:
    share = production(moment) / 3
    for offset, phase in enumerate(PHASES):
        net_import = load(phase, moment) + rng.uniform(-20, 20) - share
        amps = abs(net_import) / VOLTS + (0.01 if index % 2 else 0.0)
        add(f"sensor.solaredge_m1_ac_current_{phase}", moment + timedelta(milliseconds=10 + offset), f"{amps:.2f}")
        power_time = moment + timedelta(milliseconds=15 + offset)
        power_entity = f"sensor.solaredge_m1_ac_power_{phase}"
        if phase == "c" and moment == FROZEN_POWER_C[0]:
            # Last report before the power stops reporting until FROZEN_POWER_C[1] (a lost sample).
            add(power_entity, power_time, f"{-net_import:.1f}", last_reported=power_time)
        elif not (phase == "c" and FROZEN_POWER_C[0] < moment < FROZEN_POWER_C[1]):
            add(power_entity, power_time, f"{-net_import:.1f}")
        if moment == START:
            add(f"sensor.solaredge_m1_ac_voltage_{phase}n", moment + timedelta(milliseconds=20 + offset), f"{VOLTS:.1f}")
    index += 1
    moment += POLL

rows.sort(key=lambda r: r[0])
target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "synthetic-dispatch-day.csv")
with open(target, "w", newline="", encoding="utf-8") as handle:
    writer = csv.writer(handle, lineterminator="\n")
    writer.writerow(["entity_id", "ts_utc", "state", "last_reported"])
    for moment, entity, state, last_reported in rows:
        writer.writerow([entity, ts(moment), state, last_reported])
print(f"{len(rows)} rows -> {target}")
