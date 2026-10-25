#!/usr/bin/env python3
"""Generates synthetic-phase-self-test.csv (standard library only, deterministic).

One hour for the passive phase self-test (CONN-05): someone else switches relays while Switchboard
watches; Switchboard itself switches nothing. Main meter polled every 10 s: per-phase power
(sensor.selftest_power_a/b/c, W, positive = import) and per-phase current (sensor.selftest_current_a/b/c,
A, unsigned: the magnitude of the net flow, alternating by 0.01 A so every poll is a row). Base load
A 500 W, B 400 W, C 600 W, noise +-15 W.

Events (meter phases A/B/C are mapped to L1/L2/L3 by the tests):

  12:05:05 / 12:10:05  switch.selftest_heater on / off   1.7 kW on meter phase C: declared on L2,
                                                         this is the swapped L2/L3 case (mismatch L3)
  12:15:05 / 12:20:05  switch.selftest_heater on / off   with a 2 kW kettle on phase A 12:15:20-12:17:00:
                                                         the switch-on is not conclusive (foreign step)
  12:25:05 / 12:27:05  switch.selftest_cold on / off     a heater whose thermostat is open: no step
  12:30:05 / 12:32:05  switch.selftest_small on / off    150 W on phase B: below the meter's noise
  12:35:05 / 12:37:05  switch.selftest_cold2 on / off    another heater with its thermostat open, while
  12:35:10 / 12:37:10  switch.selftest_neighbour on / off  a declared 1.7 kW relay on phase C switches 5 s
                                                         later: a step on C that is not cold2's (not isolated)
  12:40:00-12:55:00    PV exports 2.2 kW on phase C
  12:45:05 / 12:50:05  switch.selftest_heater on / off   while phase C exports: the unsigned current
                                                         falls when the heater starts (wrong direction)

Run: python tests/fixtures/replay/make-synthetic-self-test.py   (rewrites the CSV next to this script)
"""

import csv
import os
import random
from datetime import datetime, timedelta, timezone

START = datetime(2026, 10, 22, 12, 0, 0, tzinfo=timezone.utc)
END = START + timedelta(hours=1)
POLL = timedelta(seconds=10)
VOLTS = 230.0
PHASES = ("a", "b", "c")
BASE_W = {"a": 500.0, "b": 400.0, "c": 600.0}
HEATER_W = 1700.0


def at(minutes, seconds=0):
    return START + timedelta(minutes=minutes, seconds=seconds)


HEATER = [(at(5, 5), at(10, 5)), (at(15, 5), at(20, 5)), (at(45, 5), at(50, 5))]
COLD = [(at(25, 5), at(27, 5))]
SMALL = [(at(30, 5), at(32, 5))]
COLD2 = [(at(35, 5), at(37, 5))]
NEIGHBOUR = [(at(35, 10), at(37, 10))]
KETTLE = ("a", at(15, 20), at(17, 0), 2000.0)
EXPORT = ("c", at(40, 0), at(55, 0), -2200.0)

rng = random.Random(20261022)
rows = []


def ts(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.") + f"{moment.microsecond // 1000:03d}Z"


def add(entity, moment, state):
    rows.append((moment, entity, state))


def on(spans, moment):
    return any(start <= moment < end for start, end in spans)


def load(phase, moment):
    watts = BASE_W[phase]
    if phase == "c" and on(HEATER, moment):
        watts += HEATER_W
    if phase == "c" and on(NEIGHBOUR, moment):
        watts += HEATER_W
    if phase == "b" and on(SMALL, moment):
        watts += 150.0
    kettle_phase, start, end, extra = KETTLE
    if phase == kettle_phase and start <= moment < end:
        watts += extra
    export_phase, start, end, extra = EXPORT
    if phase == export_phase and start <= moment < end:
        watts += extra
    return watts


RELAYS = (
    ("switch.selftest_heater", HEATER),
    ("switch.selftest_cold", COLD),
    ("switch.selftest_small", SMALL),
    ("switch.selftest_cold2", COLD2),
    ("switch.selftest_neighbour", NEIGHBOUR),
)
for entity, _ in RELAYS:
    add(entity, START + timedelta(milliseconds=1), "off")
for entity, spans in RELAYS:
    for start, end in spans:
        add(entity, start, "on")
        add(entity, end, "off")

index = 0
moment = START
while moment < END:
    for offset, phase in enumerate(PHASES):
        net = load(phase, moment) + rng.uniform(-15, 15)
        add(f"sensor.selftest_power_{phase}", moment + timedelta(milliseconds=10 + offset), f"{net:.1f}")
        amps = abs(net) / VOLTS + (0.01 if index % 2 else 0.0)
        add(f"sensor.selftest_current_{phase}", moment + timedelta(milliseconds=15 + offset), f"{amps:.2f}")
    index += 1
    moment += POLL

rows.sort(key=lambda r: r[0])
target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "synthetic-phase-self-test.csv")
with open(target, "w", newline="", encoding="utf-8") as handle:
    writer = csv.writer(handle, lineterminator="\n")
    writer.writerow(["entity_id", "ts_utc", "state"])
    for moment, entity, state in rows:
        writer.writerow([entity, ts(moment), state])
print(f"{len(rows)} rows -> {target}")
