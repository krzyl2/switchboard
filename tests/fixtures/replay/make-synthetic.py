#!/usr/bin/env python3
"""Generates synthetic-three-phase-import-export.csv (standard library only, deterministic).

A committed stand-in for the gitignored live recording, with the same shape (Home Assistant history:
one row per state change, entity IDs of the reference installation) and every situation the signal
tests need, at known times:

  * main meter (SolarEdge M1 shape) polled every 10 s: per-phase current (unsigned), per-phase power
    (positive = export) and voltage; each poll writes the currents a few ms before the powers, as the
    live meter does in ~90 % of polls;
  * inverter polled every 30 s: total power and per-phase currents;
  * EV charger (go-e shape) every 2.5 s, L1 current and power; every fourth value repeats, so Home
    Assistant records no row for it (doubling that interval); its force-state select (the example's
    stop actuator, 0.7.1) reported "0" (neutral) once at the start;
  * 12 min of import (little sun), then 13 min of export (full sun);
  * one out-of-step inverter row at 10:18:01 (a cloud reported before the meter sees it);
  * the L2 current frozen for 3 min from 10:08:00 (fourth column last_reported marks it);
  * the L3 voltage unavailable from 10:20:00 to 10:21:00.

Run: python tests/fixtures/replay/make-synthetic.py   (rewrites the CSV next to this script)
"""

import csv
import os
import random
from datetime import datetime, timedelta, timezone

START = datetime(2026, 10, 21, 10, 0, 0, tzinfo=timezone.utc)
DURATION = timedelta(minutes=25)
EXPORT_FROM = START + timedelta(minutes=12)
FROZEN_FROM = START + timedelta(minutes=8)
FROZEN_FOR = timedelta(minutes=3)
OUT_OF_STEP_AT = START + timedelta(minutes=18, seconds=1)
VOLTAGE_GAP = (START + timedelta(minutes=20), START + timedelta(minutes=21))

PHASES = ("a", "b", "c")
HOUSE_LOAD_W = {"a": 800.0, "b": 500.0, "c": 1200.0}
VOLTS = 230.0

rng = random.Random(20261021)
rows = []


def ts(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.") + f"{moment.microsecond // 1000:03d}Z"


last_state = {}


def add(entity, moment, state, last_reported=None):
    # History records changes only: a repeated value produces no row.
    if last_state.get(entity) == state and last_reported is None:
        return
    last_state[entity] = state
    rows.append((moment, entity, state, ts(last_reported) if last_reported else ""))


def production(moment):
    return 300.0 if moment < EXPORT_FROM else 6000.0


# Inverter: every 30 s, production split evenly; currents unsigned.
moment = START
while moment < START + DURATION:
    watts = production(moment) + rng.uniform(-40, 40)
    add("sensor.solaredge_ac_power", moment + timedelta(milliseconds=2), f"{watts:.0f}")
    for phase in PHASES:
        add(f"sensor.solaredge_ac_current_{phase}", moment + timedelta(milliseconds=4), f"{watts / 3 / VOLTS:.2f}")
    moment += timedelta(seconds=30)

# The cloud reported a poll early: production collapses one second after a meter poll that still
# shows full export, so balance sees an impossible negative house load and rejects this sample.
add("sensor.solaredge_ac_power", OUT_OF_STEP_AT, "200")

# Main meter: every 10 s; currents first, powers a few ms later, then voltages.
moment = START
last_frozen_value = None
while moment < START + DURATION:
    share = production(moment) / 3
    for index, phase in enumerate(PHASES):
        load = HOUSE_LOAD_W[phase] + rng.uniform(-60, 60)
        export = share - load
        current = abs(export) / VOLTS + 0.05
        current_at = moment + timedelta(milliseconds=10 + index)
        power_at = moment + timedelta(milliseconds=15 + index)
        frozen = phase == "b" and FROZEN_FROM <= moment < FROZEN_FROM + FROZEN_FOR
        if phase == "b" and moment == FROZEN_FROM:
            # Last report before the sensor stops: it stays at this value, unreported, for 3 min.
            add(f"sensor.solaredge_m1_ac_current_{phase}", current_at, f"{current:.3f}", last_reported=current_at)
        elif not frozen:
            add(f"sensor.solaredge_m1_ac_current_{phase}", current_at, f"{current:.3f}")
        add(f"sensor.solaredge_m1_ac_power_{phase}", power_at, f"{export:.1f}")
        voltage_at = moment + timedelta(milliseconds=20 + index)
        voltage_entity = f"sensor.solaredge_m1_ac_voltage_{phase}n"
        if phase == "c" and VOLTAGE_GAP[0] <= moment < VOLTAGE_GAP[1]:
            if moment == VOLTAGE_GAP[0]:
                add(voltage_entity, voltage_at, "unavailable")
        else:
            add(voltage_entity, voltage_at, f"{VOLTS + rng.uniform(-3, 3):.1f}")
    moment += timedelta(seconds=10)

# EV charger: every 2.5 s on L1; every fourth value repeats (no row).
moment = START
sample = 0
while moment < START + DURATION:
    if sample % 4 != 3:
        amps = 16.0 + rng.uniform(-0.3, 0.3)
        add("sensor.goe_422752_nrg_4", moment + timedelta(milliseconds=1), f"{amps:.2f}")
        add("sensor.goe_422752_nrg_7", moment + timedelta(milliseconds=2), f"{amps * VOLTS:.0f}")
    sample += 1
    moment += timedelta(milliseconds=2500)

# The charger's force-state select: neutral, reported once (a select reports only on change).
add("select.goe_422752_frc", START + timedelta(milliseconds=3), "0")

rows.sort(key=lambda r: r[0])
target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "synthetic-three-phase-import-export.csv")
with open(target, "w", newline="", encoding="utf-8") as handle:
    writer = csv.writer(handle, lineterminator="\n")
    writer.writerow(["entity_id", "ts_utc", "state", "last_reported"])
    for moment, entity, state, last_reported in rows:
        writer.writerow([entity, ts(moment), state, last_reported])
print(f"{len(rows)} rows -> {target}")
