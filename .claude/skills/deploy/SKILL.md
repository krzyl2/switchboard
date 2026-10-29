---
name: deploy
description: Release Switchboard (bump, verify, commit, push, image, deploy to the live HA, post-check) by running deploy/release.ps1. Use when the maintainer says deploy, release or ship.
---

# /deploy

The release is done by `deploy/release.ps1`, not by you. You only write the notes and the subject, run the script and report. Details: `docs/RELEASE.md`.

## Steps

1. Read `git log --oneline` since the last release commit (the newest subject that starts with `x.y.z:`) and the top two entries of `switchboard/CHANGELOG.md` for the style.
2. Propose the next version (patch bump unless the user says otherwise), a `-Subject` (no version prefix, no type tag, `<version>: <subject>` at most 72 chars) and `-Notes` (3-6 factual lines: `### Fixed` / `### Added` headings with `- ` bullets, as in the CHANGELOG). Write the notes to a temp file in the scratchpad.
3. Run `pwsh deploy/release.ps1 -Version <v> -Subject '<s>' -Notes <file> -DryRun` and show the user the notes, subject, version and the stage lines.
4. Ask for a go-ahead. **Never run the real release without a fresh, explicit approval in this turn for commit + push + image + deploy.** An earlier approval, or approval of the notes only, does not count.
5. Run the same command without `-DryRun`. Report the final summary (`deploy/out/last-release.json`: commit, tests, image digest, add-on version, signals, go-e state, elapsed) and the rollback hint the script prints.

## When a stage fails

Report the stage name, the script's one-line reason and the exit code, then stop. Do not improvise a manual fix of that stage with ad-hoc commands (no hand-run git, docker, dotnet, curl or HA calls to get past it). Propose the fix and ask. After the cause is fixed and the user agrees, resume with the same arguments plus `-From <stage>`; stages are idempotent.

## Never

- Touch the live `switchboard.yaml`, the add-on options, `mode`, the control switch, or any entity (`switch.grzalka_*`, `number.goe_*`, `select.goe_*`). The script only reads the go-e states.
- Read, print, ask for or store the HA token. It lives DPAPI-protected in `%LOCALAPPDATA%\Switchboard\ha-token.dpapi`; the user sets it once with `pwsh deploy/set-ha-token.ps1` in their own terminal, never in chat. If preflight says it is missing, tell the user to run that.
- Switch the active `gh` account, use `git add -A`, stage `src/Switchboard.Host/Properties/`, or edit versions by hand (the script bumps every place).
- Run `-Rebuild`, `-From` / `-Stage` to skip verify, or `-CoAuthor` overrides unless the user asks.
