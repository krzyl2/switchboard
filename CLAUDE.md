# Switchboard

Home Assistant add-on distributing grid connection capacity across controllable high-power loads. Keeps every phase under its breaker first, then sends the remaining headroom to whichever load is worth the most per kWh.

Start with `docs/STATUS.md` (what is built, what is next, standing rules). Read `docs/01-PRODUCT.md` before changing anything structural. `docs/04-ALGORITHMS.md` is the control specification; `docs/07-PITFALLS.md` lists traps that already cost debugging time on the predecessor system.

## Hard rules

- **Protection outranks everything.** No optimization, user preference or configuration may raise a load above a phase limit. Safe state is every controlled device off, entered on stale data, lost connection or shutdown.
- **Never command an entity the user has not declared as an actuator.** Discovery may suggest; only the user declares.
- **The core never knows an entity ID.** Everything goes through the signal mapping layer. SolarEdge, Tauron and go-e specifics belong in configuration or test fixtures, never in domain code.
- **Shadow mode is the default.** A fresh install computes and logs, it does not switch.
- **Every decision carries a reason code** from the closed list in `04-ALGORITHMS.md`. A decision without a reason is a bug.
- **No literals for control parameters.** Hysteresis, lockouts, tier durations and budgets come from configuration.
- **No exposed port beyond ingress, no outbound telemetry.**
- **This is not a protective device** and no copy anywhere may suggest it is.

## Conventions

- Energy kWh, power W, current A. Unit conversion happens only in the signal layer.
- `DateTimeOffset` everywhere; local time only at the presentation edge. Expect 23- and 25-hour days.
- Clock is injected; no `DateTime.Now` in domain or application code. Control logic is tested on a virtual clock.
- Freshness from HA `last_reported`, never `last_changed`.
- Code and identifiers in English. UI chrome in English with `en` and `pl` translations. Mockups are in Polish.
- Entity IDs render in monospace in the UI.
- Permissive licenses only (MIT / BSD / Apache).

## Technology

**.NET 10 (LTS), target `net10.0`.** Decided 2026-10-23; reasoning and pinning rules in `docs/10-STACK.md`. .NET 11 is preview-only and is not a base for software other people install.

Ingress SPA in `web/`, built separately and copied into the image. Pin every dependency to an exact version and record it in the pinning table in `docs/10-STACK.md`.

## Searching this repo

Do not run a recursive search from the repo root. Name the source directories: `src/ web/ switchboard/ deploy/ tests/ docs/`. Never search `bin/`, `obj/`, `node_modules/`, `.git/`.

## Build order

`docs/09-ROADMAP.md`. Stages are sequential and each one ends with something installable and verifiable. Do not start the onboarding wizard before the runtime exists — it will be rewritten.
