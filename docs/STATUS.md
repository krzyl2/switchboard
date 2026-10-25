# Implementation status

Single entry point for a new session. Updated 2026-10-25 at version 0.7.1 (git `main`). Read this, then `CLAUDE.md`, then `docs/DECISIONS.md` (binding, newest sections last).

## Where we are

| Item | State |
|---|---|
| Released version | 0.7.1, image `ghcr.io/krzyl2/switchboard:0.7.1` (public), repo `github.com/krzyl2/switchboard` (public, MIT) |
| Live install | Maintainer's HA `http://192.168.10.21:55285`, add-on `fb9ed8c0_switchboard`, **option `mode: shadow`, control switch OFF** |
| Tests | .NET 2007, web 1027, LiveReplay 22 (needs the gitignored recording), all green; build 0 warnings |
| Requirements ticked | 63 of 66 in `docs/02-REQUIREMENTS.md` |

## Stages

| Stage | Version | State |
|---|---|---|
| 0 Skeleton (add-on, health, packaging) | 0.1 | done, live |
| 1 Signals (mapping layer, freshness, diagnostics) | 0.2 | done, live |
| 2 Guard (breaker model, tier table, safe state, kill switch) | 0.3 | done, live |
| 3 Dispatch (merit order, settling, verification, circuit groups) | 0.4–0.5 | done, live (shadow) |
| 4 Runtime UI (dashboard, flows, device card, limits, decision log) | 0.5 | done, live |
| 5 Onboarding wizard (5 steps, passive phase test, backups) | 0.6 | done, live |
| Control (inserted before 6): global switch, command path, `start:`, active phase test | 0.7.0 | done; live with control off |
| 0.7.1: select as stop actuator (go-e force state), cleanups | 0.7.1 | done; live, see "Live file" below |
| 6 Public release (docs, store text, second-person install) | – | not started |
| v2.0 tariffs, settlement, surplus routing, value per kWh | – | not started |
| v3.0 planner, forecasts, deadline goals | – | not started |
| EV module (`docs/12-EV-MODULE.md`) | – | spec only |

## Open requirements

- **UI-12** device card: value per kWh, goal and command history (value/goal wait for v2/v3).
- **PKG-06** survive HA and MQTT broker restarts. HA Core restart and Modbus outage verified live (2026-10-25, control off). Mosquitto restart not tried.
- **VER-05** one week of shadow observation. Running since 2026-10-24; the UI shows the day count (advice only).

## Next steps, in order

1. **Phase 2: first supervised enable** (only when the maintainer says yes and is at home). Plan in the Control section of `DECISIONS.md` and the 0.7.0 workflow result; short form: set add-on option `mode: active`, open Status, flip "Sterowanie dozwolone", watch decisions with `commanded: true` (first effect: go-e setpoint lowered to fit the L1 reserve, accepted by the maintainer), then kill switch on/off, then the active phase test per heater (expect L2 for `switch.grzalka_1`, L1 for `_2`, L3 for `_3`, the last one first real proof), then control off (nothing sent, devices stay). Abort and switch control off on any call to an undeclared entity, any "not a declared actuator" refusal, unconfirmed device, or pending count stuck above 0.
2. Mosquitto restart check (PKG-06), with control off.
3. Finish the shadow week (VER-05), then decide on leaving control on.
4. Stage 6 public release.
5. v2.0 (tariffs G13 etc., net-billing, net-metering, RCE average; surplus routing; value per kWh), then v3.0, then the EV module. Maintainer's device-wants model (devices report "want to charge", power and energy-by-deadline; car and storage before the water heater; value per kWh follows prices and the fuel saved) is in `DECISIONS.md` and `12-EV-MODULE.md`.

## Open questions for the maintainer

- Send-time refusal of commands to Switchboard's own MQTT entities (today only a warning).
- Reason codes v4 proposals: `mode_active`, `command_failed`, `command_not_sent` (nearest existing codes used).
- Editing a stop actuator with control on can stop a running charger for one guard step: edit with control off.
- Licence is MIT (decided); recording `tests/fixtures/replay/live-*.csv.gz` stays private (gitignored).

## Standing rules (from the maintainer)

- Only GitHub account `krzyl2`: push with `GH_TOKEN="$(gh auth token --user krzyl2)" git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push`. Never switch the globally active gh account.
- Never start a new Home Assistant instance; the live HA is the test bed.
- Never enable control, change `mode`, or touch `switch.grzalka_*` / `number.goe_*` / `select.goe_*` on the live HA without explicit consent. The add-on's own `switch.switchboard_kill_switch` may be tested (records only while control is off).
- Do not change the maintainer's HA settings; write only under `/addon_configs/fb9ed8c0_switchboard/`.
- Hard rules in `CLAUDE.md` stand: shadow default, no literal control parameters, no derived sensor as input, no Recorder access, no port beyond ingress, not a protective device.
- Private Rule 6 (token budget) is waived for this project; mention a breach once in the final report.
- Replies in Polish with diacritics, terse.

## Working method (what worked)

- One workflow script per stage (`design → red-team → implement (sequential + parallel parts) → 3-lens review → verify → fix`), then the orchestrator re-verifies: `dotnet build Switchboard.slnx -c Release --no-incremental` (0 warnings), `dotnet test`, `cd web && npm ci && npm test && npm run build`, grep that `call_service` lives only in the command port and allow-list (8 pairs) and only `EffectorGate` submits.
- Release loop: bump versions (`switchboard/config.yaml`, `Directory.Build.props`, `WebSurfaceTests`), CHANGELOG, commit and push, `BUILDX_BUILDER=switchboard-builder pwsh deploy/build-push.ps1 -Version x.y.z`, then in the HA browser page `store/reload` and `addons/fb9ed8c0_switchboard/update` via `hass.callWS({type:'supervisor/api', ...})`. Logs: `hass.callApiRaw('GET','hassio/addons/fb9ed8c0_switchboard/logs')`.
- Writing the live `switchboard.yaml`: code-server terminal (`a0d7b954_vscode`), Cancel the workspace-trust dialog, write via temp file + `mv`, check sha256, `exit`. Never type while the editor has focus.
- Decisions go one line each, dated, into `docs/DECISIONS.md`.

## Live file

`/addon_configs/fb9ed8c0_switchboard/switchboard.yaml` follows `deploy/examples/switchboard.yaml`. Check the first line of the 0.7.1 section of `DECISIONS.md` for whether the go-e select stop (`stop_actuator: select.goe_422752_frc`, options `"1"` / `"0"`) has been applied; the maintainer approved it on 2026-10-25.
