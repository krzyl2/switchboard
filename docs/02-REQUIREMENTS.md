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

- [ ] **BAL-01**: Balance of the running local hour computed from the meter's primary export and import counters (summed-phase), baseline persisted across restarts, unknown when it cannot be trusted
- [ ] **BAL-02**: Projection of the balance at the next :00 from free power (grid net export plus own loads added back), with a configurable smoothing window
- [ ] **BAL-03**: Target band (floor, ceiling, burn-down ramp) from configuration; no literals
- [ ] **BAL-04**: Allowance rule start/stop from the projection, with merit order across devices; guard and envelope remain the gate
- [ ] **BAL-05**: Unknown balance or free power means zero allowance (no balance-driven starts; Switchboard's own balance starts are stopped through lockouts)
- [ ] **BAL-06**: Hour boundaries on the injected clock, correct on 23 and 25 hour days and after a clock step
- [ ] **BAL-07**: Dashboard shows balance, projection, band and minutes to :00; decision inputs carry them
- [ ] **BAL-08**: Heater cut-over: shadow comparison with the Node-RED flow, then switch-over through `input_boolean.force_cwu`

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
- [ ] **UI-12**: Device card: connection, phase, limits, reaction time, value per kWh, goal, and recent command/verification history
- [x] **UI-13**: Full light and dark theming; layout usable at phone width
- [x] **UI-14**: Polish and English translations for all add-on options and UI chrome

## Packaging and distribution

- [x] **PKG-01**: HA add-on: `repository.yaml` + add-on folder with `config.yaml`, options schema, ingress enabled, `homeassistant_api: true`
- [x] **PKG-02**: Multi-arch image (amd64, aarch64) published to GHCR
- [x] **PKG-03**: MQTT discovery publishes runtime entities (per-phase headroom, overload budget, mode, health, per-device state)
- [x] **PKG-04**: Supervisor-provided MQTT credentials; no manual broker configuration in the common case
- [x] **PKG-05**: No exposed network port beyond ingress; no outbound telemetry
- [ ] **PKG-06**: Add-on survives HA restarts and MQTT broker restarts without manual intervention

## Verification

- [x] **VER-01**: Control logic unit-tested on a virtual clock: lockouts, hysteresis, change limits, budget consumption and recovery, phase rotation
- [x] **VER-02**: Settling-window scenarios: delayed sample, lost sample, unresponsive effector, foreign load arriving mid-window
- [x] **VER-03**: Every row of the fail-safe table has a test
- [x] **VER-04**: Replay harness — run the dispatcher against recorded history and assert no limit violation
- [ ] **VER-05**: Live verification on the maintainer's 3×25 A installation in shadow mode for at least one week before `active` is recommended
