#!/usr/bin/env python3
"""Generates synthetic-overload-tier-walk.csv (standard library only, deterministic).

A committed overload walk through every band of the default tier table (3 x 25 A, characteristic B),
for the guard's replay tests (VER-04) and for development replay (SWITCHBOARD_DEV_REPLAY). Same shape
and entity IDs as the live recording, so deploy/examples/switchboard.yaml maps it as it is:

  * main meter (SolarEdge M1 shape) polled every 10 s: per-phase current (unsigned) 10 ms after the
    poll, per-phase power (positive = export) 15 ms after, voltages once. Every poll alternates the
    value by 0.01 A, so Home Assistant history has a row for every poll, as the live meter does;
  * the inverter asleep all night (unavailable), so there is no production;
  * heater elements switch.grzalka_1/2/3 on, the go-e charger current number.goe_422752_amp at 16 A
    and its force-state select select.goe_422752_frc (the example's stop actuator) at "0" (neutral);
  * L1 to L3 at a quiet 8 A between segments; every segment is preceded by 13 min of quiet, so the
    overload budget is full and every limit released before it starts.

Segments (start = first poll above; t0 = the poll before it), on L1 unless noted:

   A  05:00  0.80 x In (20 A) for 3 min               no shed
   B  21:00  exactly 1.00 x In (25.00 / 24.99 A), 3 min  no shed (hold band; up_to is inclusive)
   C  37:00  1.08 x In (27 A) for 16 min              heater at the 2nd cycle boundary; charger reduced
                                                       at t0 + 15:00 (band timer), off at the next poll
   D  66:00  exactly 1.13 x In (28.25 / 28.24 A), 4 min  heater at the 2nd boundary; budget untouched
   E  83:00  1.20 x In (30 A) for 4 min               heater at the 1st boundary; budget 0.5 at t0 + 90 s;
                                                       charger reduced at t0 + 135 s (budget < 0.25)
   F 100:00  1.32 x In (33 A) for 40 s                heater at the entering poll; charger reduced at
                                                       t0 + 22.5 s (budget < 0.25); budget empty at t0 + 30 s
   G 114:00  1.48 x In (37 A) for 20 s                everything on L1 at the entering poll
   H 128:00  1.20 x In, 16 polls; again from 139:20   thermal memory: the first leaves the budget at
     139:20  (8 min 30 s of recovery in between)      0.056 (170 s counted), the second empties it
   I 155:00  L2 current frozen for 3 min (fourth column last_reported): stale 90 s after its last report
   J 165:00  L3 at 30 A of export for 2 min           no shed, no reduction request
   K 170:00  switch.grzalka_3 frozen (last_reported): its state is stale 10 min later, fresh at 182:00

Run: python tests/fixtures/replay/make-synthetic-overload.py   (rewrites the CSV next to this script)
"""

import csv
import os
from datetime import datetime, timedelta, timezone

START = datetime(2026, 10, 21, 22, 0, 0, tzinfo=timezone.utc)
END = START + timedelta(minutes=190)
POLL = timedelta(seconds=10)
VOLTS = 230.0
QUIET_A = 8.0
PHASES = ("a", "b", "c")


def minutes(m, s=0):
    return START + timedelta(minutes=m, seconds=s)


# (phase, first poll above, last poll above, low value, high value): the current alternates between
# the two values poll by poll, so history records every poll.
SEGMENTS = [
    ("a", minutes(5), minutes(8) - POLL, 20.00, 20.01),
    ("a", minutes(21), minutes(24) - POLL, 24.99, 25.00),
    ("a", minutes(37), minutes(53) - POLL, 27.00, 27.01),
    ("a", minutes(66), minutes(70) - POLL, 28.24, 28.25),
    ("a", minutes(83), minutes(87) - POLL, 30.00, 30.01),
    ("a", minutes(100), minutes(100, 30), 33.00, 33.01),
    ("a", minutes(114), minutes(114, 10), 37.00, 37.01),
    ("a", minutes(128), minutes(128) + 15 * POLL, 30.00, 30.01),
    ("a", minutes(139, 20), minutes(139, 20) + 15 * POLL, 30.00, 30.01),
]
EXPORT = ("c", minutes(165), minutes(167) - POLL, 30.00, 30.01)
FROZEN_L2 = (minutes(155), minutes(158))
FROZEN_HEATER = (minutes(170), minutes(182))

rows = []


def ts(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.") + f"{moment.microsecond // 1000:03d}Z"


def add(entity, moment, state, last_reported=None):
    rows.append((moment, entity, state, ts(last_reported) if last_reported else ""))


def current_at(phase, poll, index):
    """(amperes, exporting) of a phase at a poll; alternates by 0.01 A every poll."""
    odd = index % 2 == 1
    for seg_phase, first, last, low, high in SEGMENTS:
        if seg_phase == phase and first <= poll <= last:
            return (high if odd else low), False
    seg_phase, first, last, low, high = EXPORT
    if seg_phase == phase and first <= poll <= last:
        return (high if odd else low), True
    return (QUIET_A + (0.01 if odd else 0.0)), False


# Devices and the asleep inverter: one row each at the start; history records changes only.
for heater in ("switch.grzalka_1", "switch.grzalka_2", "switch.grzalka_3"):
    add(heater, START + timedelta(milliseconds=1), "on")
add("number.goe_422752_amp", START + timedelta(milliseconds=2), "16")
add("select.goe_422752_frc", START + timedelta(milliseconds=2), "0")
add("sensor.solaredge_ac_power", START + timedelta(milliseconds=3), "unavailable")
for phase in PHASES:
    add(f"sensor.solaredge_ac_current_{phase}", START + timedelta(milliseconds=4), "unavailable")

# The heater on L3 reports once more, then stops reporting (frozen Modbus relay), then recovers.
add("switch.grzalka_3", FROZEN_HEATER[0], "on", last_reported=FROZEN_HEATER[0])
add("switch.grzalka_3", FROZEN_HEATER[1], "on")

index = 0
poll = START
while poll < END:
    for offset, phase in enumerate(PHASES):
        amps, exporting = current_at(phase, poll, index)
        current_time = poll + timedelta(milliseconds=10 + offset)
        power_time = poll + timedelta(milliseconds=15 + offset)
        entity = f"sensor.solaredge_m1_ac_current_{phase}"
        if phase == "b" and poll == FROZEN_L2[0]:
            # Last report before the sensor freezes: it stays silent until FROZEN_L2[1].
            add(entity, current_time, f"{amps:.2f}", last_reported=current_time)
        elif not (phase == "b" and FROZEN_L2[0] < poll < FROZEN_L2[1]):
            add(entity, current_time, f"{amps:.2f}")
        watts = amps * VOLTS
        add(f"sensor.solaredge_m1_ac_power_{phase}", power_time, f"{watts if exporting else -watts:.1f}")
        if poll == START:
            add(f"sensor.solaredge_m1_ac_voltage_{phase}n", poll + timedelta(milliseconds=20 + offset), f"{VOLTS:.1f}")
    index += 1
    poll += POLL

rows.sort(key=lambda r: r[0])
target = os.path.join(os.path.dirname(os.path.abspath(__file__)), "synthetic-overload-tier-walk.csv")
with open(target, "w", newline="", encoding="utf-8") as handle:
    writer = csv.writer(handle, lineterminator="\n")
    writer.writerow(["entity_id", "ts_utc", "state", "last_reported"])
    for moment, entity, state, last_reported in rows:
        writer.writerow([entity, ts(moment), state, last_reported])
print(f"{len(rows)} rows -> {target}")
