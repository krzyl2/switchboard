# Switchboard

Home Assistant add-on that distributes the available grid connection capacity across controllable high-power loads — EV chargers, water-heater elements, heat pumps, batteries.

Two jobs, in this order:

1. **Keep every phase under its breaker.** Per-phase limits, a breaker thermal model that allows the short excursions the hardware itself allows, and load shedding in a configured order.
2. **Send the remaining capacity where it is worth most.** Each load declares what a kWh is worth to it; the system allocates accordingly.

Status: **Stage 0 (skeleton)** — installs, connects to Home Assistant, publishes `binary_sensor.switchboard_healthy`, serves an empty ingress page. Controls nothing. See `docs/09-ROADMAP.md`.

## Documentation

Read in order:

| Document | Contents |
|---|---|
| [`docs/01-PRODUCT.md`](docs/01-PRODUCT.md) | What this is, scope, decisions, constraints |
| [`docs/02-REQUIREMENTS.md`](docs/02-REQUIREMENTS.md) | Numbered requirements for v1.0 |
| [`docs/03-ARCHITECTURE.md`](docs/03-ARCHITECTURE.md) | Control loops, signal layer, device model, module boundaries |
| [`docs/04-ALGORITHMS.md`](docs/04-ALGORITHMS.md) | Breaker thermal model, shedding, settling window, parameters |
| [`docs/05-UI-SPEC.md`](docs/05-UI-SPEC.md) | Screens, tied to the mockups |
| [`docs/06-DEPLOYMENT.md`](docs/06-DEPLOYMENT.md) | Add-on packaging, build, ingress, MQTT |
| [`docs/07-PITFALLS.md`](docs/07-PITFALLS.md) | Traps that already cost debugging time |
| [`docs/08-LANDSCAPE.md`](docs/08-LANDSCAPE.md) | Existing projects and the actual gap |
| [`docs/09-ROADMAP.md`](docs/09-ROADMAP.md) | Build order |
| [`docs/10-STACK.md`](docs/10-STACK.md) | Technology: .NET 10 (LTS), pinning rules |
| [`docs/11-KICKOFF.md`](docs/11-KICKOFF.md) | Prompt that starts the implementation |
| [`docs/mockups/index.html`](docs/mockups/index.html) | Clickable UI mockups (open in a browser) |

`CLAUDE.md` holds the rules that apply to every change.

## Reference installation

The maintainer's own system, used for live verification: 3 × 25 A connection, 7 kWp PV, three 1.5 kW water-heater elements on separate phases, a single-phase EV charger, a planned 16 kWh battery.

## Not a protective device

This software reduces load on a best-effort basis using data from Home Assistant. It does not replace circuit breakers, EVSE protection or any other electrical safety equipment.

## License

MIT, see [LICENSE](LICENSE).
