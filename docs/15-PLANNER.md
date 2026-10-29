# Planner (v3.0)

Status: design of 2026-10-26; slice 1 (goal model, configuration, read-only API) built 2026-10-27, unreleased, shadow only, no effect on any decision ("As built (slice 1)" below); slices 2 and 3 built 2026-10-27 ("As built (slice 2)" and "(slice 3)" below); slice 4 (pure planner, service, `api/plan` with entries) built 2026-10-27 ("As built (slice 4)"); slice 5 (plan to dispatch behind the flag, reason codes v7) and slice 7 (UI, manual goals) built 2026-10-27 ("As built (slice 5)" and "(slice 7)"); slices 6 and 8 not started. Requirement IDs `PLAN-01..20` (`02-REQUIREMENTS.md`), slice plan at the end. Builds on the v2.0 economics (`14-ECONOMICS.md`: tariff, settlement, `PriceBook`, effective value, `SourceSet`, the allowance gate) and the hourly balance (`13-HOURLY-BALANCE.md`). The EV module (`12-EV-MODULE.md`) comes after v3.0 and is a consumer of it: it supplies goals (trips become energy-by-deadline goals) through the seam in "Goal sources" and is not specified again here. Protection is unchanged: the planner decides when energy is wanted, never whether it is allowed; the guard never sees a plan, a forecast or a price (D2, D3, D4). Shadow mode applies (D7); the planner has its own off / narrow / full switch on top (`planner.dispatch`, default `off`). This is not a protective device.

## Goals and non-goals

Goals:

- A device may say it **wants** energy: how much, by when, how hard the deadline is (`goal:`). The planner turns the goals of all devices into a schedule over the next 24 to 48 hours, from the cheapest energy first: PV surplus (forecast), then grid zones by price, with forecast prices where a price curve exists.
- Dispatch keeps its 10-30 s loop and its gates. The plan reaches it as per-device, per-slot permissions and ceilings (a **target**, never a command), and where the plan is missing, stale or wrong dispatch behaves exactly as v2.
- Battery arbitrage as device class `storage`: charge in cheap zones or on surplus, discharge in the dear zone, inside SoC limits, efficiency and what the guard allows.
- The v2 gap closes: 14-ECONOMICS left a zone-class device running whenever its zone is open; a goal now decides how much and when.
- The EV module needs nothing from v3.0 beyond what is here: `goal` with a deadline, a forecast, a price per slot, a goal seam.

Non-goals:

- No LP or MILP solver and no new third-party dependency (decision below). No probabilistic or scenario planning: one forecast, one derate factor, replanned every 15 minutes.
- No planning for loads without a goal: they stay v2 opportunists (surplus and zone class by their `sources`). The plan never invents a want.
- No fetching of any forecast, price or calendar by the add-on (D10): everything arrives as Home Assistant entities through the signal layer.
- No weather or temperature model for the house load; no occupancy model.
- No net metering (still deferred, `14-ECONOMICS.md`), no multi-site, no user scripting (D9).
- No commands: the planner has no reference to the effector, the command port, overrides or the guard's runtime types (architecture tests).
- The EV module itself (trip parsing, target SoC, kWh/km, manual modes) is out of scope.

## The three-loop contract

| Loop | Period | Reads | Writes | Authority |
|---|---|---|---|---|
| Guard | every sample | breaker currents, device states | envelope, holds | sheds anything; sees no plan, forecast or price |
| Dispatch | 10-30 s | snapshot, envelope, prices, **plan view** | wishes, then the effector gate | starts, modulates, stops within the envelope |
| Planner (new) | 15 min and on events | forecasts, price curve, load profile, goals, device states, the guard's static limits | an immutable `PlanSnapshot` | proposes; nothing in it is a command |

The planner runs on its own timer in its own hosted service. A slow, failing or absent planner cannot delay the guard or dispatch: dispatch reads the last published `PlanSnapshot` from a holder (as it reads `PriceInputs`) and judges it on every step.

### What the plan is

`PlanSnapshot` (Domain, immutable, deterministic from its inputs): `Id`, `ComputedAt`, `ConfigRevision`, `Slots` (start, end, zone, import and export price with basis, PV kWh, base-load kWh, surplus kWh), and per device `Entries` per slot:

| Field | Meaning |
|---|---|
| `Want` | `Run` (energy wanted in this slot), `Defer` (wanted later; not now), `Done` (the goal is met or its budget for the slot is delivered), `Urgent` (hard deadline at risk), `None` (no goal) |
| `Class` | `pv` or `zone:<id>`: which energy the plan counted on |
| `EnergyKwh` | the slot's budget for this device |
| `MaxPowerW` | ceiling for the slot (at most the device's maximum, at most what the static per-phase budget leaves) |
| `GoalRef` | goal id and revision the entry was made for, and the device's spec fingerprint (`SpecIdentity`) |

Storage entries carry a signed `EnergyKwh` (charge positive) and the planned SoC at the slot's end.

### How dispatch consumes it

At each step dispatch asks `PlanView.For(device, now)`. A valid answer is the entry of the slot that contains `now`; anything else is `null`, and a `null` leaves the v2 code path untouched. Where an entry exists and `planner.dispatch` allows it:

```
guard permission and envelope                  unchanged, first
allocation by order key                        unchanged; MaxAllowed capped by the entry's MaxPowerW (narrows)
allowance gate (sources, price test, balance)  unchanged, plus:
    Defer            refuses a zone-class start or increase              reason plan_deferred   (narrows)
    Done             refuses every class when goal.on_met is stop        reason goal_met        (narrows)
                     refuses an increase beyond the slot's budget        reason plan_slot_target (narrows)
    Run, class zone  opens the class `plan` for a goal device that declares   (widens one class,
                     the zone in goal.sources, price test skipped (the        only mode full)
                     planner did it), no zone_end_margin                  reason plan_slot_target
    Urgent           opens every zone, and puts the device first in the     reason goal_deadline_urgent
                     start walk (mode full)
persistence, lockouts, settling, step limit, max_simultaneous_increases    unchanged
effector gate: mode, control switch, allow-list                            unchanged
```

`planner.dispatch`:

| Value | Effect |
|---|---|
| `off` (default) | the plan is computed and shown (`api/plan`); dispatch ignores it; decisions are byte for byte v2 (no `plan` block either) |
| `narrow` | the plan may only narrow: `Defer`, `Done`, ceilings. No class is opened, nothing starts that v2 would not start |
| `full` | `narrow` plus the class `plan` and `Urgent` |

The plan never raises `MaxAllowed` above the guard's cap and never bypasses a gate; the only thing it can add is a class in the allowance gate, behind every other check, and only for a device that declares `start: headroom` and a `goal` (a `restore_only` device is never started by dispatch, planner or not).

### When the plan is missing, stale or wrong: exactly v2

| Condition | Detection | Dispatch does |
|---|---|---|
| No `planner:` section, or `planner.dispatch: off` | configuration | v2, byte for byte (digest test, PLAN-14) |
| No snapshot yet, planner failed, planner stopped | holder empty | v2 |
| Snapshot older than `planner.plan_max_age` (3 cycles) | `now - ComputedAt` | v2 |
| Snapshot from the future (clock stepped back) | `ComputedAt > now` | v2; planner replans at once |
| `now` outside the horizon | slot lookup | v2 |
| Entry made for another configuration, spec or goal revision | `ConfigRevision`, `SpecIdentity`, `GoalRef` | that device v2, others keep their entries |
| Guard safe state, kill switch, invalid configuration, HA disconnected | existing | everything off as before; the plan is `suspended` and ignored |
| A device with no entry | lookup | that device v2 |

A missing plan never means "defer everything": absence is the v2 behaviour, a `Defer` is an explicit entry of a fresh, valid plan. One log line (`plan_fallback entered` / `left`, warning / information, naming the reason, not an entity) marks each change; it is not a decision and has no reason code (as `tariff_fallback`).

## Inputs and their signal roles

Every input is a Home Assistant entity mapped as a signal (D5, D10). New kinds and roles, in the signal layer only; none enters `ProtectionInputs` or `DispatchInputs`, they travel as `ForecastInputs` beside `PriceInputs`, and an architecture test keeps the guard assembly free of them.

| Input | Role | Kind | Source shape | Freshness | Missing, stale or unusable |
|---|---|---|---|---|---|
| PV forecast, series | `pv_forecast` | `series` | an attribute of one entity: a list of `{start, value}` or a dict `time -> value`; value is power W at the time or energy Wh of the period (`series.value: power_w \| energy_wh_period`); bounded (at most 512 points, 64 KiB) | `last_reported` and `max_age` of the entity, plus coverage: the last point | zero PV for the uncovered part; note `pv_forecast_missing` or `pv_forecast_partial` |
| PV forecast, daily totals (degraded shape) | `pv_forecast_today`, `pv_forecast_tomorrow` (+ `sun_rising`, `sun_setting`) | `energy_forecast` (kWh, Wh, MWh), `timestamp` | scalars; the day total is spread over daylight as a half-sine between the sun times (`planner.pv_shape`) | as any signal | as above |
| Price curve import / export | `price_curve_import`, `price_curve_export` | `series` | attribute list `{start, end?, value}` (Nord Pool `raw_today` plus `raw_tomorrow`, Tibber, ENTSO-E style) or dict; currency label checked as for prices; `/MWh` divided in the normalizer | as above, plus coverage | the tariff schedule or the spec's fallback for the uncovered part; note `price_curve_partial` |
| Load forecast | none (internal) | derived | `load-profile.json`, see below | n/a | `planner.load.fallback_w` |
| Battery SoC | `device_soc` | `percentage` | created from the device (`soc:` block), like a draw | `max_age` | storage not planned, device v2 |
| EV or other SoC | `device_soc` | `percentage` | same | same | the goal is inactive, warning, device v2 |
| Goal target, deadline, want | `goal_target`, `goal_deadline`, `goal_want` | `percentage` or `energy_forecast`, `timestamp`, `state` | device-created, optional; a static value in the file is the alternative | `max_age` | the goal is inactive (never guessed), shown |
| Calendar | none here | | the EV module reads `calendar.*` attributes through the same bounded attribute mechanism | | EV module |
| Tariff, settlement, effective value | v2 | | `14-ECONOMICS.md` | | v2 |
| Guard static limits | none (internal) | | `ProtectionSetup`: allocation limit per phase and circuit group (In, reserve, tier `may_add`) | | planner idle without a valid configuration |

**What the signal layer needs that it does not have.** `SubscribeEntitiesParser` keeps only the unit and a select's `options` from the attributes, and signals are scalar. v3.0 adds a per-signal `attribute:` whitelist: only the named attributes of the declared entities are kept, bounded in count and bytes, parsed into a `Series` (time-sorted, finite, times in UTC offsets accepted, a malformed item refuses the whole series); the frame is dropped when oversized, never truncated. The series freshness is the entity's `last_reported` (HA rewrites a forecast sensor when it refreshes); `max_age` is the user's (a forecast refreshes hourly or less). Coverage is separate: a fresh series that ends in six hours is a partial forecast, not a stale one.

**Forecast entity shape (open question 2).** The Home Assistant core Forecast.Solar integration, as far as the design knows, exposes daily and next-hour totals but no hourly series (its hourly values go to the Energy dashboard, not to entities). It is not verified on the maintainer's HA and must be checked read-only in slice 2. Three ways to a series exist without the add-on fetching anything: Solcast (`detailedForecast` attribute), a REST sensor in HA over the Forecast.Solar API with `json_attributes_path` (its `watts` dict is the dict shape above; HA fetches, not the add-on), and the daily-totals shape, which works with the core integration as it is. The recommendation builds both shapes in slice 2; the series is better, the totals shape always works.

**PV forecast processing.** Points are integrated to slot energy (power points by the trapezoid, period energies assigned), multiplied by `planner.pv_derate` (0.85, no origin: the mockup says the price difference "covers the risk of worse weather"; to review after the shadow week), and zero beyond the coverage. A forecast older than its `max_age` is not used at all (zero PV), never the last known curve.

**Load forecast (the uncontrolled house load) from statistics.** What exists: `statistics.json` holds 5-minute buckets (live setting `BucketLength` 5 min) with `Uncontrolled` kWh per bucket (generation + import - export - controlled, the power balance identity of DECISIONS 2026-10-25), `Observed` and `Unobserved` time. Retention on the live install is `1.02:00:00` (26 hours), at most 8 days: too little for a weekly pattern. v3.0 therefore keeps its own `load-profile.json` (Infrastructure, schema 1, atomic write, size capped, untrusted on read like `statistics.json`): 168 hour-of-week bins (local time; the repeated 02:00 hour of a 25-hour day updates the same bin twice, a lost hour updates none; holidays from `economics.holidays` use the Sunday bin), each an exponentially weighted mean of the hourly `Uncontrolled` energy (half-life `planner.load.halflife_days`, an hour counts only when observed for at least `planner.load.min_observed_fraction` of it) and a sample count. It is fed at every completed local hour from the statistics accumulator and seeded at first start from the retained buckets (up to 26 hours: about a day of bins, the rest empty). No Recorder access, no history API call, no HA helper. A bin with no sample uses the mean of the filled bins; with no filled bin at all (fresh install), `planner.load.fallback_w`. The forecast reports its basis (`profile`, `partial`, `fallback`) and how many days of bins are filled; the plan shows it. Known limit: no temperature dependence (heating season errors) and no variance; the derate acts on PV only.

## The slot grid

A slot is `planner.slot` long (`60m`; `15m` and `30m` supported by the same code) and starts on a local slot boundary. Slots are cut at every local boundary and at every UTC-offset transition, so a 23-hour day has 23 hourly slots, a 25-hour day has 25, and a zone with a half-hour offset change still works. The first slot starts at the boundary at or before `now` and is shortened to `now`; a goal deadline cuts its last slot (a 06:30 deadline leaves half of the 06:00 slot). Price per slot is the time-weighted mean of the prices in force (the settlement rule of 14-ECONOMICS: zones and hourly markets change on the :00). Hourly slots match every input at once: the hourly netting period, the G13 zone edges, an hourly PV forecast and the hourly load bins; the 15-minute cadence is the replanning period, not the slot length (open question 4).

## The goal model

`goal:` moves from the reserved keys (a warning today) to a live device key. It is optional; a device without one is a v2 opportunist.

```yaml
goal:
  kind: soc                  # energy | soc | none
  hard: true                 # hard deadline: must be met, no value test, may escalate. soft: only while worth its price; lapses at the deadline
  # energy: deliver target_kwh by the deadline. soc: reach target percent of capacity_kwh by the deadline
  capacity_kwh: 18           # soc only
  loss_fraction: 0.10        # energy drawn = needed x (1 + loss_fraction)
  soc:      { entity: sensor.car_battery_level, max_age: 30m }          # role device_soc
  target:   { value: 90 }                                                # or { entity: input_number.car_target_soc, max_age: 1h }, role goal_target
  deadline: { entity: input_datetime.car_departure, margin: 30m }        # or { at: "06:30" } recurring local time; role goal_deadline
  want:     { entity: input_boolean.car_charge_wanted }                  # optional gate; role goal_want. false = no goal
  sources: pv_and_zone:offpeak     # the classes the planner may schedule this goal on (same grammar as the device's sources); default: the device's sources, else any
  escalate: true                   # hard only: when the deadline is at risk any zone may be used (default true for a hard goal)
  on_met: opportunistic            # opportunistic: after the goal the device is a v2 opportunist again. stop: it stays off (the target is also a cap)
```

- **Value forms.** Each of `soc`, `target`, `deadline`, `want` is a literal or `{ entity, max_age }`; an entity form creates a device-scoped signal (like `draw`), so the entity ID never leaves the signal layer. A goal whose required input is missing, stale, unavailable or out of range is inactive and shown with the reason; it is never guessed and never keeps an old value.
- **No deadline** means the horizon end: "wants energy, cheapest first, whenever". **No `target`** with `kind: energy` and a `want` entity means the static `target_kwh`.
- **`kind: energy`** needs `target_kwh` (literal or entity). Progress is the controlled energy the device drew since the goal began, from a goal ledger that integrates the device's measured (else rated-model) power each dispatch step, persisted in `state.json` (schema bump, an old file reads as "no goals"). **`kind: soc`** progress is the SoC signal itself; the ledger is not used.
- **Remaining need** `R = max(0, target - progress) x (1 + loss_fraction)` in kWh (soc: `(target - soc) / 100 x capacity_kwh`).
- **Hard** goals are not value-tested (the EV doc: a trip target is met in any zone with no comparison against fuel cost). **Soft** goals use a class only while `v_eff - cost >= economics.min_margin`, the v2 price test, evaluated by the planner.
- **Several goals per device** (two trips in one day) are ordered by deadline; goal j's cumulative need is due at its deadline. A goal past its deadline is `expired` (soft) or `missed` (hard, shown, still pursued as `Urgent` until met or removed).
- **A goal never widens the device's physical limits**: power is the device's range, per-phase and group budgets are the guard's static limits.

### Goal sources (the EV seam)

```
IGoalSource   (Application)   Goals(now, snapshot) -> GoalRequest[]
GoalRequest   Device, Kind, RemainingKwh | TargetSoc, Deadline, Hard, Sources, Since, Source, Revision
```

Sources: the device's `goal:` block (config and mapped entities), the manual store (below), and later the EV module, which turns calendar trips into requests for the car's device. The planner merges requests per device. Precedence for one device and one deadline: manual over module over entity over config; the losing request is shown, not applied.

**How `want` is entered by the user (open question 6).** Three ways, same model: mapped HA entities (an `input_boolean`, `input_number`, `input_datetime` on the dashboard, an automation, a calendar), a static recurring goal in the file (`deadline: { at: "06:00" }`), and a "want now" action on the device card (slice 7) that stores a manual goal in the add-on's own state (`state.json`, like overrides: refused for an undeclared device, expires at its deadline, never an entity write). Nothing here commands an entity.

## The planning algorithm

Pure Domain function `Planner.Plan(PlanInput) -> PlanSnapshot`, no clock, no I/O, no entity ID; time arrives in the input. Deterministic: the same input gives the same bytes (all ties broken by device name, then slot start, then class order).

### Slot model

For slot `t` of length `h_t` hours: `pv_t` (derated forecast kWh), `b_t` (load forecast kWh), `p_t` import price, `e_t` export value (`ExportValue`, the forgone income). Surplus `S_t = max(0, pv_t - b_t - storage charge)`. A kWh a goal device draws in slot `t` costs `e_t` while it fits in `S_t` (class `pv`, forgone export) and `p_t` beyond (class `zone:<z_t>`). The hourly-netting slot is the netting period, so this is exact for it; under net billing the same tiers apply as an approximation and the instantaneous gate of v2 decides at run time. Cost of a plan is the sum of those tier costs (`Settle` is used for the savings figure, not here).

Capacity: per slot, per constraint key (main phase, circuit group; the same `ConstraintKey` dispatch uses), the static allocation limit of the guard's setup in watts times `h_t`, shared by every device on it; a `three_phase` device draws a third on each. A device's slot power is at most its range maximum and, when on, at least its range minimum for at least `planner.min_run` (default: the device's `min_on`); below that a slot's energy is a duty inside the slot (energy over minimum power = hours on). Relay devices (`onoff`, `stepped`) use their stage power. The plan's capacity is a static optimum: foreign load is the reserve's business, and the live envelope can only be lower, which dispatch handles and the next replan sees as unmet progress.

### Goals, in an order

1. Candidate cells per goal: every (slot, class) before the deadline minus `margin`, class within `goal.sources` (soft: and worth its price), slot at or after `now`.
2. Goals ordered by urgency: hard before soft, earlier deadline first, then the device's order key (`v_eff`), then name. This is earliest-deadline-first.
3. Each goal takes cells in ascending cost; a tie in cost takes the earliest slot (robust: an under-delivery can still be repaired before the deadline, and the device sits at the target early). It takes as much of a cell as capacity, `S_t` (for `pv`) and the device's power allow, until `R` is met.
4. **Refinement:** for each goal in reverse urgency order, release its cells and refill given the others; repeat while total cost falls, at most `planner.refine_passes` (3). This repairs the one thing greedy EDF gets wrong, a less urgent goal that took a cell a more urgent goal needed, and it terminates by construction.
5. **Hard goal check and escalation:** if a hard goal's `R` cannot be met inside `goal.sources`, escalate to every zone when `escalate` is on (entries `Urgent`, class of the zone used), else report `shortfall`. If it cannot be met at all (power times time before the deadline is less than `R`) the goal is `infeasible`: the plan schedules the most it can, from now (`Urgent`), and says so.
6. **Latest-safe-start rule:** each replan computes `latest_grid_start = deadline - R / P_max` (grid-only delivery at full power). When `now >= latest_grid_start - planner.hard_goal.escalation_margin` the goal is `Urgent` whatever the plan counted on (the forecast may have failed). This is what makes a hard deadline robust against a bad PV day without planning grid energy "just in case".
7. Entries per device and slot are written (`Run` with class, energy and ceiling; `Defer` for slots with a goal remaining but energy placed later; `Done` after the goal is met or `on_met: stop`).

Opportunistic devices (no goal) get `None` and keep v2. The plan reserves nothing for them; the value order (a car above a heater) already hands PV to goal devices first at run time, and what remains is theirs.

Why not an LP: the structure is a small transportation problem (goals, cells, shared capacities) that this greedy plus refinement solves exactly when no cell is contended and within a documented gap when it is (a test compares it with exhaustive search on small instances, below); storage adds one dynamic (SoC) handled exactly by a DP. A general LP buys optimality on cases the installation does not have at the price of a native library (see the decision).

### Worked example (the maintainer's installation)

3 x 25 A, 230 V, reserve 2.5 kW per phase (3.25 kW allocation per phase), three heaters 1.7 kW (`pv_only`, `start: headroom`, no goal) on L1, L2, L3, go-e on L1 at 13 A (2.99 kW, minimum 6 A = 1.38 kW), G13, hourly netting, no battery. Prices are the placeholders of 14-ECONOMICS: off-peak 0.60, morning peak 1.10 (07:00-13:00 workdays), afternoon peak 1.60 (16:00-21:00 October to March, workdays), export 0.35. `pv_derate` 0.85, a house base load of 0.45 kWh per hour by day. Now is Monday 2026-10-26 18:00 local (UTC+1, the day after the clocks changed); the car is plugged in at 40 %, 18 kWh battery, and has a trip Wednesday 07:00:

```yaml
goal: { kind: soc, hard: true, capacity_kwh: 18, loss_fraction: 0.10, target: { value: 90 },
        deadline: { entity: input_datetime.car_departure, margin: 30m }, sources: pv_and_zone:offpeak }
```

`R = (90 - 40) % x 18 kWh x 1.10 = 9.9 kWh`, deadline Wednesday 06:30 (36.5 hours ahead). Tuesday's forecast per hour (kWh): 08 0.3, 09 1.2, 10 2.4, 11 3.3, 12 3.6, 13 3.2, 14 2.3, 15 1.2, 16 0.3; derated and after the base load the surplus `S_t` is 09 0.57, 10 1.59, 11 2.36, 12 2.61, 13 2.27, 14 1.51, 15 0.57 (11.47 kWh).

Candidate cells for the car, by cost: PV surplus at 0.35 (the seven cells above), off-peak grid at 0.60 (Mon 21:00-Tue 07:00, Tue 13:00-16:00, Tue 21:00-Wed 06:30; capacity 2.99 kWh per hour), no other zone is in `goal.sources`. The greedy takes the PV cells, earliest first, until `R` is met:

| Slot (Tue) | Zone | Class | Energy kWh | Power | Note |
|---|---|---|---|---|---|
| 09:00 | morning peak | pv | 0.57 | 1.38 kW for 25 min | a duty inside the hour |
| 10:00 | morning peak | pv | 1.59 | 1.59 kW | |
| 11:00 | morning peak | pv | 2.36 | 2.36 kW | |
| 12:00 | morning peak | pv | 2.61 | 2.61 kW | |
| 13:00 | off-peak | pv | 2.27 | 2.27 kW | surplus is cheaper than off-peak grid (0.35 against 0.60) |
| 14:00 | off-peak | pv | 0.50 | 1.38 kW for 22 min | `R` met: 9.90 kWh in all |

Every other slot, tonight included, is `Defer` for the car (`plan_deferred` if v2 would have started it at 21:00 in the off-peak zone, which it would: `zone_allowed`). Planned cost 9.9 x 0.35 = 3.47. Two references: v2 on its own starts at 21:00 off-peak, 9.9 x 0.60 = 5.94; as soon as possible (from 18:00, 2.99 kW) costs 8.97 x 1.60 + 0.93 x 0.60 = 14.91. The plan saves 2.47 against v2 and 11.44 against "as soon as possible", on the placeholder prices. Latest-safe-start: grid-only delivery needs 9.9 / 2.99 = 3.31 h, so `latest_grid_start` is Wednesday 03:11 and the goal turns `Urgent` at 02:41 if it is still short (cheap off-peak hours exist on Tuesday night before that, so a bad PV day is repaired by the ordinary replan, not by the escalation).

The heaters get `None`: Tuesday's leftover surplus (14:00 1.01, 15:00 0.57 kWh) is below a heater's 1.7 kW for the hour, so v2's surplus gate burns whatever really appears; in the car's slots the car is first in value order and L1's 3.25 kW budget leaves no room for the L1 heater beside the car anyway (1.7 + 2.99 kW), which dispatch decides, not the plan.

Variants. **Forecast missing:** zero PV, the same goal fills the off-peak grid cells, earliest first: Monday 21:00, 22:00, 23:00 at 2.99 kWh and 0.93 kWh at 00:00 (cost 5.94), note `pv_forecast_missing`, as `12-EV-MODULE.md` requires. **Cloud at Tuesday 11:00:** the car has drawn only half of the planned surplus by 11:15, the replan sees `R` larger and moves the shortfall into the Tuesday 13:00 and 14:00 off-peak grid cells and Tuesday night, adopted for the current slot only after `adopt_cycles` agreeing replans unless the change is a trigger (below). **Hard goal infeasible** (trip in two hours with 9.9 kWh needed): `Urgent` now, class by price within reach, `shortfall` 3.9 kWh shown, the dearest zone used (`escalate`), `goal_deadline_urgent` on the start.

## Battery arbitrage (device class `storage`)

The `storage` type exists since v1 (a current setpoint, `min <= 0 < max`, signed draw, never discharged by dispatch: DECISIONS 2026-10-24). v3.0 gives it a plan.

```yaml
- id: battery
  type: storage
  phase: L2
  actuator: number.battery_setpoint
  min: -16
  max: 16
  step: 1
  unit: A
  storage:
    capacity_kwh: 10
    soc: { entity: sensor.battery_soc, max_age: 5m }     # role device_soc, percent
    soc_floor: 15                 # percent, never discharged below it
    soc_ceiling: 95               # percent, never charged above it
    round_trip_efficiency: 0.90   # split evenly between charge and discharge
    max_charge_w: 3680
    max_discharge_w: 3680
    allow_export: false           # discharge is limited to the house load
```

**Plan.** After the goals, the DP runs over SoC levels (`planner.storage.soc_step_kwh`, 0.1) and slots against the residual net energy `n_t = pv_t - b_t - goal loads` and the slot's tiers: the slot costs `import x p_t - export x e_t` of the battery-adjusted balance (exact for the netting hour). The decision per slot is the charge or discharge energy within the power limits and the SoC window; terminal SoC is valued at `planner.storage.terminal_value` (currency per kWh, no origin); a pair earns only if `discharge price x sqrt(eta) - charge price / sqrt(eta) - degradation >= planner.storage.min_spread`. Result: charge on surplus and in the cheap zone, discharge in the dear zone, nothing when the spread is too small. Exact backward induction, about 48 x 200 x 200 evaluations, milliseconds.

**Known approximation.** Goal devices are planned before the battery and see the grid prices, not the battery's marginal value; the battery then sees the goals' loads as fixed. Surplus the goals take is not available to charge it (the maintainer's order: car and battery before hot water, the car first). A joint LP could do better; the gap is small for one battery and is the price of not carrying a solver.

**Guard and discharge.** A discharge raises generation into the busbar; the guard's phase-current protection is unchanged and counts a discharge as no relief (a negative contribution, conservative: DECISIONS 2026-10-24). The plan's discharge ceiling for a slot is `min(max_discharge_w, forecast house load of the slot)`; at run time dispatch lowers it to the measured import (no export from the battery unless `allow_export`), and a discharge target is only a ceiling: the envelope still applies and any guard reduction ends it first. A discharge is commanded only through the declared actuator with a negative setpoint and only with `planner.dispatch: full`.

**Staging (open question 3).** The maintainer has no battery, so nothing here can be verified live. Slice 6 builds the model, the plan and the charge direction (which the v1 storage path already commands) and records the discharge as a plan target in shadow; the discharge command stays disabled in code behind a configuration key (`storage.discharge_command: false` by default) until a real battery has run in shadow. Slice 6 can be dropped without touching the rest.

## How the plan meets the gates

The plan chooses **when** energy is wanted; the gates decide whether it is allowed **now**. In the order a start meets them:

| Gate | Owner | The plan's part |
|---|---|---|
| Guard envelope, tiers, budgets, safe state, kill switch | guard | none; static limits are read to size the plan, never the live envelope |
| Allocation by order key and headroom | dispatch | caps `MaxAllowed` at the slot's ceiling |
| Allowance: surplus class (hourly balance / net billing) | allowance gate | a `Run` on class `pv` is a want; the balance rule still says Start, Hold or Stop now: a plan slot on surplus during a deficit hour starts nothing, the progress falls behind and the next replan moves the energy |
| Allowance: zone class (sources, price test, zone end margin) | allowance gate | `Defer` narrows; `Run` on `zone` opens class `plan` (full mode) for a declared goal device |
| Hourly balance for zone-class energy | none (as v2: a zone-class device is not stopped by the balance) | the plan's tier costs already net surplus and import inside the hour |
| Persistence, lockouts, settling, step limit, simultaneous increases | dispatch and effector gate | none; the plan's `min_run` is derived from `min_on` so a planned run is not shorter than the relay allows |
| Mode, control switch, allow-list | effector gate | none |

A plan wrong about the PV is repaired by progress: the goal ledger and the SoC are measured, `R` is recomputed at every replan.

## Re-planning

| Trigger | Replan | Adoption of the new plan |
|---|---|---|
| Cadence: every `planner.cycle` (15 m) on the injected clock, aligned to :00 :15 :30 :45 | yes | hysteresis below |
| Goal added, changed or removed (config reload, entity change, manual goal) | at once, at most one per `planner.min_replan_interval` (60 s) | at once for that device |
| Device state: plugged in or out (goal activates or ends), device becomes `unresponsive` or recovers, goal progress deviates from the plan by more than `planner.progress_tolerance_fraction` | at once, rate limited | at once for that device |
| PV forecast or price curve updated (series digest changed) | next cycle boundary, not at once | hysteresis |
| Configuration reload, clock step | at once | at once |
| Guard safe state exited | at once | at once |

**Hysteresis (no flapping).** A plan carries intent for the future slots, which take every new plan as it comes. The **current slot's** entries are the commitment: an entry changes only after `planner.adopt_cycles` (2) consecutive replans propose the same change (the `StableAdoption` rule of the phase ranking and the value order), and only if the energy budget moves by at least `planner.adopt_min_change_fraction` (0.05) or `Want` flips; the triggers above and `Urgent` bypass it. A device that is running under a `Run` entry keeps it to the slot's end or `Done`, so a forecast wobble never produces stop-start inside an hour; lockouts apply regardless.

## Configuration

Two places, as with the economics. **Data** in `switchboard.yaml`: top-level `planner:` (optional; absent = v3.0 off, byte for byte v2), per device `goal:` and, for storage, `storage:`, forecast and curve signals under `signals:`, `curve:` in a dynamic price spec. **Parameters** in `control-defaults.yaml` under `planner:` (`control.planner` overrides, checked like the others), no literal in code:

```yaml
planner:
  cycle: 15m
  min_replan_interval: 60s
  plan_max_age: 45m            # three cycles; after it dispatch is v2
  max_solve_time: 2s
  start_delay: 60s             # no plan before inputs had this long to arrive after a start
  horizon: 48h                 # beyond forecast coverage PV is zero and prices come from the schedule
  slot: 60m                    # 15m, 30m or 60m
  pv_derate: 0.85              # no origin; review after the shadow week
  pv_shape: half_sine          # degraded totals shape
  refine_passes: 3
  adopt_cycles: 2
  adopt_min_change_fraction: 0.05
  progress_tolerance_fraction: 0.10
  hard_goal:
    escalation_margin: 30m
  load:
    halflife_days: 14
    min_observed_fraction: 0.8
    fallback_w: 400            # no origin
  storage:
    soc_step_kwh: 0.1
    min_spread: 0.10           # currency per kWh, no origin
    degradation_per_kwh: 0
    terminal_value: 0
```

```yaml
# switchboard.yaml, the maintainer's installation (additions to the economics example of 14-ECONOMICS)
planner:
  dispatch: off                # off | narrow | full

signals:
  pv_forecast_series:
    entity: sensor.forecast_solar_watts         # an HA REST sensor with a `watts` attribute, or Solcast
    kind: series
    attribute: watts
    series: { shape: dict, value: power_w }
    max_age: 6h
    role: pv_forecast
  # or the degraded totals shape (works with the core Forecast.Solar integration):
  # pv_today:     { entity: sensor.energy_production_today,    kind: energy_forecast, unit: kWh, max_age: 6h, role: pv_forecast_today }
  # pv_tomorrow:  { entity: sensor.energy_production_tomorrow, kind: energy_forecast, unit: kWh, max_age: 6h, role: pv_forecast_tomorrow }
  # sun_rising:   { entity: sensor.sun_next_rising,  kind: timestamp, max_age: 24h, role: sun_rising }
  # sun_setting:  { entity: sensor.sun_next_setting, kind: timestamp, max_age: 24h, role: sun_setting }

devices:
  - id: ev_charger
    value_per_kwh: 2.03
    sources: pv_and_zone:offpeak
    start: headroom
    goal:
      kind: soc
      hard: true
      capacity_kwh: 18
      loss_fraction: 0.10
      soc: { entity: sensor.car_battery_level, max_age: 30m }
      target: { value: 90 }
      deadline: { entity: input_datetime.car_departure, margin: 30m }
      sources: pv_and_zone:offpeak
```

A dynamic price with a forward curve (14-ECONOMICS `signal:` + `fallback` + `add`, plus the optional `curve:`):

```yaml
economics:
  tariff:
    signal: price_now            # kind price, role price_import (v2.0)
    curve: price_curve           # kind series, role price_curve_import: the planner's forward prices
    fallback: { default_zone: offpeak, zones: [ ... ] }
    add: { default_zone: dist, zones: [ ... ] }
```

Checks (config issues): `planner.dispatch` known; a `goal` of kind `soc` needs `capacity_kwh` and `soc`; `energy` needs `target_kwh`; `loss_fraction` in [0, 1); `deadline.margin` and `at` parse; goal `sources` zone ids exist; a goal on a device with `start: restore_only` is a warning (`goal_without_start`, the planner never starts it, the goal can still `Defer`); `curve` without `signal` is a warning (the curve then replaces nothing); a `series` signal needs `attribute`; forecast and curve roles unused by anything are warnings (`forecast_signal_unused`); structural errors (wrong type, unknown key) are errors as everywhere else; a goal whose inputs cannot be evaluated is dropped with a warning, never an invalid document: narrowing is the only direction a configuration problem may move a device.

## Reason codes (list version 7)

Version 6 is in the tree, so these are **version 7**; the list only appends and the log keeps reading versions 7 to 2 (an older line naming a new code is not a decision). `plan_slot_target` and `goal_met` are already in the closed list from version 1 and have never had an emitter: v3.0 gives them one and they need no version bump, only meaning (check their en and pl sentences).

| Code | Meaning | Versions 1 to 6 used |
|---|---|---|
| `plan_slot_target` (existing) | a start by the plan's own class, or an increase refused or a decrease because the slot's power ceiling or energy budget is reached | `headroom_available` / `headroom_insufficient` |
| `goal_met` (existing) | a start or increase refused or a stop because the device's goal is met and `on_met` is `stop` | `headroom_insufficient` |
| `plan_deferred` (new) | a start or increase refused because the plan places the device's energy in a later slot (zone class only) | `headroom_insufficient` (or the v6 permitted code, which v6 would have used) |
| `goal_deadline_urgent` (new) | a start or increase permitted beyond the device's sources because a hard goal would otherwise miss its deadline | `headroom_available` |
| `plan_discharge` (new) | a storage discharge permitted because the plan's slot calls for it | `headroom_available` |
| `soc_floor` (new) | a discharge refused or ended because the state of charge is at its floor | `headroom_insufficient` |
| `soc_ceiling` (new) | a charge refused or ended because the state of charge is at its ceiling | `headroom_insufficient` |

Decision inputs gain an optional `plan` block (key absent on old lines and whenever `planner.dispatch` is `off`; null in `api/decisions` without a planner; a present but malformed block makes the line no decision, like `balance` and `economics`): `status` (`fresh`, `stale`, `absent`, `invalid`, `suspended`), `plan_id`, `computed_at`, `slot_start`, `slot_end`, `want`, `class`, `energy_kwh`, `delivered_kwh`, `max_power_w`, `goal` (`kind`, `remaining_kwh`, `deadline`, `hard`, `source`), `soc` for storage. Replan and adoption events are application log lines (`plan_replanned`, `plan_adopted`, `plan_fallback`), not decisions.

## API

`GET api/plan` (read-only, ingress only, no new port; `404`-free: `enabled: false` without a `planner:` section):

```
enabled, dispatchMode, status (fresh | stale | absent | suspended | error), id, computedAt, validUntil, horizonEnd, slotMinutes,
inputs { pvForecast {state, ageS, coversUntil, basis (series | totals | none), derate},
         loadForecast {basis (profile | partial | fallback), daysFilled},
         prices {basis (tariff | curve | partial), coversUntil}, goalsActive, goalsInactive[ {device, reason} ] },
slots [ {start, end, zone, importPrice, exportPrice, pvKwh, baseKwh, surplusKwh} ],
devices [ {device, goal {kind, hard, source, targetKwh|targetSoc, deadline, remainingKwh, status (on_track | at_risk | infeasible | met | expired | missed | inactive)},
           predictedCompletion, shortfallKwh,
           entries [ {start, want, class, energyKwh, maxPowerW, soc?} ] } ],
cost { planned, reference_v2, reference_asap, currency },
notes [ pv_forecast_missing | pv_forecast_partial | price_curve_partial | goal_input_stale | solver_timeout | ... ],
lastSolve { durationMs, result }
```

Camel case as the other endpoints; the currency label verbatim from the configuration; `api/signals` lists the new kinds; `api/economics` is unchanged. `api/decisions` carries the `plan` block. Manual goals (slice 7): `POST api/goals` and `DELETE api/goals/{device}` through the same ingress policy as the override endpoints; refused with 404 for an undeclared device, 409 without a valid configuration.

## UI

Day plan (the mockup's screen 06, Polish copy is normative for vocabulary, not pixels; "Plan doby", "Przeliczany co 15 minut. Wykonywany jest zawsze tylko najbliższy odcinek - reszta to zamiar, który zmieni się wraz z prognozą."): one card, header "from now to the horizon, PV forecast X kWh, tariff name"; an SVG with the kW axis, the tariff zones as a coloured band (cheap, middle, dear by price rank, not by name), the PV forecast as a line, and one row per device with bars labelled by class ("from grid, off-peak", "from PV surplus", "charge from PV", "discharge in the peak"); the current slot marked; the time axis in local time with the 23- or 25-hour day drawn as it is. Below two columns: "Plan decision" (a sentence per goal built from the structured entry by code, en and pl templates, never a model) and "Cost" (planned, v2 on its own, as soon as possible: the mockup's "without control" wording predates the savings definition and is not used). Inputs line: forecast basis and age, load basis, price basis, derate; a warning list for the `notes`. Entity IDs in monospace. Phone width, both themes, `prefers-reduced-motion`. The page states that the plan is an estimate, not a guarantee and not a protective device.

Device card goal row (UI-12): goal kind, remaining, deadline, hard or soft, source of the goal, status pill (`on_track`, `at_risk`, `infeasible`, `met`, `inactive` with the reason), the next planned slots, and the "want now" action (manual goal). The command history row is unchanged. With no planner: the row says "no goal". Strings in en and pl; a test holds the status, want, class and note lists to the backend (as `EconomicsSpaContractTests`).

## Failure behaviour

Principle: a planner input that fails **narrows or falls back, never widens, never guesses**; protection is untouched; the plan only ever proposes. Each row gets a named test (PLAN-17).

| Condition | Detection | Reaction | Visible as |
|---|---|---|---|
| PV forecast missing, stale, unavailable, wrong unit, unparseable | signal state, parser | zero PV for the whole horizon, the plan is made (goals go to grid classes inside `goal.sources`, as `12-EV-MODULE.md` says) | note `pv_forecast_missing`, `pvForecast.basis none` |
| PV forecast covers only part of the horizon | coverage | zero PV beyond it | note `pv_forecast_partial` |
| Price curve missing, stale or partial | signal state, coverage | the tariff schedule or the spec's fallback for the uncovered part | note `price_curve_partial`, basis in the plan |
| No load profile yet or corrupt `load-profile.json` | file read | corrupt: set aside as `.corrupt`, fresh profile; empty bins: mean of filled, else `planner.load.fallback_w` | `loadForecast.basis` |
| Goal input (SoC, target, deadline, want) stale or unavailable | signal state | goal inactive; entries of its device age out with the plan, then v2; the UI warns, a hard goal's risk is named | note `goal_input_stale`, goal status `inactive` |
| SoC of storage stale | signal state | storage not planned, v1 behaviour (charge only, no discharge). **Deferred with slice 6** (storage, deferred by the maintainer 2026-10-29): no storage is planned before then, so there is no such row to test yet | note |
| Hard goal infeasible or at risk | planner | `Urgent`, escalation, `shortfall` | status `infeasible` / `at_risk`, `goal_deadline_urgent` on the start |
| Solver failure: exception, `max_solve_time` exceeded, or the result fails the verifier (a slot over capacity, a negative energy, a non-finite number, a class outside `goal.sources`) | `PlanVerifier` before publish | the result is dropped, the last plan stays until `plan_max_age`, then v2 | note `solver_timeout` / `plan_invalid`, `lastSolve` |
| Planner stopped or throws | hosted service | dispatch unaffected; plan ages out; v2 | `status absent` |
| Clock step back | `ComputedAt > now` | plan discarded, replan at once; v2 meanwhile | `plan_fallback` line |
| Clock step forward, DST day | injected clock, slot grid | plan stale if older than `plan_max_age`, replan; DST slots from the zone rules | none |
| Restart | process start | the plan is **not persisted** (it is a pure function of inputs, and a stale plan is a hazard); goal progress is (energy goals); first plan after `start_delay`; v2 until then; adoption memory starts empty, the first plan is adopted at once | `status absent` |
| Home Assistant disconnected, guard safe state, kill switch, invalid configuration | existing | everything off as before; plan `suspended` and ignored; planner keeps its last inputs and does not fabricate fresh ones | `status suspended` |
| `planner:` removed on reload | parser | v2 exactly | none |
| `planner:` invalid (unknown key, bad mode, a parameter out of range) | parser | the whole document is invalid, as everywhere (no fallback to an earlier one); the planner gets no configuration and plans nothing, as with the section removed | error naming the key |
| A goal invalid in a semantic way (can never be evaluated) | parser | the goal is dropped with a warning; structural errors invalidate the document | issue code |
| Mode `shadow` | existing | plan computed, decisions are recorded as "would"; nothing is commanded | as 04 |
| Statistics file with an older schema | version | unaffected (the profile is its own file) | none |

Safe state, the kill switch and every row of 04 section 8 outrank all of the above.

## Test strategy

Control logic on the virtual clock (`FakeTimeProvider`) with the Warsaw zone; the 2026-03-29 (23-hour) and 2026-10-25 (25-hour) days are fixtures. Every test states why it matters (Rule 9).

- **Forecast fixtures**: a clear day, an overcast day, a day with a late shift, missing, stale at `max_age`, partial coverage, a dict and a list shape, an oversized frame, a malformed point, totals plus sun times.
- **Slot grid and load profile**: 23 and 25 slots per day, a half-hour offset zone, deadline cutting a slot, repeated 02:00 hour updating one bin twice, a lost hour none, holiday on the Sunday bin, profile file bounds and corruption.
- **Planner determinism**: golden plans (the worked example above is one, to the kWh; the zero-PV variant another), byte-equal output on repeated runs and under input permutation (device order, signal order).
- **Optimality bound**: on random small instances (2 to 3 goals, 12 slots) the plan's cost is compared with exhaustive search; equal when no cell is contended, within the stated gap otherwise; the test fails if the refinement ever raises cost.
- **Never causes an allocation the envelope refuses (property test)**: random goals, forecasts, prices, devices, phases and circuit groups; the verifier and an independent check assert per slot per constraint key that planned power is at most the guard's static allocation limit, that each device stays in its range, that classes are within `goal.sources` (plus escalation only for hard goals), that only declared `start: headroom` devices with goals have `Run`, and that every feasible hard goal is met. A second property drives the **dispatch engine** with random plans (valid and garbage) and asserts `MaxAllowed` and every granted increase never exceed what the same step without a plan allows, except the single class `plan`, which is itself behind the envelope, persistence and step limits.
- **Fallback is exactly v2**: with no `planner:` section, `planner.dispatch: off`, an empty, stale, future-dated, wrong-revision or foreign plan, the dispatch digest tests (balance worlds, synthetic replays, the 72 h LiveReplay) are byte-identical to v2 (PLAN-14, as ECO-15). `narrow` is a subset of v2's decisions: nothing starts that v2 refuses.
- **Gates**: a table of (plan entry, class, sources, balance state, lockout, envelope) to decision and reason code; a `Run` on surplus in a deficit hour starts nothing; `Defer` blocks only the zone class; `Urgent` ordering; a `restore_only` device is never started.
- **Re-planning**: forecast jitter of 5 percent never flips the current slot; a real change flips it after `adopt_cycles`; a goal change adopts at once; rate limit; a running device keeps its entry to the slot end; clock step back and forward.
- **Storage**: DP against exhaustive search on small SoC grids, efficiency, floor and ceiling, spread too small yields no cycle, no export unless allowed, discharge never planned above the house load, never counted as relief, `soc_floor` and `soc_ceiling` codes, the discharge command disabled by default.
- **Reason codes**: version 7 closed list, older lines read, a version 6 line naming a new code is not a decision, SPA list and en and pl sentences complete, `plan` block old-line and malformed-line behaviour.
- **Architecture**: the planner assembly references neither the effector, the command port, overrides nor guard runtime types; the guard assembly references no plan, forecast or curve type; `call_service` still lives only in the command port; no entity ID in Domain; no currency symbol; the dependency list in `10-STACK.md` is unchanged.
- **Live, shadow, control off** (slice 8): the plan runs for a week with `dispatch: off`; each day the plan's car and heater slots are compared with what dispatch did and with the real PV; one day is checked by hand against the contract zones; then `narrow`, then `full`, each with the maintainer's consent and a day in shadow first. No change to the live file without consent.

## Slice plan

Each slice: one implementation subagent, ends with a green build (0 warnings), tests, and something releasable in shadow. Slices 1 to 3 have no effect on decisions, 4 computes a plan nobody consumes, 5 is the first with an effect and is behind `planner.dispatch`. Version numbers are suggestions; 3.0.0 is slice 8.

| # | Slice | Scope and areas | Requirements | Acceptance | Depends on |
|---|---|---|---|---|---|
| 1 | Goal model, configuration, read-only | Domain `Goals` (`Goal`, `GoalRequest`, remaining-need maths, hard and soft, several goals per device, `IGoalSource`); `goal:` leaves `LaterKeys` and is parsed (kinds, forms, issues), device-scoped goal signals (`device_soc`, `goal_target`, `goal_deadline`, `goal_want`) created from devices; the goal ledger for energy goals, persisted in `state.json` (schema bump, old file read); `planner:` section parsed and `control.planner` parameters bound, not read; `GET api/plan` returns `enabled`, goals and their status only; wizard keeps `goal:`, `planner:` and the new keys. 0.11.0 | PLAN-01, 02, 03, 04, 05 | a 0.10 file loads unchanged and decides byte for byte; invalid goals give the listed issues; the ledger survives a restart on the virtual clock; `api/plan` shows goals with their inactive reasons; wizard round-trip | none |
| 2 | Forecast and price-curve signals | Infrastructure: bounded attribute whitelist in `SubscribeEntitiesParser`, `EntityUpdate.Series`; signal kinds `series`, `energy_forecast`, `timestamp`; roles `pv_forecast`, `pv_forecast_today`, `pv_forecast_tomorrow`, `sun_rising`, `sun_setting`, `price_curve_import`, `price_curve_export`; coverage and freshness; `ForecastInputs` beside `PriceInputs`; `curve:` in `DynamicPrice`; diagnostics page and `api/signals`; SPA role and kind lists, en and pl; read-only check of the maintainer's real forecast entities (what shape exists) before the slice closes. 0.11.1 | PLAN-06, 07 | shape, bound, stale, partial, wrong-unit tests; no entity ID outside the signal layer; the guard assembly forecast-free (architecture test); diagnostics shows series signals with age and coverage; a file without them unchanged | 1 |
| 3 | Slot grid, PV and load forecasts | Domain `SlotGrid` (DST, offset transitions, deadline cuts), `PvForecast` (integration, derate, half-sine totals shape), `LoadProfile` and `LoadForecast`, `load-profile.json` (Infrastructure, seeded from the statistics, fed hourly), `PlanInputs` assembly (prices per slot from the tariff, curve and fallback); `api/plan` gains `inputs` and `slots`. No planner yet. 0.11.2 | PLAN-08, 09 | slot counts on both DST days; profile update, seed, bounds, corruption; fallback bases; hand-computed forecast slots; `api/plan` slots match the tariff at every boundary | 2 |
| 4 | The pure planner and its service | Domain `Planner` (EDF fill, refinement, hard and soft, escalation, latest-safe-start), `PlanSnapshot`, `PlanVerifier`; Application `PlannerService` (own timer, `max_solve_time`, holder, `start_delay`, replan triggers, adoption hysteresis, `plan_*` log lines); `api/plan` gains devices, entries, cost, notes; plans computed in shadow, nothing reads them. 0.12.0 | PLAN-10, 11, 12, 16 (part) | golden worked example and zero-PV variant; determinism; the optimality-bound and never-refused properties; solver failure keeps the last plan; adoption table | 3 |
| 5 | Plan to dispatch, behind a flag | `PlanView`, `planner.dispatch` `off` / `narrow` / `full`; the plan class and `Defer`, `Done`, ceilings, `Urgent` in the allowance gate (`DispatchEngine.Balance.cs`) and the start walk; reason codes list version 7 (Domain, `ReasonCodes`, `DecisionJson`, SPA list, `reasons.ts`, en and pl, contract tests); the `plan` decision-input block; `plan_fallback`; 04 section 6 and 7a updated. 0.13.0 | PLAN-13, 14, 15, 16 (rest), 17 | `off` and every fallback row byte-identical to v2 (digest tests, LiveReplay); `narrow` a subset of v2; the dispatch property test; gate table; older log lines read | 4 |
| 6 | Battery arbitrage (droppable) | `storage:` keys and parser, `device_soc` for storage, DP in the planner, plan entries signed, dispatch charge direction through the plan, discharge ceiling rules and the disabled-by-default discharge command, codes `plan_discharge`, `soc_floor`, `soc_ceiling` in dispatch. 0.13.1 | PLAN-18 | DP against exhaustive search; floor, ceiling, efficiency, spread; never above the house load, never relief; default build never commands a discharge; fixtures only (no live battery) | 4, 5 |
| 7 | UI and manual goals | Day-plan screen, device card goal row (UI-12), "want now" with the manual goal store (`state.json`), `POST api/goals` and `DELETE api/goals/{device}`, strings en and pl, contract tests for the lists, both themes, phone width. 0.13.2 | PLAN-19, UI-12 | web tests; looked at in a browser at 375 px in both themes against sample payloads; manual goal survives a restart and expires; 404 and 409 behaviour | 4 (5 for the decision links) |
| 8 | Live shadow week and release | The planner on the maintainer's installation for a week, `dispatch: off`, then `narrow`, then `full` with consent; the 72 h replay with the planner on; the checklist of the testing section; zones, forecast shape and one day checked by hand; version 3.0.0 | PLAN-20, VER-05 (planner part) | checklist signed by the maintainer; no allocation refused by the envelope in the replay; plan vs actual PV and surplus plausible | 1-7 (6 optional) |

## As built (slice 1)

Goal model, `goal:` and `planner:` configuration, the goal ledger and read-only `GET api/plan`. Unreleased, version not bumped (0.11.0 is the suggested number), shadow only. **Nothing here reaches the guard, dispatch or the effector**: a file with goals and `planner: dispatch: full` decides byte for byte as the same file without them (a test runs the synthetic morning both ways and compares the digest, which is itself pinned to the 0.9 engine).

- **Domain** (`Switchboard.Domain.Goals`, `Switchboard.Domain.Planning`; pure, no entity, no clock). `GoalSpec` (a device's `goal:` as configuration reads it: names signals, never entities; `Origin` is `entity` when the target, deadline or want comes from an entity, else `config`; `Revision` is an FNV-1a fingerprint of what the goal is), `GoalRequest` (what one source asks for at one moment: remaining need in kWh drawn with the loss included, target, deadline, margin, hard, sources, escalate, on_met, since, origin, revision), `Goal` (a request with its status), `GoalMath` (`RemainingEnergyKwh` and `RemainingSocKwh`; the worked example gives 9.9), `GoalMerge` (precedence manual > module > entity > config for one device and one deadline, the loser listed as superseded, several goals per device kept and ordered by deadline, ties to the harder goal then the lower revision, independent of input order), `GoalLedgerEntry` and its `Continues` rule. `PlannerDefinition`, `PlannerDispatchMode` and `PlannerParameters` hold `planner:` and `control.planner`.
- **`goal:`** (`GoalSectionParser`, called by `DeviceSectionParser`; `goal` left `LaterKeys`, `guards` stays). Keys `kind` (`energy`, `soc`, `none`), `hard` (default false), `capacity_kwh`, `loss_fraction` (in [0, 1), default `control.planner.goal.default_loss_fraction`), `soc`, `target` (soc) or `target_kwh` (energy), `deadline`, `want`, `sources`, `escalate` (default: the value of `hard`), `on_met` (default `opportunistic`). A number is a literal (`90`, `{ value: 90 }`) or `{ entity, max_age }`; an energy target entity may add `unit` (Wh, kWh, MWh). `deadline` is `{ at: "HH:mm" }` (recurring local time) or `{ entity, max_age }`, each with an optional `margin`. `kind: none` is no goal, quietly. Issues: `missing_key` (soc needs `capacity_kwh`, `soc` and `target`; energy needs `target_kwh`), `key_not_allowed` (a key of the other kind), `out_of_range` (`loss_fraction`, durations), `invalid_value` (`at`, an entity of a domain that cannot carry the input, value and entity together or neither), `invalid_entity_id`, `duplicate_entity` (one entity feeds one signal, goal inputs included), `unknown_key`, the warning `sources_unusable` (a goal zone the tariff lacks is dropped, never widened), the new warning `goal_without_start` (a goal on `start: restore_only`) and the new warning `goal_dropped` (a goal whose literal can never be evaluated, a percentage outside 0 to 100, is dropped and the file stays valid: the design's "semantic problems narrow, structural ones invalidate"). Two deviations from the design's wording: a soc goal also requires `target` (the design's list names only `capacity_kwh` and `soc`), and a literal `soc` is accepted.
- **Goal signals** (PLAN-02). Created per device like a draw, so the entity ID stays in the signal layer: `device_<id>_soc` (kind `percentage`, role `device_soc`), `device_<id>_goal_target` (`percentage`, or `energy_counter` for `target_kwh`, role `goal_target`), `device_<id>_goal_deadline` and `device_<id>_goal_want` (kind `state`, roles `goal_deadline` and `goal_want`). The kinds `series`, `energy_forecast` and `timestamp` do not exist before slice 2, so a deadline is read as a state string (`GoalDeadlines.Parse`: an ISO instant with an offset, a local date and time, or a local time of day, read in the injected zone; a time that happens twice is the first, one that does not exist moves to after the gap). `max_age` per entity, default `control.planner.goal.input_max_age`, at most `control.signals.max_age_limit`. The goal model reads the signal layer only through `GoalInputs` (`Switchboard.Application.Signals`: name to state, detail and value), so the full snapshot stays with the signal layer and diagnostics.
- **Inactive goals.** `ConfigGoalSource` evaluates in the order want, soc, target, deadline and stops at the first input that cannot be used: `want_off`, `input_missing` (not arrived, entity missing), `input_stale`, `input_unavailable` (unavailable or unknown), `input_invalid` (rejected by the signal layer: not a number, a wrong unit; an unreadable on/off or deadline), `input_out_of_range` (a percentage outside 0 to 100, a negative energy target). Never guessed, never an old value.
- **Statuses.** Slice 1 reports `on_track`, `met` (remaining below 1e-9 kWh), `expired` (soft, deadline passed), `missed` (hard, deadline passed, still listed with its remaining need) and `inactive`; `at_risk` and `infeasible` need the plan (slice 4). A recurring `at` deadline is the next occurrence strictly after now, so such a goal rolls to the next day the moment it passes and is never missed; its ledger entry starts at zero then.
- **Seam** (`IGoalSource`, `GoalContext`, `GoalOffer`, `GoalEvaluator`). The design's `Goals(now, snapshot) -> GoalRequest[]` returns a `GoalOffer` (`Requests` plus `Inactive`), because an inactive goal has to say why. The configuration is the only source built; the manual store (slice 7) and the EV module are further `IGoalSource`s merged by `GoalMerge` (tested with a fixed source).
- **Ledger** (PLAN-03; `GoalLedger`, `GoalTracker`). An energy goal's progress is the controlled energy of its device since the goal began: at every control step the tracker integrates the previous step's device power (`PowerView`: measured plus rated-model, an unknown power counts nothing) over the time since that step, if it is within `Statistics:MaxIntegrationGap` and not backwards, exactly as the statistics do. It is a listener of the guard runner after the presentation hub, holds no command port, kill switch or override, isolates its own failures and only counts. Entries begin when an energy goal is first active, follow a deadline that moved before it passed, start again when the deadline they counted toward has passed and another one applies (`GoalLedgerEntry.Continues`), end when `want` goes off or the goal leaves a valid configuration, and are kept (and keep counting) while an input is merely missing or the configuration is invalid. A soc goal has no entry (its progress is the SoC signal).
- **`state.json` schema 4.** `goals` per device: `kind`, `since`, `deadline` (or null), `delivered_kwh`, `updated_at`; read as untrusted (exact keys, device-name keys, `delivered_kwh` in [0, 1e6], round-trip times; anything else sets the whole file aside). A schema 3 file reads as no goals; schema 1 and 2 as before. A goal that begins, ends or moves its deadline is written at once, progress with the next interval save and at the clean stop (`RuntimeStateCoordinator.FlushAsync` queues the last of it). A rollback to 0.10.x sets a schema 4 file aside like any untrusted file (as the schema 3 bump did for 0.6.x). The stretch between the last step before a stop and the first step after a start is not counted, so a restart can only under-count.
- **`planner:`** (PLAN-05, `PlannerSectionParser`). `dispatch: off | narrow | full`, default `off`, parsed and echoed; slice 5 is the first reader. An absent or empty section is `null` and the file is a 0.10 file; `planner: {}` is enabled with `off`; an unknown value or key is an error. `control.planner` (`control-defaults.yaml`, every key required, none read before slice 4): `cycle`, `min_replan_interval`, `plan_max_age`, `max_solve_time`, `start_delay`, `horizon`, `slot`, `pv_derate`, `pv_shape`, `refine_passes`, `adopt_cycles`, `adopt_min_change_fraction`, `progress_tolerance_fraction`, `hard_goal.escalation_margin`, `load.*`, `storage.*` as in "Configuration" above, plus `goal.input_max_age` (1h) and `goal.default_loss_fraction` (0), the two slice 1 parameters. Checked: `slot` in 15m, 30m, 60m; `horizon` not below a slot; `plan_max_age` not below `cycle`; `pv_derate` in (0, 1]; fractions in range; `adopt_cycles` at least 1; unknown keys refused.
- **`GET api/plan`** (`PlanEndpoint`; read-only, ingress only). `enabled` (a `planner:` section exists), `dispatchMode` (null when not enabled), `status` (always `absent`: there is no plan), `inputs { goalsActive, goalsInactive[ { device, reason, input } ] }`, `devices [ { device, goal, goals } ]` (`goal` is the first goal by deadline, `goals` all of them; a goal carries kind, hard, source, targetKwh or targetSoc, currentSoc, deadline, marginS, remainingKwh, deliveredKwh, status, inactiveReason, inactiveInput, onMet, escalate, sources, since), `superseded`, `notes` (empty), `issues` (those under `planner` and `devices[n].goal`). The goals are shown whether or not a `planner:` section exists. No slots, entries, cost or computed-at yet. Evaluated on demand against the signals of the latest store cycle (no side effects).
- **Wizard** (PLAN-01). `planner:` is a preserved top-level section and `goal:` a device key the wizard does not own: a save keeps both unchanged and the file still parses (tested with `deploy/examples/planner-goal.yaml`); a file without them gains neither. `DeviceSectionParser.KeysFor` includes `goal`. The SPA gained the four roles and the two issue codes (en and pl); it has no `api/plan` client (slice 7).
- **Tests.** Domain model, configuration (goal and planner issues, defaults, overrides), the sources on real signals on the virtual clock (stale, missing, unavailable, wrong unit, out of range, want, deadline forms, the 25- and 23-hour days), the ledger on the virtual clock (integration, gaps, a clock stepping back, cycle roll, restart through `state.json`), persistence (schema 3 read, untrusted content), the endpoint, wizard round trip, architecture (no goal type reachable from the guard or dispatch, no goal word in their sources, no command or entity in the goal code) and the byte-for-byte digest. Existing tests changed because of the schema bump (4) and because `goal` is no longer a reserved key (`guards` stands in for it in the reserved-key test); the architecture test of the goal code is the one to update deliberately in slice 5.
- **Not done, or uncertain.** (1) A helper entity (`input_number`, `input_boolean`, `input_datetime`) reports only when it changes, so `max_age` against `last_reported` makes a goal input go stale after its `max_age` even though its value has not moved; the default is 1h and a user can set up to `control.signals.max_age_limit`, and whether the freshness probe keeps such an entity fresh on the live HA is not verified. (2) The checks that belong to slice 2 (`curve` without `signal`, `forecast_signal_unused`, a `series` signal needs `attribute`) are not built. (3) `planner.dispatch` narrow and full are accepted and echoed; nothing reads them. (4) No SPA page for `api/plan`, no CHANGELOG entry (no version bump).

## As built (slice 2)

Signal layer only (unreleased, shadow, no effect on any decision). New kinds `series`, `energy_forecast`, `timestamp`; roles `pv_forecast`, `pv_forecast_today`, `pv_forecast_tomorrow`, `sun_rising`, `sun_setting`, `price_curve_import`, `price_curve_export`. A series signal names one attribute (`attribute:`) and its layout (`series: {shape auto|list|dict, value power_w|energy_wh_period, start_key, end_key, value_key}`); a price curve declares a price unit instead of `series.value`. The transport (`SeriesAttributeParser`) keeps only that attribute, at most 512 points and 64 KiB (UTF-8 bytes): an oversized frame is dropped whole, a malformed item refuses the whole series, a time without a UTC offset is refused, the result is time-sorted, a duplicate time is an error. The normalizer gives the numbers their unit (a curve per MWh divided there only, the currency label checked against `economics.currency`; a day total in kWh; negative power or energy is malformed). Freshness is `last_reported` against `max_age`; coverage (`ForecastSample.CoversUntil`) is separate. A stale, unavailable or refused signal carries no content, so the last known curve is never used; a probe answer may confirm a series but never replace it. `ForecastInputs` is `SignalSnapshot.Forecast`, beside `Prices`, and is in none of `ControlInputs`, `ProtectionInputs`, `DispatchInputs` (architecture test: the guard assembly has no forecast type). `curve:` in a dynamic price (`DynamicPrice.Curve`) names a price-curve signal and changes no dispatch price. Issues: `series_value_required` (error), `forecast_signal_unused` (a forecast or sun role with no `planner:` section, a curve role no price names), `curve_without_signal` (the price is then its fallback), `unit_mismatch` for a curve in another currency. `api/signals` carries `series {valueKind, points, from, coversUntil}`. Deviations from the design: item keys of a list series are configurable (defaults `start`, `end`, `value`) so Nord Pool, Tibber and ENTSO-E shapes read without vendor code; Solcast reports `pv_estimate` in kW, which a PV series does not accept (W or Wh per period only); an empty list or dict is `SeriesMissing`; `max_age` of the sun signals is capped by `control.signals.max_age_limit` (24 h), the example above uses 24h. **Not done:** the read-only check of the maintainer's real forecast entities on the live HA (open question 2 stays open; no token path in the repo notes).

## As built (slice 3)

Domain `Switchboard.Domain.Planning` (unreleased, no effect on any decision). `SlotGrid` cuts at local slot boundaries and at every UTC-offset transition (golden: 23 and 25 hourly slots on 2026-03-29 and 2026-10-25 Europe/Warsaw, a half-hour-offset zone, a 23.5 h day on Lord Howe), the first slot is shortened to `now`, cuts (deadlines) and the horizon end split slots; slot 15, 30 or 60 min. `PvForecast` takes plain inputs, not the signal-layer type (power points by the trapezoid, period energies by overlap, daily totals with sun times as an exact half-sine), applies `pv_derate`, zero beyond coverage, reports basis (none, series, totals) and coverage. `LoadProfile`: 168 bins Monday first, a holiday uses the Sunday row, a repeated 02:00 updates the same bin twice, a lost hour none; exponentially weighted by elapsed time with the half-life; an hour counts only when observed for at least `min_observed_fraction`; **the sample is kWh per observed hour** (energy is integrated over observed time only, so raw energy would read a gap as low load); negative `Uncontrolled` counts as 0. `LoadForecast`: an empty bin takes the mean of the filled bins, none filled takes `fallback_w`; basis profile, partial, fallback; `DaysFilled` is filled bins / 24 rounded down. `PriceCurve` and `SlotPrices`: time-weighted slot price, curve over tariff, tariff for gaps (`price_curve_partial`); the curve keeps negative market prices (a dynamic tariff total does not). The export slot price is the export tariff time-weighted, not routed through `ISettlementModel`. `PlanInputs.Build` assembles zone, import and export price with basis, PV, base load, surplus and notes. `load-profile.json` (`LoadProfileFile`): schema 1, atomic write, 64 KiB cap, strict on read, corrupt set aside as `.corrupt`; `LoadProfileKeeper` seeds from the retained buckets and feeds every completed local hour (a `FedThrough` mark prevents double counting). `ForecastParameters` holds slot, derate, half-life, min observed fraction and fallback (validated in the constructor, no literals). Known limits: no ageing of a bin untouched for months, no temperature dependence, no variance.

### PLAN-06 closing: the degraded totals shape (code complete 2026-10-29, not yet verified on the live HA)

Read-only check of the maintainer's HA: no hourly series entity exists. There are the core forecast_solar sensors `sensor.energy_production_today` and `sensor.energy_production_tomorrow` (kWh, device class energy, no attributes, refreshed hourly) and `sun.sun` (state `above_horizon` / `below_horizon`; attributes `next_rising`, `next_setting` as ISO strings with `+00:00`, plus `elevation`, `azimuth`). So the degraded shape of "PV forecast shapes" is the one in use.

- **`attribute:` for kind `timestamp`.** A timestamp signal may name one attribute whose text is the instant (`entity: sun.sun, kind: timestamp, attribute: next_rising, role: sun_rising`); without it the state is the instant, as before. The name must be a plain identifier (letters, digits, underscore, not starting with a digit, 1 to 64 characters), so the whitelist is a name, never a path or a pattern. No other kind than `series` and `timestamp` has `attribute:` (`key_not_allowed`). The transport (`SubscribeEntitiesParser`) keeps the text of each declared attribute (at most `EntityUpdate.MaxAttributeText` characters; absent, not text or oversized reads as empty and the sample is refused as `not_timestamp`), `EntityUpdate.Attributes`, `TrackedEntity.Attribute`, `IEntitySubscriptionSink.TimestampAttributes` (a changed set needs a new subscription, as for series specs). Freshness stays the entity's `last_reported`; a probe answer carries no attributes, so it confirms a sun time but never replaces it.
- **One entity, two sun signals.** `sun.sun` has to feed both `sun_rising` and `sun_setting`, so "one entity feeds one signal" has one exception: timestamp signals that read different attributes of it (`duplicate_entity` otherwise). The store keeps a list of signals per entity.
- **`PlanSourceMapper.Pv(forecast, zone, now)`** (and `Map(..., now)`): a usable series wins; when there is no usable series, today's and tomorrow's usable `energy_forecast` totals are spread over daylight with `PvForecast.FromDailyTotals` (basis `totals` in `api/plan` `inputs.pvForecast.basis`, the SPA already lists it). `sun.sun` gives the NEXT events, so after sunset both are tomorrow's. The day's window is derived by moving the events by whole 24-hour spans of absolute time (not local clock time: the sun does not follow a DST change, so a 23- or 25-hour day keeps its daylight) to the one whose local date is the day, and the set is the first one after that rise within 24 hours; a few minutes of drift a day over at most `MaxSunShiftDays` (3) is far below a slot. The total of "today" is the whole day's, so slots already past take their part and are not planned anyway. A day whose total is stale or unusable gets no PV, a stale or missing sun gives none for both days, a window that cannot be made (rise not before set, a day over 24 hours) or a negative total gives `PvForecast.None`: zero PV with basis `none`, never the last known curve. The state shown for the PV forecast is the `pv_forecast` signal's, else the today total's.
- **Example**: `deploy/examples/planner-goal.yaml` (commented signals block). **Tests**: `PlanSourceMapperTotalsTests` (before sunrise, midday, after sunset, 23- and 25-hour days, stale totals, missing sun, series wins), `SunAttributeSignalTests` (the maintainer's 9.655 / 12.903 kWh, sun stale, probe), `ForecastConfigurationTests`, `SeriesAttributeParserTests`, `PlanInputsEndpointTests`.
- **Series times without an offset**: the Forecast.Solar `watts` keys are the site's local time (`2026-10-29 07:00:00`); a series time without a UTC offset is read in the add-on's zone (the slot grid's), so that zone must be Home Assistant's. Spring-gap times move one hour on, repeated autumn times are the first. Tests: `ForecastSolarSeriesTests`. Example signal: `deploy/examples/planner-goal.yaml` (`sensor.pv_forecast_watts`, `attribute: watts`, `max_age: 3h`).
- **Rejected for D10**: the WebSocket `energy/solar_forecast` route (an hourly `wh_hours` series for the Energy dashboard) is a command, not an entity, and the add-on reads entities only. The alternative that keeps hourly shape is a REST sensor in HA over the Forecast.Solar API with a watts attribute (`result.watts`, a dict time to W): a `series` signal with `attribute: watts`, `shape: dict`, `value: power_w` reads it as it is, and then the series wins over the totals.
- **Open**: install this build and check on the live HA that `sensor.energy_production_*` and `sun.sun` feed the plan (`inputs.pvForecast.basis: totals`); PLAN-06 is ticked only then.

## As built (slice 4)

Unreleased, shadow, no effect on any decision: plans are computed and shown, nothing reads them (dispatch, guard and effector untouched; digest tests unchanged).

- **Domain** `Switchboard.Domain.Planning`: `Planner.Plan(PlanInput, CancellationToken) -> PlanSnapshot` (`Planner.cs`), types in `PlanTypes.cs` (`PlanInput`, `PlanDevice`, `PlanEntry`, `PlanGoal`, `DevicePlan`, `PlanCost`, `PlanSnapshot`; `Id` is a content fingerprint, `Canonical()` is the byte-for-byte text used by the determinism tests), `PlanVerifier.Verify(input, plan)` with 11 `PlanViolationCode`s. `PlanNote` gained `GoalShortfall`, `GoalInfeasible`, `GoalUrgent`, `GoalMissed`, `GoalInputStale`, `SolverTimeout`, `PlanInvalid`.
- **Application** `Planning/`: `PlannerService` (own timer on the injected clock aligned to :00/:15/:30/:45, `start_delay`, triggers, rate limit, adoption hysteresis, `max_solve_time` via `Task.Delay` on the injected clock, cooperative cancel), `PlanHolder` / `PlanState` / `PlanStatus` (fresh, stale, absent, suspended, error), `IPlannerSource`, `IPlannerSuspension`. The constructor is closed on six dependencies (architecture test).
- **Host**: `DiagnosticsPlannerSource` (builds `PlanInput`; static per-phase and group capacity from `ProtectionSetup`: `min(In - reserve, may_add peak x In) x nominal voltage`, same `ConstraintKey` as dispatch), `PlannerSuspension` (guard safe state, kill switch, HA link), `PlannerHostedService`. No `planner:` section means idle.
- **`GET api/plan`** additions (camelCase): `id`, `computedAt`, `validUntil`, `horizonEnd`, `slotMinutes`, `configRevision`, `suspended`, `cost {planned, referenceV2, referenceAsap, currency}`, `lastSolve {at, durationMs, result, reason, violations[]}`, `status` fresh / stale / suspended / absent / error; per device `entries[] {start, end, want, class, energyKwh, maxPowerW}`, `shortfallKwh`, `predictedCompletion`; per goal `plannedKwh`, `shortfallKwh`, `latestGridStart`, `urgent`. Earlier keys kept. Without `planner:` no plan is shown.
- **Logs** `plan_*` (not decisions, no reason code, no entity ID). A solver exception, timeout or verifier rejection keeps the last plan and logs it.
- **Measured**: the worked example solves in about 0.4 ms (Release, 1 goal, 4 devices, 50 slots); random instances up to 40 slots below 1 ms. Tests: .NET 3118 (3021 before), build 0 warnings.
- **Golden**: Tuesday 09-14 cells 0.57, 1.59, 2.36, 2.61, 2.27, 0.50, 9.9 kWh, cost 3.465 (doc 3.47), `latest_grid_start` Wed 03:11:20, `Urgent` from 02:41:20; zero-PV variant Mon 21-23 at 2.99 + 0.93 at 00:00, cost 5.94, note `pv_forecast_missing`. Rounding tolerance 0.0055 (the doc rounds 2.355 to 2.36).
- **Optimality bound** (593 comparable small integer instances against exhaustive search): one goal exact in all 300; with competition 589 of 593 exact, worst complete plan 25 % dearer, 1 instance left a goal short that exhaustive search placed (explicit `goal_shortfall`). The test enforces at least 98 % exact, gap at most 25 %, unmet at most 1 %, never cheaper than the optimum. Cause: greedy fill, no min-cost flow; a shared PV pool between devices with separate power limits can be split suboptimally.
- **Deviations from the design text**: refinement is an order search over goals (all permutations up to 4 goals, single-goal moves up to 8, structural size bounds, `refine_passes` passes, a new order kept only when better: less unmet need, then lower cost) because release-and-refill cannot improve under contention; EDF order is hard first, then earlier deadline, no queue-jumping by critical goals (else an infeasible urgent goal starves a feasible earlier one); an entry has one class (all surplus = `pv`, any grid energy = `zone:<dearest used>`); `planner.min_run` does not exist in `control-defaults`, the device's `min_on` is used and a cell below min power x min_on is skipped except a goal's last remainder; a device whose minimum power exceeds its static limit is never planned (goal infeasible); a goal with a deadline beyond the horizon is neither infeasible nor escalated and its unplaced part is no shortfall; `restore_only` and storage devices get `None` (the goal of a `restore_only` device is listed and reserves nothing); stepped devices use minimum = smallest stage, maximum = sum of stages (approximation); hysteresis scales the held current-slot entry to the rest of the slot and compensates in later slots, the merged plan must pass the verifier or the new plan stands whole (`plan_hold_dropped`, debug); triggers replan at once but at most one per `min_replan_interval` (config reload, clock step and safe-state exit bypass it); a change of the forecast digest waits for the cycle boundary; `referenceV2` and `referenceAsap` are time-ordered fills from now without the value test.
- **Known gaps**: no storage (slice 6), nothing reads the plan (slice 5), no UI (slice 7); plan cost is not recomputed after the hysteresis hold; the verifier flags a hard goal with no entry only when no other entry shares its constraint or PV; progress tolerance can trigger a replan for a goal whose device is not planned (manual charging); not checked on the live HA, PLAN-06 stays open.

## As built (slice 5)

Unreleased, shadow default (`planner.dispatch: off`). With `off`, no `planner:` section, or no usable plan, decisions are byte for byte v2 (digest tests ECO-15 unchanged: balance worlds, synthetic morning, live 72 h).

- **`PlanView`** (`Application/Planning/PlanView.cs`): `IPlanView` (`Mode`, `For(device, now)`, `Observe`). `For` returns the entry of the slot containing `now` only for a fresh plan (within `plan_max_age`), not dated in the future, `now` inside the horizon, not suspended, with matching `ConfigRevision`, `SpecIdentity` and `GoalRef`; `None` entries and `Done` without `on_met: stop` give null. Anything else is null, the v2 path.
- **Dispatch** (`DispatchEngine.Plan.cs`, point edits in `.Balance.cs`, `.cs`, `.Economics.cs`; the engine takes the view through its constructor, `DispatchInput` and `GuardRunner` unchanged): narrowing happens before allocation (`Done` with `on_met: stop` = `goal_met`, ceiling `MaxPowerW`, slot budget `plan_slot_target`), `Defer` refuses a zone-class start or increase (`plan_deferred`). In `full` only: class `plan` for `Run` on a declared goal zone (`plan_slot_target`, no price test, no zone-end margin) and `Urgent` first in the start walk with every zone open (`goal_deadline_urgent`), both behind every other gate. A device started by class `plan` is not stopped by the price test on the next step. `restore_only` is never started.
- **Reason codes list version 7**: appended `plan_deferred`, `goal_deadline_urgent`, `plan_discharge`, `soc_floor`, `soc_ceiling` (the last three have no emitter before slice 6); `plan_slot_target` and `goal_met` got emitters and reworded sentences; `ReasonCodes.EconomicsListVersion = 6` kept for the older constants; the log reads versions 7 to 2. SPA list and en/pl sentences updated.
- **Decision `plan` block** (`PlanDecisionInputs`, strings only): `status`, `plan_id`, `computed_at`, `slot_start`, `slot_end`, `want`, `class`, `energy_kwh`, `delivered_kwh` (an estimate), `max_power_w`, `mode`; written only when `dispatch != off` and an entry was used; `api/decisions` carries `plan: null` otherwise. A line with codes below 7 or a malformed block is not a decision.
- **`plan_fallback entered` / `left`** log lines (warning / information, reason without entity ID: `suspended`, `no_plan`, `solver_failed`, `plan_too_old`, `plan_from_the_future`, `outside_the_horizon`, `configuration_changed`), once per change, only when mode is not `off`.
- **Tests**: `PlanViewTests` (15 fallback rows), `PlanDispatchGateTests` (23, one per row of "how the plan meets the gates"), `PlannerDispatchDigestTests` and `PlannerLiveDispatchDigestTests` (digests equal v2 for off, absent, stale, future, outside horizon, foreign revision, suspended; a control row with a valid plan changes the digest), `PlanNeverWidensTests` (property, 600 worlds, fixed seed, garbage plans with NaN, negative and infinite values, mutation-checked), `PlanDecisionJsonTests`. `GoalArchitectureTests` and `PlannerArchitectureTests` updated knowingly: dispatch may name only `IPlanView`, `PlanEntryView`, `PlanDecisionInputs`, `PlanWant`, `PlannerDispatchMode`; the guard, guard domain, effector and `Domain/Dispatch` still name nothing of the plan.
- **Deviations**: the pinned digests are kept by reading the serialized line as `"codes":6` in `EconomicsBackwardCompatibilityTests.Add` (one `Replace`, commented), because the list version number is part of the line; class `plan` opens `EnergyClass.Zone` in the log (no new enum value, which would force SPA and `api/economics` changes), the reason says the plan did it; class `plan` opens only a zone named in `goal.sources` (a goal without `goal.sources` leaves the device its own `sources`, stricter than "any"); class `plan` and `Urgent` act only for devices that declare `sources:` (a goal device on the v2 balance rule is not widened, only narrowed); the ceiling can lower `MaxAllowed` and so take a stage off; the slot budget blocks only increases (also a start from zero) for `Run`, `Urgent` ignores it, delivered energy is an under-estimate when power is unknown.
- **Gaps**: no host test of `DiagnosticsPlanCurrency` on live signals (stub plus DI test only); revision, spec and goal refs in production are first exercised in the shadow week; the "nothing reads yet" comment in `SwitchboardServices` is stale.

## As built (slice 7)

Unreleased, shadow. Closes the UI side of UI-12 and PLAN-19, but not looked at in a browser (see gaps).

- **Manual goal store** (`Domain/Goals/ManualGoal.cs`, `Application/Goals/ManualGoalStore.cs`): an `IGoalSource` of origin `manual` and `IManualGoalControl`, one manual goal per device; refused for an undeclared device, no configuration, target out of range, deadline in the past or beyond `control.planner.horizon`, a `soc` goal on a device without a `goal:` of kind soc. Expires at its deadline or when the device leaves the configuration. Loss fraction from the device's `goal:` or `control.planner.goal.default_loss_fraction`; sources, escalate and on_met from the device's `goal:` (else none, escalate = hard, `opportunistic`). Precedence manual over entity over config per device and deadline through `GoalMerge`, the loser in `superseded`; goals with different deadlines coexist. Never writes an entity (constructor takes `IGoalInputSource`, `SignalClock`, logger; architecture test).
- **`state.json` schema 5**: key `manual_goals` per device `{kind, target, deadline, hard, since, delivered_kwh, updated_at}`; a schema 4 file reads as no manual goals; an older build sets a schema 5 file aside like any untrusted file. Written at once on begin and end, progress with the interval save and on flush.
- **API**: `POST api/goals` `{device, kind: energy|soc, target, deadline (ISO with offset), hard}`, exactly those keys, `application/json`, 1024 B, answer `{device, kind, target, deadline, hard, since, deliveredKwh, created}`; refusals `{code, message}`: 400 `bad_request`, 413, 404 `unknown_device`, 409 `no_configuration`, 422 `soc_not_configured` / `target_out_of_range` / `deadline_passed` / `deadline_beyond_horizon`. `DELETE api/goals/{device}`: 200 `{device, ended: true}`, 404 `unknown_device` / `no_manual_goal`. A change wakes the planner through `IPlanTrigger.Wake()` (implemented by `PlannerService`, logic unchanged; rate limit by the existing code); the same POST twice gives `created: false` and no wake. `api/plan` `GoalDto` gained `device` (only in `superseded`).
- **UI** (`web/`): `#/plan` (navigation item 06): status badge, "preview only" for `dispatch: off`, estimate-not-a-guarantee and not-a-protective-device sentences, inputs, notes and issues, SVG chart (zone band by price rank, PV line, bars per device by class, "now" marker, 23 and 25 hour days by real time) with a `<details>` table, per device goal, runs, predicted completion, shortfall, cost against `referenceV2` and `referenceAsap`. Device card goal row (progress, remaining, deadline, margin, hard or soft, source, status, `latestGridStart`, `urgent`, shortfall, next runs, superseded requests) with "Want now" and cancel. en and pl, entity IDs in monospace, colours from tokens only.
- **Tests**: .NET `ManualGoalStoreTests`, `ManualGoalPersistenceTests`, `GoalsEndpointTests`, `PlanSpaContractTests`, `ManualGoalArchitectureTests`; pins 4 to 5 in three persistence tests; web 1358 (1201 before).
- **Deviations**: schema 5 instead of an optional key in schema 4 (the parser demands exact keys, schema 4 is unreleased); manual progress is counted in the store, not the ledger, so the same energy counts to both goals when a device has a file goal and a manual one; a manual goal needs a deadline; body limit and target ranges (1 000 000 kWh, 100 %) are constants in code; `IPlanTrigger` instead of injecting the sealed `PlannerService`; `GoalTracker.Follow` filters manual goals out of the ledger loop.
- **Gaps**: not looked at in a browser at 375 px in both themes (mock server in the agent's temp directory, `#/plan` and `#/devices/ev_charger`; the SVG chart may need CSS fixes); a manual goal on a `restore_only` or storage device is accepted but the planner gives it no entries and the UI does not say so; `lastSolve.reason` is free text, the contract test does not pin the list.

## Open questions for the maintainer

Each has a recommendation, and the design above already uses it. Items marked "would change" say what moves if the answer differs.

1. **Solver approach.** Recommend **no solver library**: an in-repo deterministic planner (earliest-deadline-first merit-order fill over price-tiered cells, a capped refinement pass, and an exact DP for storage). The problem is tens of slots and a handful of devices; the code is a few hundred lines, bit-for-bit reproducible, testable against exhaustive search, and costs no dependency. The library route was weighed and rejected: Google.OrTools is Apache-2.0 but ships a large native package whose aarch64 build is a risk for the multi-arch image; HiGHS is MIT but has no maintained, permissively licensed .NET binding to pin (a native build of our own would be a new supply chain); GLPK is GPL (refused); a hand-written simplex is the worst of both (numerical edge cases, nothing to compare it with). Would change: if joint optimality across several batteries and many contended goals is ever needed, a library goes into `10-STACK.md` with an exact pin and an architecture test that the planner still produces a valid plan without it.
2. **Forecast source and entity shape.** Recommend the series attribute (list of `{start, value}` or dict time to value) as the primary shape and the daily totals with sun times as a degraded shape, both built in slice 2; verify read-only which entities the maintainer's Forecast.Solar actually exposes before the slice closes. Would change: if only totals exist and the maintainer does not want a REST sensor, the plan runs on the half-sine shape (coarser, still useful).
3. **Battery in 3.0 or deferred.** Recommend in 3.0 as slice 6, plan and charge direction, with the discharge command disabled in code until a real battery has run in shadow (the maintainer has none; nothing can be verified live). Would change: deferring drops slice 6 and PLAN-18 only; the other slices do not depend on it.
4. **Planning slot: 15 minutes or hourly.** Recommend hourly slots (parameter `planner.slot`): the netting hour, the G13 zone edges, the PV forecast and the load bins are all hourly, so a finer slot adds states, not information; the 15-minute period is the replanning cadence, and the current hour is executed continuously against its remaining energy. Would change: a market with 15-minute prices sets `slot: 15m`; the same code runs, with more cells.
5. **May the plan start devices that dispatch would not start by itself.** Recommend yes, narrowly: only a device that declares a goal and `start: headroom`, only on classes in `goal.sources` (plus any zone for a hard goal at risk), behind every gate, and only in `planner.dispatch: full`, which stays off until `narrow` has had a shadow week. Would change: answering no removes class `plan` and `Urgent` from slice 5 (a hard goal then relies on the device's `sources` and the replan) and loses the guarantee of a deadline in a zone the device does not list.
6. **How `want` is entered.** Recommend mapped HA entities (an `input_boolean`, `input_number`, `input_datetime`, or an automation) plus a recurring static goal in the file plus a "want now" action on the device card stored in the add-on's own state; the EV module later feeds the same seam from the calendar. Would change: a different primary input (for example a dedicated MQTT number) is another `IGoalSource`; the model does not move.
7. **Tie-break among equal-cost slots.** Recommend the earliest slot (robust against under-delivery, the plan converges early). Would change: latest keeps the car lightly charged longer and delays the grid draw; one comparator in the planner.
8. **Starting values with no origin**: `pv_derate` 0.85, `load.fallback_w` 400, `adopt_cycles` 2, `adopt_min_change_fraction` 0.05, `escalation_margin` 30 m, `plan_max_age` 45 m, `storage.min_spread` 0.10. To review after the shadow week; all are configuration.
