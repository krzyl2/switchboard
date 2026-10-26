# Implementation status

Single entry point for a new session. Updated 2026-10-26 at version 0.10.0 (git `main`). Read this, then `CLAUDE.md`, then `docs/DECISIONS.md` (binding, newest sections last).

## Where we are

| Item | State |
|---|---|
| Released version | 0.10.0, image `ghcr.io/krzyl2/switchboard:0.10.0` (public), repo `github.com/krzyl2/switchboard` (public, MIT) |
| Live install | Maintainer's HA `http://192.168.10.21:55285`, add-on `fb9ed8c0_switchboard`, **option `mode: shadow`, control switch OFF** (phase 2 done 2026-10-26, then back to shadow) |
| Tests | .NET 2634 (2026-10-26, with slices 1-5 and the command path in the working tree; the flaky test is fixed there but not yet confirmed by repeated runs), web 1099, LiveReplay 22 (needs the gitignored recording), all green; build 0 warnings. Former flaky test `ControlEndpointTests.Control_is_allowed_with_a_json_post_and_written_to_state_json_at_once` (about 1 full run in 3, also before 0.8) now waits for the coordinator's `Saved` event instead of polling the file; confirm with repeated runs |
| Requirements ticked | 83 of 90 in `docs/02-REQUIREMENTS.md` (counted 2026-10-26 after ECO-11 and ECO-14) (BAL-01..05 and BAL-07 done in code, BAL-06 covered by the tracker tests but not ticked, BAL-08 open) |

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
| 0.7.1: select as stop actuator (go-e force state), cleanups | 0.7.1 | done; live, go-e select stop applied to the live file (20 signals) |
| 6 Public release (docs, store text, second-person install) | – | not started |
| v2.0 Economic Dispatch (tariffs, settlement, surplus routing, value per kWh; `docs/14-ECONOMICS.md`, ECO-01..16) | 0.10–2.0 | slices 1-6 UI built, unreleased, no version bump (Domain/Economics: tariff, settlement models, `PriceBook`, effective value, `SourceSet`; `economics:` config, device keys `value_follows`, `value_factor`, `sources` and parameters, read-only `GET api/economics`; slice 3 in dispatch, shadow only: the allowance gate for devices that declare `sources`, the live value order, reason codes v6, the `economics` decision-input block; ECO-01 to ECO-10 and ECO-15 ticked, a file without `economics:` decides byte for byte as 0.9; slice 4: dynamic prices from an HA entity, signal kind `price`, roles `price_import` / `price_export`, `signal:` + `fallback` + `add` price specs, `PriceInputs`, `price_basis` live / fallback, `tariff_fallback` log line; slice 5: savings accounting, `statistics.json` schema 2 with the time-weighted prices and basis per bucket and the value per device, the device-off counterfactual per settlement period (local hour or bucket), `savings` in `GET api/economics`, prices in `GET api/statistics`; ECO-11 and ECO-14 ticked); slice 6 (UI and live verification) not started |
| v3.0 planner, forecasts, deadline goals | – | not started |
| EV module (`docs/12-EV-MODULE.md`) | – | spec only |
| Hourly balance (`docs/13-HOURLY-BALANCE.md`, BAL-01..08; replaces the Node-RED heater flow) | 0.9.x | BAL-01 done and live (measurement only, `GET api/balance`); BAL-02..05 released in 0.9.0, tested on synthetic data and live in shadow only (projection, band, allowance gate on `start: headroom` devices, reason codes v4); BAL-07 released in 0.9.0 (api/balance with projection and band, decision inputs, dashboard card); BAL-08 cut-over open |

## Open requirements

- **UI-12** device card: value per kWh, goal and command history (value/goal wait for v2/v3).
- **PKG-06** survive HA and MQTT broker restarts. HA Core restart and Modbus outage verified live (2026-10-25, control off). Mosquitto add-on restart verified live (2026-10-26 14:45 local, shadow, control off): the link dropped, retried at 1, 2 and 4 s (Supervisor MQTT service answered HTTP 400 while the broker was down), reconnected at 14:45:08 and republished discovery when HA came online; no entity stayed unavailable, `api/status` mqtt connected.
- **VER-05** one week of shadow observation. Running since 2026-10-24; the UI shows the day count (advice only).

## Next steps, in order

0. **Phase 2 done 2026-10-26** (control on 09:14-09:45Z, now off; option still `active`, maintainer to decide on `shadow`): kill switch, go-e stop and restore, active phase test (grzalka_2 = L1 and grzalka_3 = L3 confirmed, grzalka_1 = L2 inconclusive but clean on). The heaters are still driven by the Node-RED flow; Switchboard replaces it via `input_boolean.force_cwu` once the hourly balance module (`docs/13-HOURLY-BALANCE.md`, BAL-01..08) exists and has run in shadow beside it. BAL-02..05 and BAL-07 shipped in 0.9.0 (the live balance matched `sensor.bilans_godzinowy_tauron` to 0 Wh at 14:44 and 15:52 local on 2026-10-26). Next: watch the gate in shadow on live data, then BAL-08 (comparison with the Node-RED flow, cut-over).
1. **Phase 2: first supervised enable** (only when the maintainer says yes and is at home). Plan in the Control section of `DECISIONS.md` and the 0.7.0 workflow result; short form: set add-on option `mode: active`, open Status, flip "Sterowanie dozwolone", watch decisions with `commanded: true` (first effect: go-e setpoint lowered to fit the L1 reserve, accepted by the maintainer), then kill switch on/off, then the active phase test per heater (expect L2 for `switch.grzalka_1`, L1 for `_2`, L3 for `_3`, the last one first real proof), then control off (nothing sent, devices stay). Abort and switch control off on any call to an undeclared entity, any "not a declared actuator" refusal, unconfirmed device, or pending count stuck above 0.
2. ~~Mosquitto restart check (PKG-06)~~ done 2026-10-26 (see Open requirements; PKG-06 is verified live).
3. Finish the shadow week (VER-05), then decide on leaving control on.
4. Stage 6 public release.
5. v2.0 Economic Dispatch, designed 2026-10-26 in `docs/14-ECONOMICS.md` (ECO-01..16; reason codes list version 6; net metering deferred), six slices, each shadow-releasable, the first two with no effect on decisions:
   1. ~~Tariff module (flat, zoned, G13 as a config template) and the optional `economics:` section, read-only, `GET api/economics` (ECO-01, 02).~~ Built 2026-10-26 (unreleased, version not bumped): `src/Switchboard.Domain/Economics/`, `EconomicsSectionParser`, `control.economics` parameters (bound, not read), `api/economics`, `deploy/examples/economics-g13.yaml`, wizard keeps the section. Not yet done: the live check of the zone against the Tauron contract (needs the maintainer's file and a release), the release 0.10.0 itself. Slices 2 and 3 below are built.
   2. ~~Settlement module (`hourly_netting` wrapping the BAL rule, `net_billing`) and effective value, `sources` parsed, pure domain (ECO-03, 04).~~ Built 2026-10-26 (unreleased, version not bumped): `Settlement.cs`, `PriceBook.cs`, `EffectiveValue.cs`, `SourceSet.cs` in `src/Switchboard.Domain/Economics/`; `DeviceDefinition` carries `ValueFollows`, `ValueFactor`, `Sources` (narrowed) and `SourcesDeclared` (not on `DeviceSpec`, which the guard sees); `api/economics` echoes `devices[]` with `vEff` and `sources`; issue codes `sources_unusable` and `value_follows_without_price`; `net_metering` refused as deferred. Wired by slice 3, which consumes `ISettlementModel.Decide`, `EffectiveValue.At`, `ValueTest.Worth` and `Sources` in the allowance gate.
   3. ~~Dispatch: live value order, `sources` allowance gate, price test, reason codes v6, decision inputs, no-`economics` replay identical to 0.9 (ECO-05..09, 14, 15).~~ Built 2026-10-26 (unreleased, version not bumped, shadow only; "As built (slice 3)" in `14-ECONOMICS.md`): `ApplyAllowanceGate` in `DispatchEngine.Balance.cs` (a device with no `sources` keeps the 0.9 balance rule exactly, a device with `sources` is allowed by surplus or the active tariff zone, price test, zone-end margin), `ValueOrder` and `StableAdoption` (the phase ranking's rule shared), `DispatchSetup.Economics`, reason codes list version 6 (Domain, `DecisionJson`, SPA list, sentences, contract tests), `economics` block in the dispatch decision inputs and `api/decisions`; ECO-05 to ECO-09 and ECO-15 ticked, ECO-14 open (dynamic-price and statistics rows, slices 4 and 5). ECO-15 is held by a digest test against the 0.9 engine (balance worlds, synthetic replays, live 72 h). Not done: the live shadow check on the maintainer's G13 contract and the release 0.11.0 itself. 
   4. ~~Dynamic prices from an HA entity (ECO-10, the dynamic rows of ECO-14).~~ Built 2026-10-26 (unreleased, version not bumped, shadow only; "As built (slice 4)" in `14-ECONOMICS.md`): `SignalKind.Price`, roles `price_import` / `price_export`, unit `<currency>/kWh|MWh` with the per-MWh division in the normalizer only and the label checked against `economics.currency`, `DynamicPrice` (`signal` + required `fallback` + optional `add`; missing fallback invalid), `PriceInputs` beside the dispatch inputs, `price_basis` live / fallback, the fallback entering and leaving as one log line each (no decision, list stays version 6), `api/economics` and the Signals page, wizard keeps price signals and `economics:`, `deploy/examples/economics-dynamic.yaml`. ECO-10 ticked, ECO-14 open (the statistics row, slice 5). Not done: the live check with the maintainer's real price entity and the release itself. **Next: slice 5** (savings accounting; `PeriodPrice.Mean` does not read live prices, see the slice 4 notes); slice 6 (UI) needs 3 and 5.
   5. ~~Savings accounting in statistics (ECO-11).~~ Built 2026-10-26 (unreleased, version not bumped, shadow only; "As built (slice 5)" in `14-ECONOMICS.md`): per-bucket time-weighted import and export price and basis from what dispatch used each step, per-device value at `v_eff`, `statistics.json` schema 2 (schema 1 still read, no price data, no back-fill), `SavingsAccounting` (hour or bucket, counted when complete and priced, running apart, `since`, fallback flag), `savings` in `GET api/economics` with a `from`/`to` range, `prices` in `GET api/statistics`. No card yet.
   6. ~~UI (savings, tariff status, device card value and sources; ECO-12, 13).~~ Built 2026-10-26 (unreleased, version not bumped; "As built (slice 6 UI)" in `14-ECONOMICS.md`): `api/economics` client and sample, Tariff and Savings cards, savings in the decision-log summary, device card value, sources and allowed-now (`class` per device is the one backend field added), en and pl, tests; looked at in a browser at 375 px in both themes against sample data. UI-12 stays open (goal is v3.0). Next: commit and release, then the live shadow week on the real G13 contract with the maintainer's consent (ECO-16), then 2.0.0.
   Maintainer's open questions (recommendations in the doc): currency label, G13 hours and prices, dynamic prices in 2.0, savings baseline, export price source. Then v3.0, then the EV module. Maintainer's device-wants model (devices report "want to charge", power and energy-by-deadline; car and storage before the water heater; value per kWh follows prices and the fuel saved) is in `DECISIONS.md` and `12-EV-MODULE.md`.

## Open questions for the maintainer

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

`/addon_configs/fb9ed8c0_switchboard/switchboard.yaml` equals `deploy/examples/switchboard.yaml` (sha256 1fec5453…c1f4, LF) including the go-e select stop (`stop_actuator: select.goe_422752_frc`, `stop_option: "1"`, `resume_option: "0"`), applied 2026-10-25 with the maintainer's approval. Open live checks for phase 2: does `select.goe_422752_frc` report only on change, go unavailable on a go-e link loss, reset on unplug; are its options exactly 0/1/2.
