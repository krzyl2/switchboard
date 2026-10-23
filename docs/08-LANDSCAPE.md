# Landscape

Surveyed October 2026, before committing to the build. The conclusion: parts of this exist, the combination does not, and the gap is specifically **per-phase load shedding driven by a breaker model**.

## Closest neighbours

**Load Juggler** (HACS integration) — dynamic load management distributing available power across managed loads, with phase-aware calculations for one-, two- and three-phase installations, per-load phase mapping, a main-breaker current limit and circuit groups for loads sharing a downstream breaker. This is the closest thing to the v1.0 scope. Its focus is EV chargers over OCPP. It has no breaker thermal model and no economic layer.

**EMHASS** — linear-programming optimizer for home energy, with PV and load forecasting, battery and deferrable loads, HiGHS solver, available as an add-on or standalone container, and documented recipes for using evcc as the executor. This is the v3.0 planner, already built and mature. Competing with it on optimization quality would be a mistake.

**SEM (Solar Energy Management)** — large orchestration integration for PV, EV and battery. Notably, a user with a 3×25 A connection asked for per-phase peak handling, and the maintainer answered that SEM has a per-phase *guard* that blocks charging on an over-limit, but does **not** do per-phase peak shaving — shedding loads to hold each phase under a ceiling. That is precisely the gap this project fills, confirmed by the author of the nearest competitor.

**EV-only balancers** — `frenck/ev_load_balancing`, PowerSync, straybiker's blueprint-based balancer, vendor-specific solutions. All limit a single charger against mains load. None distribute across multiple loads, and none model breaker tolerance.

## Where the differentiation is

1. **Per-phase shedding with a thermal budget.** Nobody models the breaker curve. Everyone treats the rating as a hard ceiling, which either wastes headroom or sheds on every kettle.
2. **One system for protection and economics.** The alternative today is EMHASS planning while something else executes, with neither aware of the other's constraints.
3. **Onboarding.** EMHASS is configured through a long YAML document. A wizard with entity discovery, live readouts and a phase-assignment self-test is a genuine differentiator for a public add-on, and cheap relative to its value.

## Where differentiation is not

**Optimization quality.** EMHASS has had a mature LP formulation since 2021. The planner should be deliberately conventional, and interoperation with EMHASS as an alternative planner is worth keeping possible rather than ruling out.

## Positioning consequences

- Ship the guard first. It is the unique part, it needs no prices or forecasts, and it is useful on day one.
- Do not describe the product as an optimizer in the store listing. Describe it as connection-capacity management that also knows what energy is worth.
- Keep the planner interface narrow enough that an external planner could feed it targets later.
