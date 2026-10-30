# Build order

Each stage ends with something that runs and can be verified. No stage leaves the system half-wired.

## Stage 0 — Skeleton

Add-on that installs, starts, connects to HA over the Supervisor WebSocket, publishes a health entity over MQTT discovery, and serves an empty ingress page.

**Done when:** the add-on appears in the store from a custom repository, installs, and its health entity goes available in HA.
**Covers:** PKG-01, PKG-02, PKG-03, PKG-04, PKG-05, SAFE-02.

## Stage 1 — Signals

SignalStore with mapping, unit and sign normalization, freshness from `last_reported`, staleness, the balance residual check, and a diagnostics view listing every signal with value and age.

**Done when:** three phase currents are mapped by hand in a config file and the diagnostics page shows correct live values and ages; unplugging a sensor turns it stale within `max_age`.
**Covers:** SIG-01 … SIG-09.

## Stage 2 — Guard

Breaker profile, tier table, per-phase overload budget with recovery, safe state, kill switch, shed ordering, EffectorGate with lockouts. Shadow mode only — decisions are computed and logged, nothing is commanded.

**Done when:** against the dev harness (fake meter driven by input_numbers) every row of the tier table produces the right decision at the right moment, and the replay harness over recorded history reports no missed violation.
**Covers:** BRK-01 … BRK-05, SAFE-01, SAFE-03, SAFE-07, DISP-02, MODE-01, MODE-03, VER-01, VER-03, VER-04.

## Stage 3 — Dispatch

Device types, headroom allocation, hysteresis, settling windows, in-flight estimate, effector verification, deferred intent, phase rotation, circuit groups. Active mode becomes possible.

**Done when:** on the maintainer's installation, in shadow mode for a week, the decision log contains no decision the maintainer disagrees with; then active mode for the heater elements only.
**Covers:** DISP-01 … DISP-10, CONN-03, SAFE-04, SAFE-05, VER-02.

## Stage 4 — Runtime UI

Dashboard, flows, device card, limits, decision log. Real data, full theming, phone width.

**Done when:** every screen in the mockup except the day plan is backed by live data.
**Covers:** UI-07 … UI-14.

## Stage 5 — Onboarding

The five-step wizard, entity discovery, live readouts, validation, phase-assignment self-test, device templates, persistence, editability.

**Done when:** a clean install can be fully configured without touching a file, and a deliberately swapped phase mapping is caught by the self-test.
**Covers:** UI-01 … UI-06, CONN-01, CONN-02, CONN-04, CONN-05, CONN-06, SAFE-06, MODE-02, MODE-04.

Deliberately last among the v1.0 stages: the wizard can only be designed properly once the runtime knows exactly what it needs, and building it early guarantees rework.

## Stage 6 — Public release

Documentation, translations, store description with the "not a protective device" statement, GHCR release, custom-repository install instructions.

**Done when:** a second person installs it from the repository on their own instance and completes setup without asking a question.
**Covers:** PKG-06, VER-05, UI-14.

---

## After v1.0

| Release | Content |
|---|---|
| v2.0 | Value per kWh as the live merit order; tariff module (flat / zoned / dynamic); settlement module (net-billing, net-metering, feed-in); surplus routing; hourly-netting trajectory control (specified in `docs/13-HOURLY-BALANCE.md`, first item, replaces the Node-RED heater flow) |
| v3.0 | LP planner over 24–48 h, PV and load forecasts, deadline goals, battery arbitrage; optional interoperation with an external planner |
| v4.0 | Jinja templates on every field, then sandboxed scripting only if users ask for it |

Economics are not in v1.0 on purpose. The guard is useful, differentiating and verifiable without a single price. Prices add a configuration burden and a whole class of "it charged at the wrong time" support questions, and they should arrive only once the protection layer is trusted.

## Optional module: EV charging

Specified in `docs/12-EV-MODULE.md` (maintainer interview 2026-10-25): PV surplus first, G13 off-peak second, peak only for a trip deadline; trips from the Google calendar `Kursy`; target SoC = trip SoC + 25 % floor + 15 points reserve; Forecast.Solar 2-day forecast; manual mode and "charge now". Builds on the v2.0 tariff and settlement modules and the v3.0 forecast and deadline goals, so it comes after them. Optional, off by default.

### EV module: execution progress (handoff, 2026-10-30)

Execution plan and binding corrections: `docs/EV.md` (wins over `12-EV-MODULE.md`; sections 2a and 3 carry the maintainer amendments). Order S0 -> S1 -> (S2 || S3) -> S5 -> (S6 || S7) -> S8. Working rules: orchestrate subagents, one implementer plus one independent review agent per slice, orchestrator re-verifies (`dotnet build Switchboard.slnx -c Release --no-incremental` 0 warnings, `dotnet test`, `cd web && npm test && npm run build`). Use absolute paths; never leave the shell in `web/`.

| Slice | State | Notes |
|---|---|---|
| S0 hardening and records | done, uncommitted | per-source exception isolation in `GoalEvaluator` (B1) with guard-step test; planner tests for a met stop cap with an unmet trip; records in DECISIONS, 02, 04 (v7), 12 |
| S1 `ev:` configuration | done, uncommitted | `EvSectionParser`, `control.ev` bound not read, `GET api/ev`, `deploy/examples/ev.yaml`; kwh_per_km fallback 0.20; reviewed, fixes applied |
| S2 signals and trips | done, uncommitted, reviewed, fixes applied | calendar-event and distance kinds, roles, `EvInputs` (all unavailable when the feed is not Connected), `TripParser` (first `km:` decides, ambiguous = `KmUnreadable`), edge-date safety |
| S3 energy model | done, uncommitted, reviewed, fixes applied | `KwhPerKmLearner` (pair tolerance `control.ev.learn.pair_tolerance` 60 s), `SocEstimator` (idle re-report refreshes age, sticky charger gap), `TargetSoc`, `ev.json` (own file, 32 KiB cap, newer schema kept as `ev.json.schema<N>`) |
| S5 `EvGoalSource` (core) | done, uncommitted, independently reviewed (35 mutations, 31 killed), fixes applied | `GoalOffer.Failed` + `GoalBoard.Degraded` (Follow skips), `EvComposer` (trip/weekly/cap/floor, incremental needs, unique deadlines, stable revision), `EvEngine` + `EvSnapshotKeeper` (snapshot outside the guard thread, `control.ev.snapshot_max_age`), `EvGoalSource`, assessment in `api/ev`, `cause` in `api/plan`, planner spread (`GoalRequest.Spread`, `QuietShortfall`, `PlanDevice.StepW`, late tie-break), `SocEstimator` `CouldFlow`, `ev.json` `last_full_at`. Not done: synthetic soc `GoalSpec` (`GoalSpec.ModuleOwned` waits for S6 `ManualGoalStore`); full replay digest with an active EV source (harness takes no goal sources; `PlanView` over a real plan covers off/narrow) |
| S6 manual control | done, uncommitted, independently reviewed (25 mutations, 4 survivors covered by new tests), fixes applied | `POST api/ev/mode`, `POST`/`DELETE api/ev/charge-now`, `manual` in `api/ev`, MQTT select/number/button, `MqttTopicGuard` EV topics only with `ev:`, `ManualGoalStore` accepts the EV device (synthetic `ModuleOwned` spec), `EvMode` auto/pv_only/full_charge; contract in EV.md section 11; `ev_off` reserved, not emitted |
| S7 UI | done, uncommitted, reviewed with S6, fixes applied | EV card in `#/plan`, device-card row, controls, unparsed events; en/pl, 375 px both themes looked at on sample data; `EvSpaContractTests` |
| S8 live shadow week | not started | needs maintainer consent per step |

Open items for the next session
- Whole tree verified 2026-10-30 after S5-S7 and review fixes: Release build 0 warnings, dotnet 4009, web 1465, `npm run build` ok. Next: S8 (live shadow week) only with the maintainer's consent per step.
- Live read-only HA discovery done 2026-10-30 (maintainer allowed the token route): SoC `sensor.byd_seal_5_dm_i_poziom_baterii` (reports every few hours while the car sleeps), `sensor.byd_seal_5_dm_i_zasieg` is the TOTAL range (incl. fuel range `..._zasieg_paliwa`), so the electric range needs an HA template sensor (total minus fuel) or a maintainer decision; plugged `binary_sensor.byd_seal_5_dm_i_wtyczka`, go-e `sensor.goe_422752_car_value`; NO `Kursy` calendar in HA (only `calendar.workday_sensor_calendar`); live `ev_charger` already has `start: headroom`, override `force_off`. Open live checks: SoC report rate while charging (drift limit 1.5 kWh may expire the estimate after about 20 min of charging), learner bounds 0.12-0.60 reject a total-range sample until a template sensor exists.
- Known gap for S5: the SoC estimator's charger gap is sticky on ANY stale charger tick, so an idle parked car whose charger entity blips loses its estimate (goals inactive) until the SoC value changes; limit the gap to ticks where energy could flow once S5 supplies charger state.
- Live read-only HA discovery (EV.md S2 list: car SoC and electric-range entities, `Kursy` calendar sample, go-e car-state entity and its state strings, report rates, `last_reported`, zone) and the live `start:` check on `ev_charger` are NOT done; the maintainer was asked to allow the token route (`deploy/ha-token.ps1`, `deploy/ha-supervisor.mjs`, read only) and had not answered.
- Unrelated uncommitted work in the tree (the `NotDelivered` verdict in `Effector/*`, `Settling.cs`, web `DeviceCard.tsx` and `reasons*`): the maintainer said to include it in the same commit. `src/Switchboard.Host/Properties/launchSettings.json` is untracked dev-only, do not commit.
- Deploy only after the whole EV module is done, with the maintainer's fresh explicit approval (`/deploy`); version proposal 0.11.9 or later.
- Maintainer amendments (EV.md 2a): weekly full charge on any weekend day plus manual full for a long trip, full charge ends as late as the window allows, charge spread over long windows (14 A instead of 20 A). Fallback 0.20 kWh/km (90 km on 18 kWh).
