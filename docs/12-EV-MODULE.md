# EV charging module (optional)

Status: specification from the maintainer interview of 2026-10-25. Nothing built. Depends on the v2.0 tariff and settlement modules and the v3.0 forecast and deadline-goal mechanisms; the EV is their first user. Protection is unchanged: the module only proposes a target for the charger, and Guard and Dispatch still cap it by the L1 limit (D2, D3). Off by default; shadow mode applies (D7).

## Goal

Charge the car from PV surplus first, then from the grid zones in order of price (cheap, middle, expensive), the dearest only for what the next trip still needs, and have it charged enough for every trip in the calendar.

## Inputs

All read from Home Assistant entities, mapped in the wizard; the core knows no entity ID (D5).

| Input | Source | Notes |
|---|---|---|
| SoC | HA entity of the car | battery 18 kWh (PHEV) |
| Electric range | HA entity of the car | with SoC gives kWh/km, smoothed; configuration fallback when there is no history |
| Trips | Google Calendar `Kursy`, read through the HA calendar entity | see Trips |
| Plugged / charging | go-e car-state entity | no cable = no plan, no control |
| PV forecast, 2 days | Forecast.Solar via HA | hourly values; missing or stale = plan with zero PV |
| Tariff | zone preset `G13` (hours) + prices per kWh in configuration | see Tariff |
| Surplus | net-billing hourly balance | see Surplus |

The add-on makes no outbound connection (D10): calendar and forecast come through HA only.

## Trips

One calendar event is one trip. `km: <n>` in the description is the whole trip, door to door. Event start = departure, event end = return. Charging is possible only while the car is home and plugged in, so the gap between two events of one day is a charging window. An event without `km:` is ignored and shown in the UI as unparsed. Parsing is code, not a model call.

Energy need of a trip = km × kWh/km (learned from range and SoC).

## Target SoC at departure

```
target = max(floor, trip_soc + floor_after + reserve)
floor = 25 %, floor_after = 25 %, reserve = 15 percentage points
```

After the trip at least 25 % remains, plus a 15-point reserve: target = trip SoC + 40 points. A trip that uses 50 % needs 90 %, one that uses 60 % needs 100 %. The trip target overrides the 90 % surplus cap. All three values are configuration. The car is ready 30 minutes before the event start (`ready_margin`). Charging losses (default 10 %) are added to the energy that must be drawn.

## Without a trip

- Hold the floor (25 %) from the cheap zone, for an unplanned departure.
- Above the floor charge only from PV surplus, up to 90 %.
- Once a week (configurable weekday, default Sunday) the cap is 100 %, filled from PV, then off-peak.

## Merit order (cheapest source first)

1. PV surplus (the hourly balance, see Surplus).
2. The cheapest grid zone (G13 off-peak).
3. The middle-priced zone (G13 morning peak).
4. The most expensive zone (G13 afternoon peak): only for the part of a trip target that cheaper sources cannot deliver before departure. A trip target is a hard deadline: it is met in any zone, with no comparison against fuel cost.

The order of the grid zones is by the price per kWh in configuration, not by their names, so another tariff with two or four zones works the same way. Every zone is filled as late as it can be and still meet the deadline only when a cheaper one cannot deliver; the planner works backwards from the departure.

The planner looks at the 2-day PV forecast and does not take cheap-zone energy that forecast surplus before departure will cover. Weekends and holidays are off-peak all day.

## Surplus

The hourly net-billing balance decides: charge while the current hour is expected to end with export >= 0, so a 6 A start (about 1.4 kW) on a smaller instantaneous surplus and riding out a cloud are allowed. The go-e range is 6-20 A on L1 (single phase, 4.6 kW at most). Starts and stops respect lockouts and hysteresis from configuration (no literals).

## Tariff (G13 preset, prices in configuration)

| Zone | Hours |
|---|---|
| Morning peak | 07:00-13:00, all year |
| Afternoon peak | 19:00-22:00 Apr-Sep, 16:00-21:00 Oct-Mar |
| Off-peak | everything else; whole weekends and holidays |

Hours are as remembered by the maintainer; to be checked against the contract. Zone logic is a pluggable tariff module (D6), `DateTimeOffset` and an injected clock, tested over 23- and 25-hour days. Export is valued at last month's average RCE price (survey decision), used only to rank surplus against grid energy.

## Control

The module outputs a target current or stop for the EV device; it never commands an entity itself. The declared go-e actuator (`number.goe_422752_amp`) and stop actuator (`select.goe_422752_frc`) apply, behind the global control switch and `mode: active`. Every decision carries a reason code, so the closed list gets EV codes (proposed: `ev_trip_deadline`, `ev_surplus`, `ev_cheap_zone`, `ev_floor_hold`, `ev_weekly_full`, `ev_not_plugged`, `ev_forecast_missing`, `ev_manual`; list version 4).

Manual control (HA entity and panel):

- Mode: Auto / PV only / Off.
- Action "charge now to X %": overrides the plan, never the L1 limit.

## UI

Day plan screen (in the mockup, not yet built): next trips, target SoC and time, planned hours per source, forecast, why the car is or is not charging.

## Failure behaviour

| Condition | Reaction |
|---|---|
| Forecast missing or stale | plan with zero PV, reason `ev_forecast_missing` |
| SoC or range stale | last smoothed kWh/km; no new plan from a stale SoC; trip target shown as a warning |
| Calendar unavailable | keep the last parsed trips until their start; no new trips; floor hold stays |
| Not plugged | no plan, no control |
| Guard or Dispatch limit | always wins; the plan only proposes |

## Open items

- Entity IDs for SoC, range, car state and calendar: chosen in the wizard.
- No device_tracker for now; the go-e state decides presence.
- Ready margin (30 min), charging losses (10 %) and the kWh/km fallback are starting values with no origin, for the maintainer to review after a week in shadow.

## Always under the guard

Whatever the plan says, the guard's phase limits and the dispatch envelope cap the charger (D2, D3); the plan proposes, protection disposes. The plan is automatic in the `Auto` mode: it uses the calendar, the tariff, the 2-day PV forecast and the hourly balance without any input from the user (maintainer 2026-10-26).
