# Release

One command releases: `pwsh deploy/release.ps1 -Version x.y.z -Subject '<text>' -Notes <file or text>`. In Claude Code use `/deploy` (`.claude/skills/deploy/SKILL.md`), which writes the notes and runs the script after your explicit go-ahead.

## One-time setup

1. In Home Assistant: profile (bottom left) > Security > Long-lived access tokens > Create. Copy it.
2. In your own terminal: `pwsh deploy/set-ha-token.ps1`, paste the token at the hidden prompt. It checks the token with one WebSocket handshake and stores it DPAPI-protected for your Windows user in `%LOCALAPPDATA%\Switchboard\ha-token.dpapi`. Never paste it into chat, a file in the repo or a command line. `-Clear` deletes it.
3. The HA URL defaults to `http://192.168.10.21:55285`; override with env `SWITCHBOARD_HA_URL` or a one-line `%LOCALAPPDATA%\Switchboard\ha-url.txt`.
4. Also needed: `gh` logged in as `krzyl2` (the active account is never switched), Docker buildx builder `switchboard-builder`, `docker login ghcr.io`, Node 22+.

CI-style fallback only: env `SWITCHBOARD_HA_TOKEN` is used when the DPAPI file does not exist.

## Stages

`preflight` (main, tree allow-list, strict bump, krzyl2, builder, token) > `bump` (config.yaml, Directory.Build.props, WebSurfaceTests, CHANGELOG from `-Notes`) > `verify` (Release build 0 warnings, all tests, web test and build only if web/ changed, `call_service` guard) > `commit` (explicit paths, `<version>: <Subject>`, co-author trailer) > `push` (as krzyl2) > `image` (build-push.ps1, digest, amd64 + arm64 check) > `deploy` (store reload, update, wait for the version started) > `postcheck` (logs, signals, go-e state, api/status; read only).

Each stage prints PASS or FAIL and the script stops at the first FAIL with exit code 10 plus the stage number (11 preflight ... 18 postcheck); 2 is bad arguments. Re-run with `-From <stage>`; every stage is idempotent. `-DryRun` runs preflight and verify for real and only prints the rest. `-Check` is preflight only. `-SelfTest` tests the script's pure functions against `deploy/tests/` (no network, no git changes). The summary goes to `deploy/out/last-release.json` (gitignored).

## Never touched

Add-on options, `mode`, the control switch, the live `switchboard.yaml`, `switch.grzalka_*`, `number.goe_*`, `select.goe_*`. The update restarts the add-on: the go-e is stopped once and Switchboard resumes it with option `0`.

## Rollback

Not automatic. The script prints the previous version and a forward-roll (`git revert` then a new, higher version), because the Supervisor only offers the manifest version on main. The previous image tag stays in GHCR.
