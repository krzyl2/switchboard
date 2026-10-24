# Decisions

Small choices the documentation left open, one line each. Larger ones that need the maintainer are listed in the stage report, not here.

## Stage 0

- 2026-10-23 — HA WebSocket client is in-house (`System.Net.WebSockets`), not NetDaemon: protocol is small, removes version coupling, and Stage 1 needs `subscribe_entities` with `last_reported`.
- 2026-10-23 — Outbound HA commands go through an explicit allow-list; Stage 0 allows only `ping` (besides `auth`). There is no raw send path; `call_service` is structurally impossible.
- 2026-10-23 — MQTT publishes go through a topic guard: only `switchboard/…` and `homeassistant/<component>/switchboard/<object_id>/config`. Topics such as `zigbee2mqtt/<x>/set` command real devices.
- 2026-10-23 — Projects: `Switchboard.Application`, `Switchboard.Infrastructure`, `Switchboard.Host` (Web merged into Host until Stage 4). `Switchboard.Domain` is created with its first type in Stage 1 rather than empty now.
- 2026-10-23 — Single test project `tests/Switchboard.Tests`; xunit.v3 in VSTest mode so plain `dotnet test` works.
- 2026-10-23 — Clock is `System.TimeProvider`; tests use `FakeTimeProvider`. `Microsoft.CodeAnalysis.BannedApiAnalyzers` fails the build on `DateTime.Now`, `Thread.Sleep`, `Task.Delay`/`PeriodicTimer`/`WaitAsync`/`CancellationTokenSource` overloads without a `TimeProvider` (tests included).
- 2026-10-23 — Operational parameters live in `src/Switchboard.Host/appsettings.json` under `Switchboard`, bound to options with required properties, `ValidateOnStart` and `ErrorOnUnknownConfiguration` (a misspelt key fails startup).
- 2026-10-23 — Mode resolution is fail-safe: only the exact string `active` yields active; anything else, including a missing or broken `options.json`, is shadow. Stage 0 has no command path, so `active` only logs a warning.
- 2026-10-23 — Standalone path (no `SUPERVISOR_TOKEN`) reads `SWITCHBOARD_HA_URL`, `SWITCHBOARD_HA_TOKEN`, `SWITCHBOARD_MQTT_HOST/PORT/USERNAME/PASSWORD`, `SWITCHBOARD_MODE`, `SWITCHBOARD_LOG_LEVEL`; `SWITCHBOARD_MQTT_PORT` defaults to 1883. Standalone MQTT has no TLS.
- 2026-10-23 — Add-on mode takes MQTT credentials only from the Supervisor (no env fallback), refetched on every connect; Supervisor `ssl: true` is honoured.
- 2026-10-23 — HA connect and handshake receives are bounded by `HomeAssistant:PongTimeout`, so a peer that never sends `auth_required` cannot hold the link in "connecting".
- 2026-10-23 — After `auth_invalid` the client waits `ReconnectMaxDelay` before retrying; the token is never logged.
- 2026-10-23 — Bearer header is also set on the WebSocket upgrade (the Supervisor proxy needs it, per Argus).
- 2026-10-23 — Health = HA WebSocket authenticated with keepalive answered AND MQTT connected. Published at start and every `Heartbeat:Interval`; MQTT reports connected only after availability and discovery are out, so HA never sees state before its config.
- 2026-10-23 — All MQTT publishes and the last will are QoS 1; availability and discovery retained, state not retained.
- 2026-10-23 — `binary_sensor.switchboard_healthy` carries no `device_class`, `entity_category` or `origin`; suggested entity id via `default_entity_id` (accepted by HA 2026.10.0 without warnings, verified in e2e).
- 2026-10-23 — Ingress guard runs first; requests without a remote IP are refused; `X-Ingress-Path` becomes `PathBase` only when it is a single well-formed path. Unknown `/api/*` routes return 404, not the SPA shell.
- 2026-10-23 — Host: `InvariantGlobalization=true`, `UseAppHost=false` (identical publish output for every arch), `IncludeSourceRevisionInInformationalVersion=false`.
- 2026-10-23 — Logging: Microsoft.Extensions.Logging SimpleConsole, single line, timestamped; level from `log_level`. Secret-holding types redact `ToString`.
- 2026-10-23 — Runtime base `ghcr.io/home-assistant/base-debian:trixie-2026.10.0` (Debian 13), not Argus's bookworm: the current HA base line.
- 2026-10-23 — ASP.NET Core runtime 10.0.13 installed with `dotnet-install.sh` (pinned version, unpinned script — it has no versioned URL). It is 10 days old, under the two-week rule, but it is the runtime that ships with SDK 10.0.402 and a security release.
- 2026-10-23 — s6: `S6_CMD_WAIT_FOR_SERVICES=0` (a crash at start otherwise deadlocks the halt), `S6_SERVICES_GRACETIME=8000`, `S6_KILL_GRACETIME=1000` (fits the Supervisor's 10 s stop). `finish` records the exit code, stops respawn and halts the container.
- 2026-10-23 — Image label `io.hass.type="app"` (current Supervisor naming); `io.hass.arch` uses HA arch names (`amd64`, `aarch64`). No `EXPOSE`.
- 2026-10-23 — `switchboard/Dockerfile.dockerignore` is an allow-list so `deploy/dev-data` and tokens never enter the build context.
- 2026-10-23 — GitHub owner `krzyl2` and maintainer line copied from Argus (provisional until the repository exists).
- 2026-10-23 — Store description keeps the docs/06 sentence and appends "Best-effort; not a protective device." — the CLAUDE.md hard rule outranks the verbatim manifest text.
- 2026-10-23 — SPA: Preact 10.x (not 11, its test library lagged the cutoff), Vite 8, TypeScript 7, Vitest 5; lockfile resolved with `npm install --before=2026-10-09` so transitive deps respect the two-week rule too.
- 2026-10-23 — SPA hides last known values on any fetch failure and shows "status unavailable" with the last successful update time; status payload parsed strictly. System fonts only (no web fonts).
- 2026-10-23 — Polish vocabulary follows the mockups: shadow = „Obserwacja", active = „Sterowanie aktywne", health = „Kondycja".
- 2026-10-23 — No light/dark toggle in Stage 0; `prefers-color-scheme` only. The toggle arrives with the runtime UI (Stage 4).
- 2026-10-23 — Placeholder `icon.png`/`logo.png` generated by `deploy/generate-placeholder-art.py` (stdlib only).
- 2026-10-23 — Dev stack: HA 2026.10.0, mosquitto 2.0.22, ports bound to 127.0.0.1 only; e2e passes the HA token through the process environment and never writes `deploy/.env`. The dev HA owner gets a random, unprinted password per run.
- 2026-10-23 — Dedicated buildx builder `switchboard-builder` for multi-arch builds; other builders untouched.
- 2026-10-23 — MQTT keep-alive is explicit: `Switchboard:Mqtt:KeepAlivePeriod` = 15 s (the MQTTnet default the e2e observed), validated `< Heartbeat:ExpireAfter`. It decides how fast a hung process turns the health entity unavailable, so it is not left implicit.
- 2026-10-23 — `Switchboard:Mqtt:ShutdownTimeout` = 4 s bounds the OFF → offline → disconnect sequence; a packaging test keeps `2 × ShutdownTimeout ≤ S6_SERVICES_GRACETIME`. `HostOptions.ShutdownTimeout` stays default (lowering it alone made the process die by SIGABRT).
- 2026-10-23 — MQTT reconnect backoff resets only after availability, discovery, state and subscribe succeed, not on CONNACK. A session that fails while still connected is left with a best-effort retained `offline` and a clean disconnect before retrying.
- 2026-10-23 — `finish` logs SIGTERM at info and any other signal or non-zero exit as fatal before halting.
- 2026-10-24 — MQTT identity scheme approved by the maintainer and frozen as an API: unique_id `switchboard_<object_id>`, discovery `homeassistant/<component>/switchboard/<object_id>/config`, state `switchboard/<object_id>/state`, availability `switchboard/availability`, device `["switchboard"]` (`MqttIdentity`).
- 2026-10-24 — Build-only npm dependencies under MPL-2.0 / CC-BY-4.0 / BlueOak / CC0 (Vite 8 toolchain) accepted by the maintainer; nothing outside MIT/BSD/Apache ships in the image.
- 2026-10-24 — Liveness is a Docker `HEALTHCHECK` (`curl` to `http://127.0.0.1:8099/api/status`, 30 s / 5 s / 3 retries, 30 s start period) instead of `watchdog: tcp://…` (connects even to a frozen process). `startup`, `boot`, `ingress_port` dropped from config.yaml as Supervisor defaults; the add-on linter now passes clean.
- 2026-10-23 — DOCS.md shed-loads example uses a template binary_sensor helper with `delay_on: 60 s` plus a `time_pattern` backstop; a plain state trigger with `for:` restarts its timer on every off ↔ unavailable flap (and the previous example did not load in HA 2026.10).
