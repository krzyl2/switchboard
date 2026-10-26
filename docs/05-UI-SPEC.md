# UI specification

Reference: `docs/mockups/index.html` — a clickable, static mockup with seven screens and mock data. It is normative for **structure, vocabulary and information hierarchy**, not for pixels. Mockup text is Polish because the first user is Polish; shipped chrome is English with Polish and English translations (D11).

Design tokens in the mockup (colour, type, spacing) are a starting point, not a design system. If the add-on grows one, it belongs in its own folder like Argus's.

## Screen map

| # | Screen | Purpose | Requirements |
|---|---|---|---|
| 01 | Pulpit / Dashboard | What is happening right now and why | UI-08, UI-09, UI-10, UI-11 |
| 02 | Przepływy / Flows | Where the power comes from and goes | UI-07, UI-11 |
| 03 | Konfiguracja wstępna / Setup | First-run wizard | UI-01 … UI-06 |
| 04 | Karta urządzenia / Device | Per-device configuration and verification history | UI-12, DISP-07 |
| 05 | Limity i przeciążenia / Limits | Breaker profile, budget, shed order | BRK-01 … BRK-05, DISP-02 |
| 06 | Plan doby / Day plan | Intent over the next 24 h | v3.0 — mockup only |
| 07 | Dziennik decyzji / Decision log | Audit trail, signal health | MODE-03, MODE-04, SIG-05 |

## 01 — Dashboard

Three blocks, in this order:

1. **Phase bars.** One row per phase: segmented bar showing base load / controlled load / foreign load, a dashed marker at the reserve threshold and a solid marker at the breaker. Current in amperes and percentage of limit on the right.
2. **Power balance.** Production, house load, controlled load, storage, grid, hourly balance. These must sum correctly — in the mockup they do, and a mismatch is a bug worth catching in tests.
3. **Allocation queue.** Devices ordered as dispatch ranked them, each with state badge, power, phase and a one-line reason.

A banner appears above the queue whenever a phase is over its reserve threshold, naming the foreign load and the planned action with its timing.

Rules that are easy to get wrong:

- Stopped devices stay in the list with their blocking reason. Disappearing rows make the user think the configuration broke.
- Controlled and foreign load never share a colour.
- The reserve threshold is drawn, not only stated.
- With an `economics:` section the dashboard also shows a Tariff card (zone, import and export price, basis, next change) and a Savings card (an estimate against the controlled loads being off, never a bill). Amounts carry the configured currency label; without the section both are absent (`14-ECONOMICS.md`, "As built (slice 6 UI)").

## 02 — Flows

A single schematic: sources on the left (PV, grid, storage) → main meter → junction → three phase busbars → devices grouped by phase.

- Stroke width proportional to power; one scale for the whole drawing.
- Flow direction animated, suppressed under `prefers-reduced-motion`.
- Device boxes carry name, sub-line (state or constraint) and power.
- A stopped device is drawn with a dashed border and zero power, not removed.
- Below the schematic: per-phase headroom table (to the reserve, to the breaker) and a "where PV went in the last hour" breakdown.

The schematic must make one thing obvious without reading any text: **the three phases are independent**. This is the single idea the product is built on, and it is also the thing users get wrong when they expect load to move from a busy phase to an idle one.

Mobile: horizontal scroll inside the diagram container only; the page never scrolls sideways.

## 03 — Setup wizard

Five steps. Step 1 is the one that cannot be simplified.

**Step 1 — Main meter.** Device picker, then explicit per-phase entity mapping (L1, L2, L3), each row showing the live value and its age. Then sign convention, direction entity (required when current is unsigned), detected refresh interval, and optional per-phase active power.

Before continuing, validation runs: entities exist, values plausible, phases sum to the total, refresh matches declaration. A "test phase assignment" action energizes one known device and reports which phase rose. Swapped L2/L3 is the single most dangerous misconfiguration in the product — it makes the system shed load on the phase that is not overloaded — so this test is prominent, not hidden in diagnostics.

**Step 2 — Connection.** Topology (three-phase per-phase limits / three-phase summed / single-phase), breaker rating, characteristic, voltage, reserve per phase. The copy explains the reserve in terms of the user's own appliances, and states the kettle arithmetic from `04-ALGORITHMS.md` so the number is not a guess.

**Step 3 — Sources and storage.** Inverter, battery capacity, charge power, SoC limits.

**Step 4 — Loads.** Template cards (EV charger, heater element, heat pump / AC, generic on-off), each prefilling type, limits and lockouts. Phase assignment and circuit group per device.

**Step 5 — Mode.** Shadow (recommended, default) or active, plus the "this is not a protective device" statement. The wizard cannot be completed without reading this step.

## 04 — Device card

Two columns: connection (entity, phase, min/max, reaction time) and economics (value per kWh with its derivation, goal, allowed sources). With an `economics:` section the economics column shows the static value and the value now, the sources in words and what is allowed now (the goal row stays "not available" until v3.0). Below, the command history table: time, command, expected Δ, measured Δ, verdict.

That table is the main diagnostic for "why isn't my car charging", and it is also where effector verification becomes visible to the user rather than being an internal mechanism.

## 05 — Limits and overloads

- Tier table for the configured breaker, editable, with the amperes and kilowatts computed for this installation rather than shown abstractly.
- Per-phase overload budget bars with recovery time.
- A real recorded excursion rendered as a small chart: current over time, both thresholds marked, the shed moment annotated.
- Shed order table with each device's value per kWh and shed speed.

The copy must say plainly that the durations are more conservative than the standard, and why.

## 06 — Day plan

Mockup only in v1.0. Shows PV forecast, tariff zones as a band, and per-device intent bars, plus planned versus unmanaged cost. Keep the screen in the mockup so the navigation and data model anticipate it, but do not build it before the planner exists.

## 07 — Decision log

Table of decisions (time, device, change, reason) plus two side panels: signal health (name, age, state) and a daily summary (self-consumption, energy per device, number of sheds, breaker trips, savings).

"Breaker trips: 0" is the product's primary success metric and belongs on this screen permanently.

## Cross-cutting

- Full light and dark theming; the user's HA theme is not readable from ingress, so follow `prefers-color-scheme` and offer an explicit toggle.
- Usable at 400 px width.
- No emoji as status; a badge or a dot with a text label.
- Entity IDs always monospace.
- Every number that can be stale shows its age somewhere, or the user cannot tell a frozen UI from a calm system.
