# Switchboard — Phase-Aware Load Dispatch for Home Assistant

> Nazwa robocza. Polska nazwa produktowa używana w UI makiet: „Rozdzielnica Mocy".

## What This Is

A Home Assistant add-on that distributes the available grid connection capacity across high-power controllable loads — EV chargers, water-heater elements, heat pumps, batteries — so that no phase ever exceeds its main breaker rating and the energy that does flow goes to whichever load it is worth the most on. Published publicly for other HA users, not a private setup; users connect their own meter, sources, storage and loads through a configuration UI.

Two concerns, deliberately separated and ordered:

1. **Protection** — keep every phase under its breaker. Fast, deterministic, no forecasts, no prices.
2. **Economics** — among the headroom that protection leaves, give power to the load with the highest value per kWh. Slower loop, uses tariffs and forecasts.

Protection always wins. Economics never overrides it.

## Core Value

A user with a 3×25 A connection can charge an EV, heat water and run a battery at the same time, and the breaker never trips — while every kWh goes to the load where it saves the most money. Setup takes one guided wizard, not a YAML file.

## Current State

**Nothing shipped.** This is a greenfield project. Requirements were elicited over a long design discussion (October 2026); clickable UI mockups exist at `docs/mockups/index.html` and are the reference for screens and vocabulary. The predecessor is a working Node-RED flow (v8.2) controlling three CWU heater elements from PV surplus — its hard-won stability parameters are carried over as defaults (see `07-PITFALLS.md`).

## First release: v1.0 Capacity Guard

**Goal:** Ship the protection layer alone, as a usable product. A user installs the add-on, runs the wizard, points it at their meter and loads, and the system keeps each phase under its breaker by shedding loads in a configured order. No prices, no forecasts, no planner.

This ordering is deliberate. Load management delivers value from the first minute with no economic configuration, and it is the capability no existing HA project covers well (see FEATURES.md). The optimizer is v2.0.

### Target features

- Guided onboarding: main meter entity mapping per phase, phase-assignment self-test, connection parameters, loads from templates, shadow/active mode
- Per-phase current tracking with staleness detection and fail-safe
- Breaker thermal model: configurable overload tiers with a time budget and recovery, not a single hard limit
- Load shedding and restoring in merit order, with relay lockouts and change limits
- Settling window after each of our own actuator commands, with effector verification
- Flow dashboard: sources → meter → per-phase busbars → loads, with live power and per-phase headroom
- Decision log: every decision carries a machine-readable reason
- Shadow mode as the default after installation

### Out of scope for v1.0

Prices, tariffs, forecasts, the LP planner, battery arbitrage, user scripting. Multi-site. Anything that writes to a device not explicitly declared by the user.

## Milestones ahead

| Milestone | Content |
|---|---|
| **v1.0 Capacity Guard** | Protection + dispatch + onboarding + flow view (this milestone) |
| v2.0 Economic Dispatch | Value-per-kWh merit order, tariff module (zoned / flat / dynamic), surplus routing, hourly-netting settlement model |
| v3.0 Planner | LP over a 24–48 h horizon, PV and load forecasts, deadlines (EV SoC by time), battery arbitrage |
| v4.0 Extensibility | Jinja-templated fields everywhere, then sandboxed user scripts if demand appears |

## Decisions

| ID | Decision | Rationale |
|---|---|---|
| **D1** | Rules describe constraints and value; the optimizer decides what runs | User-authored rules that switch devices directly collide with each other and make planning meaningless |
| **D2** | Per-phase limits are first-class; a summed-only limit is a degraded mode | A 3×25 A connection with a single-phase wallbox trips on one phase while total load looks fine. This is the product's main differentiator |
| **D3** | Breaker overload is modelled as a time budget with thermal memory, not a hard ceiling | IEC 60898 guarantees no trip at 1.13×In for an hour and a trip within an hour at 1.45×In. Short, small excursions are allowed by the hardware and must be allowed by the software, or every kettle causes a pointless shed |
| **D4** | Three control loops: Guard (fastest available sample), Dispatch (10–30 s), Planner (15 min) | Protection cannot wait for a settling window or a solver |
| **D5** | Core never knows entity IDs; everything goes through a signal mapping layer | The add-on is for other people's installations. SolarEdge/Tauron specifics live in configuration |
| **D6** | Settlement rules (hourly netting, net-billing, feed-in, dynamic prices) are a pluggable module | Hourly netting is a Polish Tauron specific. Hard-coding it blocks every other market |
| **D7** | Shadow mode is the post-install default | Software that switches 5 kW loads in strangers' houses must prove itself before it acts |
| **D8** | Safe state = everything controlled is off | No missing signal, stale reading or crash may ever result in a load being energized |
| **D9** | No user scripting in v1 | Sandboxed script execution in an add-on holding a Supervisor token is an attack surface with no demand behind it yet. Jinja templates evaluated by HA cover most of the need |
| **D10** | Ingress only; no exposed port, no outbound telemetry | Same reasoning as D9 |
| **D11** | UI chrome in English; the add-on ships Polish and English translations | Public distribution. Mockups are in Polish because the first user is Polish |
| **D12** | Deployment mirrors Argus: add-on folder + `repository.yaml`, multi-arch GHCR image, ingress SPA, MQTT discovery for published entities | Proven, already operated by the same maintainer |

## Constraints

- **Not a protective device.** The add-on reduces load on a best-effort basis from Home Assistant data. It does not replace breakers or EVSE protection, and the UI says so during onboarding.
- **Targets HA OS / Supervised** (add-ons need the Supervisor). A standalone Docker path with manual configuration stays available for HA Container/Core users.
- **Licenses:** permissive only (MIT/BSD/Apache).
- **No cloud, no multi-tenancy, no phone-home.**

## Reference material in this repo

| Path | What it is |
|---|---|
| `docs/02-REQUIREMENTS.md` | Numbered requirements for v1.0 |
| `docs/03-ARCHITECTURE.md` | Loops, signal layer, device model, module boundaries |
| `docs/04-ALGORITHMS.md` | Breaker thermal model, shedding, settling window — the maths |
| `docs/05-UI-SPEC.md` | Screen-by-screen breakdown tied to requirement IDs |
| `docs/06-DEPLOYMENT.md` | Add-on packaging, image build, ingress, MQTT |
| `docs/07-PITFALLS.md` | Known traps from the Node-RED predecessor and from the HA platform |
| `docs/08-LANDSCAPE.md` | Existing projects and what differentiates this one |
| `docs/09-ROADMAP.md` | Build order |
| `docs/mockups/index.html` | Clickable UI mockups — seven screens, Polish, mock data. Vocabulary and screen structure are normative; pixel fidelity is not |
