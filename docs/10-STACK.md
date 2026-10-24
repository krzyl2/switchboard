# Technology choice

**Decided: .NET 10 (LTS).** See the Decision section at the bottom. The rest of this file is the reasoning and the rules that follow from it.

## Candidates

### A. .NET 10 add-on (chosen)

Clean Architecture, HA WebSocket client, MQTT publisher, ingress API, SPA in the `web/` folder. The planner later uses an LP solver callable from .NET (HiGHS via OR-Tools or a native wrapper).

**For:** the maintainer's primary stack, so the control logic — which is where the real complexity sits — gets written quickly and stays maintainable for years. Strong testing story for a time-driven state machine. Argus already proves the add-on packaging works with .NET.
**Against:** few community examples for HA add-ons in .NET; the HA WebSocket client is a dependency to pin carefully. Argus pins `NetDaemon.Client` 23.46.0 because it targets .NET 8; on .NET 10 the current NetDaemon line applies instead — check what it targets before pinning, and be prepared to talk to the HA WebSocket API directly if the client lags the runtime. The WebSocket protocol is small and stable; a thin in-house client is an acceptable fallback and removes a version-coupling risk for a long-lived add-on.

### B. Python add-on

**For:** the native ecosystem for both HA and optimization; the largest body of examples; trivial LP integration later.
**Against:** not the maintainer's primary language, and this project's difficulty is domain logic and state management rather than data science.

### C. HACS integration (rejected)

Runs inside the HA process. A solver or a stuck control loop takes Home Assistant down with it, and HA's internal APIs shift every few releases. Not acceptable for software that holds 5 kW loads.

## Frontend

Ingress SPA. Argus moved to Preact with a hand-written CSS token system and dark mode; the same choice fits here and keeps the bundle small. The mockup (`docs/mockups/index.html`) is plain HTML and CSS — port its structure and vocabulary, do not paste its markup.

## Pinning rules

Pin every dependency to an exact version. Prefer versions at least two weeks old. Record the chosen versions and their release dates here when the decision is made, as Argus does in its own `CLAUDE.md` — that table turned out to be the single most useful thing in that repository when a dependency broke.

## What not to use

| What | Why |
|---|---|
| A HACS integration for the control loop | Takes HA down with it; see above |
| Direct Recorder database access | Unsupported and version-fragile; use WebSocket plus the history API |
| An embedded script interpreter in v1 | The add-on holds a Supervisor token; Jinja evaluated by HA covers the need |
| GPL-licensed components | Permissive licenses only |
| A derived "house load" sensor as a control input | See `07-PITFALLS.md` |

---

## Decision

**.NET 10 (LTS), decided 2026-10-23.**

.NET 10 went GA on 11 November 2025 and is supported until November 2028. .NET 11 is expected around November 2026 and is preview-only at the time of this decision, so it is not a base for a product other people will install. Target `net10.0`; revisit after .NET 11 has been GA for at least one release cycle, and only if something in it is actually needed.

The deciding argument is not performance — this workload is trivial — but that the hard part of this project is a time-driven state machine with lockouts, budgets and verification, and it must stay correct and maintainable for years in the maintainer's own hands.

### Pinning table

Fill this in during Stage 0, with the version and its release date, as Argus does. Pin exact versions, prefer anything at least two weeks old.

Filled 2026-10-23 (Stage 0). Two-week cutoff: 2026-10-09.

| Package | Version | Released | Why this one |
|---|---|---|---|
| HA WebSocket client | in-house | — | `System.Net.WebSockets`; protocol is small, no version coupling, Stage 1 needs `subscribe_entities` with `last_reported` |
| .NET SDK (`global.json`, `rollForward: latestPatch`) | 10.0.402 | 2026-10-13 | Installed SDK; ships ASP.NET Core 10.0.13 |
| ASP.NET Core runtime (image, `dotnet-install.sh`) | 10.0.13 | 2026-10-13 | Matches the SDK; security release — under the two-week rule, deliberately |
| MQTTnet | 5.2.0.1603 | 2026-07-01 | Latest 5.x past the cutoff; has a net10.0 target. MIT |
| Microsoft.Extensions.Options | 10.0.12 | 2026-09-08 | Options validation in Application. MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | 2026-09-08 | `ILogger` in Application without ASP.NET Core. MIT |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 5.6.0 | 2026-07-02 | Fails the build on wall-clock APIs. MIT |
| xunit.v3.mtp-off | 4.0.1 | 2026-09-12 | Test framework, VSTest mode; 4.0.2 is under the cutoff. Apache-2.0 |
| xunit.runner.visualstudio | 4.0.0 | 2026-08-15 | VSTest adapter for xunit.v3 4.0.x. Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.10.1 | 2026-09-15 | VSTest host. MIT |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | 2026-09-09 | `FakeTimeProvider` — the virtual clock. MIT |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | 2026-09-08 | In-memory host tests. MIT |
| YamlDotNet | 18.1.0 | 2026-06-26 | Runtime since Stage 1: configuration document parser (Infrastructure); packaging tests. MIT |
| preact | 10.29.8 | 2026-08-01 | The only package shipped in the SPA bundle. MIT |
| vite | 8.3.4 | 2026-10-08 | Bundler. MIT |
| @preact/preset-vite | 2.10.6 | 2026-07-18 | Preact JSX for Vite. MIT |
| @babel/core | 7.29.7 | 2026-05-25 | Required peer of the preset. MIT |
| typescript | 7.0.2 | 2026-07-08 | Type check. Apache-2.0 |
| vitest | 5.0.3 | 2026-09-30 | SPA tests. MIT |
| @testing-library/preact | 3.2.4 | 2024-05-27 | Component tests; 3.2.5+ is under the cutoff. MIT |
| jsdom | 30.1.2 | 2026-10-04 | Test DOM. MIT |
| ghcr.io/home-assistant/base-debian | trixie-2026.10.0 | 2026-10-08 | Runtime base (Debian 13, s6-overlay 3.2.3.2, bashio). Apache-2.0 |
| mcr.microsoft.com/dotnet/sdk | 10.0.402 | 2026-10-13 | Build stage; same SDK as `global.json` |
| node | 24.21.0-alpine3.24 | 2026-09-07 | SPA build stage; Node 24 LTS |
| ghcr.io/home-assistant/home-assistant | 2026.10.0 | 2026-10-07 | Dev stack / e2e only |
| eclipse-mosquitto | 2.0.22 | 2025-07-11 | Dev stack / e2e only. The tag is rebuilt upstream; pin by digest if byte-identical rebuilds matter |

Build-only npm transitive dependencies outside MIT/BSD/Apache (never shipped in `wwwroot`): `lightningcss` (MPL-2.0, hard dependency of Vite 8), `caniuse-lite` (CC-BY-4.0), `lru-cache` (BlueOak-1.0.0), `mdn-data` (CC0-1.0). Accepted by the maintainer 2026-10-24 for build-time use only.
