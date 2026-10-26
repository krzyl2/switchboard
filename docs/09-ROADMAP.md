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
