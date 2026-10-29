# Requirements — v1.0 Capacity Guard

**Defined:** 2026-10-23
**Core Value:** A user with a 3×25 A connection can charge an EV, heat water and run a battery at the same time without tripping a breaker, while every kWh goes where it saves the most — set up through a wizard, not a YAML file.
**Release goal:** Ship the protection layer as a standalone, usable product: meter mapping, per-phase limits with a breaker thermal model, merit-order shedding, flow view, decision log, shadow mode.

Checkboxes stay unchecked until the requirement is verified against a running build.

## Signals and metering

- [x] **SIG-01**: Signal layer maps logical signals to HA entities; no core component references an entity ID
- [x] **SIG-02**: Per-phase current from the main meter, one entity per phase, with unit normalization (A, mA) and configurable sign convention
- [x] **SIG-03**: Direction of flow resolved either from a signed current, or from a separate power/direction entity, or declared unavailable (cautious mode)
- [x] **SIG-04**: Optional per-phase active power; when present, power is used for dispatch and current for protection — never the reverse
- [x] **SIG-05**: Every signal carries `max_age`; a signal past `max_age` is `stale` and marks dependent devices unsafe
- [x] **SIG-06**: Freshness is computed from the HA `last_reported` timestamp, not `last_changed`
- [x] **SIG-07**: Refresh interval auto-detected and surfaced; the minimum safe reserve is derived from it and shown during onboarding
- [x] **SIG-08**: Inverter production signal, where present, used to distinguish an idle phase from a phase whose load is covered by local generation
- [x] **SIG-09**: Balance consistency check: residual of `generation + import − measured loads − controlled loads` beyond a configurable threshold rejects the sample, never the decision

## Connection model

- [x] **CONN-01**: Three-phase installations supported with an independent limit per phase; single-phase and summed-only are configurable alternatives
- [x] **CONN-02**: Each device is assigned to a phase (or to all three); the assignment drives every protection decision
- [x] **CONN-03**: Sub-breaker groups: a set of devices sharing a downstream breaker with its own per-phase limit, enforced alongside the main limit
- [x] **CONN-04**: Configurable power reserve per phase, reserved for loads the system does not control
- [x] **CONN-05**: Phase-assignment self-test — energize one known device, observe which phase rose, report a mismatch
- [x] **CONN-06**: Mapping validation before the wizard completes: entities exist, values are plausible, phases sum to the total, refresh matches the declaration

## Breaker thermal model

- [x] **BRK-01**: Configurable overload tiers (multiple of rated current → allowed duration), with defaults derived conservatively from IEC 60898 characteristic B
- [x] **BRK-02**: Per-phase overload budget consumed while above the continuous threshold and recovered while below it (thermal memory)
- [x] **BRK-03**: Tier reached determines reaction speed: ordinary cycle, next cycle, immediate single shed, or full shed
- [x] **BRK-04**: Budget state and remaining recovery time exposed as entities and shown in the UI
- [x] **BRK-05**: Breaker characteristic selectable (B, C, unknown→cautious); unknown forces the continuous threshold to 1.00×

## Dispatch

- [x] **DISP-01**: Device types implemented: `onoff`, `stepped` (N × P), `modulating` (continuous within [min, max]), `storage`
- [x] **DISP-02**: Shed order derived from configured value per kWh, overridable per device
- [x] **DISP-03**: Restore order is the reverse, gated by available headroom and by each device's minimum-off time
- [x] **DISP-04**: Asymmetric action rule — reductions execute immediately, including mid-settling; increases require a closed settling window plus confirmation
- [x] **DISP-05**: Settling window closes when every balance signal has a sample newer than `command time + device reaction time`, with a hard timeout
- [x] **DISP-06**: Model-based estimate (`last measurement + Σ ΔP of commands in flight`) drives decisions while the window is open
- [x] **DISP-07**: Effector verification — measured change compared against the expected ΔP; repeated mismatch marks the device unresponsive and excludes it from dispatch
- [x] **DISP-08**: Configurable limit on simultaneous increases per window (default 1); decreases unlimited
- [x] **DISP-09**: Per-device minimum on-time and off-time honoured, with a deferred-intent reconciliation loop that applies pending changes once a lockout expires
- [x] **DISP-10**: Phase-priority rotation for multi-element loads on different phases, stabilized over N cycles

## Safety

- [x] **SAFE-01**: Safe state is every controlled device off; entered on missing data, stale signals, HA disconnection or add-on shutdown
- [x] **SAFE-02**: Watchdog heartbeat entity so an HA-side automation can shed loads if the add-on dies
- [x] **SAFE-03**: Global kill switch respected within one cycle
- [x] **SAFE-04**: Manual override per device (force on / force off) that outranks dispatch but never outranks protection
- [x] **SAFE-05**: Add-on never commands an entity the user has not explicitly declared as a device actuator
- [x] **SAFE-06**: Onboarding states plainly that this is not a protective device
- [x] **SAFE-07**: State restored after restart; all lockout timers restart from the restart instant, never from persisted expiry

## Modes and operation

- [x] **MODE-01**: `shadow` and `active` modes; `shadow` computes and logs decisions without commanding anything, and is the default after install
- [x] **MODE-02**: Mode switch takes effect without a restart
- [x] **MODE-03**: Every decision records a machine-readable reason code and the inputs that produced it
- [x] **MODE-04**: Decision log queryable in the UI and retained for a configurable period

## Hourly balance (v2.0)

- [x] **BAL-01**: Balance of the running local hour computed from the meter's primary export and import counters (summed-phase), baseline persisted across restarts, unknown when it cannot be trusted
- [x] **BAL-02**: Projection of the balance at the next :00 from free power (grid net export plus own loads added back), with a configurable smoothing window
- [x] **BAL-03**: Target band (floor, ceiling, burn-down ramp) from configuration; no literals
- [x] **BAL-04**: Allowance rule start/stop from the projection, with merit order across devices; guard and envelope remain the gate
- [x] **BAL-05**: Unknown balance or free power means zero allowance (no balance-driven starts; Switchboard's own balance starts are stopped through lockouts)
- [ ] **BAL-06**: Hour boundaries on the injected clock, correct on 23 and 25 hour days and after a clock step
- [x] **BAL-07**: Dashboard shows balance, projection, band and minutes to :00; decision inputs carry them
- [ ] **BAL-08**: Heater cut-over: shadow comparison with the Node-RED flow, then switch-over through `input_boolean.force_cwu`

## Economic dispatch (v2.0, `14-ECONOMICS.md`)

- [x] **ECO-01**: Tariff module: flat and zoned time-of-use price schedule (days, months, hours, midnight wrap, dated holiday list) evaluated on the injected clock's local time, correct on 23- and 25-hour days, with the next zone change; G13 is configuration, never code
- [x] **ECO-02**: `economics:` configuration section (currency label, tariff, settlement, holidays) parsed and validated with config issues, optional and additive (`schema_version` 1, a 0.9 file loads unchanged), preserved by the wizard, no currency symbol in code or defaults
- [x] **ECO-03**: Settlement module interface (D6) with `hourly_netting` (wrapping the BAL rule) and `net_billing` (covers feed-in) shipped; `net_metering` deferred behind the same interface
- [x] **ECO-04**: Effective value per kWh per device: static `value_per_kwh`, or `value_factor` times the import or export price; the static value is the fallback, never a last known value
- [x] **ECO-05**: Dispatch start and stop order follows the live effective value (stability cycles and minimum gap, `shed_priority` pins); the guard's shed order stays static and sees no price; the order never overrides the guard, the envelope or the balance gate
- [x] **ECO-06**: Device `sources` (`pv_only`, `pv_and_zone:<id>`, `any`; omitted keeps 0.9 behaviour) decide which energy class may start a device; surplus tied to the hourly balance (hourly netting) or to free power (net billing); an unusable declared source narrows the device, never widens it; no zone-class start inside `zone_end_margin` of the zone's end
- [x] **ECO-07**: Economic cutoff: a class whose cost (zone price or forgone export) exceeds the device's value plus `min_margin` is not used; with no export price configured the PV test is off and says so
- [x] **ECO-08**: Reason codes list version 6 (`zone_allowed`, `source_not_allowed`, `value_below_cost`, `surplus_available`, `surplus_low`); older versions stay readable; SPA list and en/pl sentences complete
- [x] **ECO-09**: Decision inputs carry an optional `economics` block (zone, prices and basis, effective value, class, sources); absent on old lines, null without an `economics:` section
- [x] **ECO-10**: Dynamic prices arrive from an HA entity mapped as a signal (roles `price_import`, `price_export`, kind `price`, `<currency>/kWh` or `/MWh`, conversion in the signal layer only); a stale or unavailable price falls back to the configured static price with basis `fallback`; the add-on fetches nothing
- [x] **ECO-11**: Savings accounting: per settlement period, value gained minus the extra settlement cost against the devices-off counterfactual; statistics persist time-weighted prices and basis with a schema version bump and read the old file; never back-filled
- [x] **ECO-12**: Savings metric and card and tariff status card live (replacing 'not available, needs a tariff'), currency label from configuration, en and pl, both themes, phone width
- [x] **ECO-13**: Device card shows value per kWh (static and effective now), sources in words and the class open now with its reason (the value and source part of UI-12; goal and history stay with UI-12)
- [x] **ECO-14**: Every economic input failure narrows or falls back to the configured static value, never widens and never guesses; protection is unaffected; each row of the failure table in `14-ECONOMICS.md` has a named test (slice 3 built the rows of the sources, balance, export price, followed price, clock, order and reload; slice 4 the dynamic-price rows (stale, unavailable, unknown, wrong unit, missing fallback, export price unknown); slice 5 the statistics row (an older schema reads with no price data); the ticks rely on those named tests, which slice 5 did not re-audit)
- [x] **ECO-15**: Without an `economics:` section, dispatch decisions and the BAL tests are identical to 0.9 (golden replay)
- [ ] **ECO-16**: Economics logic tested on the virtual clock including the 23- and 25-hour days and G13 boundary minutes; live shadow verification on the maintainer's installation, zones checked against the contract (the virtual-clock tests of the tariff, the dispatch allowance and the DST days are built in slices 1 to 3; the live verification is slice 6, so this stays open)

## Onboarding UI

- [x] **UI-01**: Five-step wizard: meter → connection → sources and storage → loads → mode
- [x] **UI-02**: Device templates (EV charger, water-heater element, heat pump / AC, generic on-off) prefilling type, limits and lockouts
- [x] **UI-03**: Entity pickers fed by HA entity discovery, filtered by domain and device class
- [x] **UI-04**: Live readout beside each mapped entity (value + age) so a wrong pick is visible immediately
- [x] **UI-05**: Wizard refuses to complete on failed validation and says what to fix
- [x] **UI-06**: Configuration persists and is editable later; re-running the wizard never loses existing devices

## Runtime UI

- [x] **UI-07**: Flow view — sources, meter, per-phase busbars, devices; path thickness proportional to power; flow direction animated, honouring `prefers-reduced-motion`
- [x] **UI-08**: Per-phase headroom table: current, headroom to reserve threshold, headroom to breaker, state
- [x] **UI-09**: Device queue with state, power and the reason for the current state
- [x] **UI-10**: Stopped devices remain visible with their blocking reason rather than disappearing
- [x] **UI-11**: Foreign (uncontrolled) load is visually distinct from controlled load throughout
- [x] **UI-12**: Device card: connection, phase, limits, reaction time, value per kWh, goal, and recent command/verification history
- [x] **UI-13**: Full light and dark theming; layout usable at phone width
- [x] **UI-14**: Polish and English translations for all add-on options and UI chrome

## Packaging and distribution

- [x] **PKG-01**: HA add-on: `repository.yaml` + add-on folder with `config.yaml`, options schema, ingress enabled, `homeassistant_api: true`
- [x] **PKG-02**: Multi-arch image (amd64, aarch64) published to GHCR
- [x] **PKG-03**: MQTT discovery publishes runtime entities (per-phase headroom, overload budget, mode, health, per-device state)
- [x] **PKG-04**: Supervisor-provided MQTT credentials; no manual broker configuration in the common case
- [x] **PKG-05**: No exposed network port beyond ingress; no outbound telemetry
- [x] **PKG-06**: Add-on survives HA restarts and MQTT broker restarts without manual intervention

## Verification

- [x] **VER-01**: Control logic unit-tested on a virtual clock: lockouts, hysteresis, change limits, budget consumption and recovery, phase rotation
- [x] **VER-02**: Settling-window scenarios: delayed sample, lost sample, unresponsive effector, foreign load arriving mid-window
- [x] **VER-03**: Every row of the fail-safe table has a test
- [x] **VER-04**: Replay harness — run the dispatcher against recorded history and assert no limit violation
- [ ] **VER-05**: Live verification on the maintainer's 3×25 A installation in shadow mode for at least one week before `active` is recommended

## Planner (v3.0, `15-PLANNER.md`)

- [x] **PLAN-01**: Device `goal:` (energy or soc by a deadline, hard or soft, `sources`, `escalate`, `on_met`) parsed and validated with config issues; optional and additive (a 0.10 file loads unchanged and decides byte for byte); preserved by the wizard
- [x] **PLAN-02**: Goal inputs (SoC, target, deadline, want) as device-scoped signals through the signal layer (roles `device_soc`, `goal_target`, `goal_deadline`, `goal_want`) or literals; a missing, stale or out-of-range input makes the goal inactive and says why, never guessed
- [x] **PLAN-03**: Goal progress: a persisted energy ledger for energy goals (`state.json` schema bump, an old file read), the SoC signal for soc goals; remaining need includes the loss fraction
- [x] **PLAN-04**: Goal source seam (`IGoalSource`): configuration, mapped entities and manual goals merged per device with a stated precedence, several goals per device ordered by deadline; the EV module is a consumer, not part of this requirement
- [x] **PLAN-05**: `planner:` section (`dispatch: off | narrow | full`, default off) and `planner` parameters in `control-defaults.yaml` (`control.planner` overrides, checked), no literal in code
- [x] **PLAN-06**: Forecast signals from Home Assistant entities only: kinds `series`, `energy_forecast`, `timestamp`, roles `pv_forecast`, `pv_forecast_today`, `pv_forecast_tomorrow`, `sun_rising`, `sun_setting`; bounded attribute whitelist; freshness from `last_reported` and `max_age`, coverage kept apart; a stale or unusable forecast means zero PV, never the last known curve; the add-on fetches nothing
- [x] **PLAN-07**: Forward price curve from an HA entity (`curve:` in a dynamic price spec, roles `price_curve_import`, `price_curve_export`), currency label checked, `/MWh` converted in the signal layer only; the uncovered part falls back to the schedule or the spec's fallback with the basis shown
- [x] **PLAN-08**: Load forecast of the uncontrolled house load from statistics-derived hour-of-week bins in `load-profile.json` (seeded from the retained buckets, fed hourly, bounded, corruption set aside), no Recorder access; fallback power when there is no profile
- [x] **PLAN-09**: Slot grid on the injected clock's local time: 23- and 25-hour days, UTC-offset transitions, deadline cuts, slot length from configuration (60, 30 or 15 minutes); price per slot time-weighted
- [x] **PLAN-10**: Pure deterministic planner: earliest-deadline-first merit-order fill over PV and zone classes by price, capped refinement, hard goals not value-tested, soft goals value-tested, escalation and latest-safe-start for hard goals, infeasible goals reported with the shortfall; no solver dependency
- [x] **PLAN-11**: A plan never allocates beyond the guard's static allocation limit per phase and circuit group, outside a device's range, to an undeclared device, to a device without `start: headroom`, or on a class outside `goal.sources` (escalation of a hard goal excepted); a verifier rejects any plan that does, and a property test holds it
- [x] **PLAN-12**: `GET api/plan` read-only (inputs with their bases, slots, per-device entries and goal status, cost, notes, last solve); no new port, no outbound connection
- [x] **PLAN-13**: Plan to dispatch as targets only: ceilings, `Defer`, `Done`, class `plan` and `Urgent` behind every gate, guard and envelope first; mode `off` ignores the plan, `narrow` only narrows, `full` may add the class `plan`; the planner has no path to the effector, the command port or the guard
- [x] **PLAN-14**: A missing, stale, future-dated, wrong-revision or foreign plan, no `planner:` section and `dispatch: off` leave dispatch decisions byte for byte as v2 (golden replay and digest tests)
- [x] **PLAN-15**: Reason codes list version 7 (`plan_deferred`, `goal_deadline_urgent`, `plan_discharge`, `soc_floor`, `soc_ceiling`; `plan_slot_target` and `goal_met` get emitters) with older versions readable, SPA list and en and pl sentences complete; decision inputs carry an optional `plan` block
- [x] **PLAN-16**: Re-planning every 15 minutes and on goal, device-state, forecast, configuration and clock events, rate limited, with adoption hysteresis for the current slot (agreeing cycles and a minimum change) so the plan does not flap
- [x] **PLAN-17**: Every row of the planner failure table in `15-PLANNER.md` has a named test: forecast, curve, profile, goal input, solver failure and timeout, clock step, restart (plan not persisted), safe state, configuration removal (storage row deferred with slice 6)
- [ ] **PLAN-18**: Battery arbitrage as device class `storage`: SoC window, round-trip efficiency, exact DP over SoC and slots, minimum spread, discharge never above the house load unless allowed and never counted as relief, discharge command disabled by default until a real battery has run in shadow
- [x] **PLAN-19**: Day-plan screen and device card goal row (closes UI-12) with the manual "want now" goal, en and pl, both themes, phone width, the estimate-not-guarantee wording, entity IDs in monospace
- [ ] **PLAN-20**: Planner logic tested on the virtual clock including the 23- and 25-hour days, forecast fixtures, determinism and the optimality bound; live shadow verification on the maintainer's installation (plan against dispatch and real PV for a week, one day checked by hand)
