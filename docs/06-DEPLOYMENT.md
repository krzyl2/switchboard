# Deployment

Modelled on the Argus add-on (`C:\Workspace\Repos\Tools\Anomaly`), which is already operated by the same maintainer. The shape below is deliberately the same so that the build, install and update habits carry over.

## Repository layout

```
switchboard/
  repository.yaml              # HA add-on repository descriptor
  switchboard/                 # the add-on folder itself
    config.yaml                # add-on manifest + options schema
    Dockerfile                 # HA base image + our published artifacts
    DOCS.md                    # what the user reads in the add-on store
    CHANGELOG.md
    icon.png  logo.png
    rootfs/                    # s6 service definitions, entrypoint
    translations/
      en.yaml
      pl.yaml
  src/                         # application source
  web/                         # ingress SPA
  deploy/
    Dockerfile.*               # build images
    docker-compose.dev.yml     # local dev: HA, broker, add-on
    build-push.ps1             # buildx → GHCR, multi-arch
  docs/                        # this documentation
  tests/
```

## Add-on manifest

```yaml
name: "Switchboard"
description: "Phase-aware load dispatch: keeps every phase under its breaker and sends power where it is worth most. Best-effort; not a protective device."
version: "0.1.0"
slug: switchboard
url: "https://github.com/<owner>/switchboard"
image: "ghcr.io/<owner>/switchboard"
arch: [amd64, aarch64]
startup: application
boot: auto
init: false
ingress: true
ingress_port: 8099
panel_icon: "mdi:transmission-tower"
panel_title: "Switchboard"
homeassistant_api: true
services:
  - mqtt:need
map:
  - type: data
watchdog: "tcp://[HOST]:8099"

options:
  mode: "shadow"
  log_level: "info"

schema:
  mode: list(shadow|active)
  log_level: list(debug|info|warning)
```

Deliberately minimal. Everything else — meter mapping, phases, devices — is configured in the ingress UI and persisted under `/data`, because an options form cannot express per-device structures or run a phase-assignment test. Argus took the same path when it moved from YAML options to the ingress config UI; start there instead of repeating the migration.

`mode` stays in the manifest options so a user can force shadow mode without opening the UI.

## Authentication

- **Home Assistant**: `SUPERVISOR_TOKEN` from the environment, with `homeassistant_api: true`. WebSocket through the Supervisor proxy; no user-supplied token anywhere.
- **MQTT**: Supervisor service discovery (`services: mqtt:need`). Manual broker settings only as a fallback for the standalone path.
- **Ingress**: authentication is Home Assistant's. The add-on exposes no other port. There is no outbound connection other than to HA and the broker.

## Persistence

`/data` holds the configuration document, applied device states, overload budgets and the decision log. Everything written there is treated as untrusted on read (schema-validated), because a user may edit it.

All timers restart from process start. Persisted lockout *expiry* timestamps are not trusted across a restart — a device that was locked out stays locked out for a fresh full interval instead.

## Container

Single container, s6 supervising the processes. HA base images for the right arch; the application layer copies published artifacts rather than building inside the image.

Multi-arch build and push with buildx to GHCR, run locally (`deploy/build-push.ps1`), the same as Argus. CI is optional and not a v1.0 requirement.

## Local development

`deploy/docker-compose.dev.yml` brings up Home Assistant, Mosquitto and the add-on with `/data` mounted locally. The dev stack needs a fake meter: a set of HA template sensors driven by an input_number per phase, so that overload scenarios can be produced by hand without waiting for a kettle. That harness is part of the dev stack, not of the add-on.

## Release checklist

1. Version bump in `config.yaml` + `CHANGELOG.md`.
2. Multi-arch build and push to GHCR.
3. Install from the custom repository on a real HA instance.
4. Shadow mode, at least one week on a live three-phase installation, decision log reviewed.
5. Only then is `active` recommended in DOCS.md.

## Standalone path

For HA Container / Core users, the same image runs under plain Docker with HA URL, long-lived token and MQTT settings supplied by environment variables. It is documented, not the primary path.

## What not to do

| Don't | Why |
|---|---|
| Expose a port besides ingress | The add-on holds a Supervisor token; anything reachable from the LAN is an attack surface |
| Read the Recorder database directly | Unsupported, breaks across HA versions; use WebSocket plus history API |
| Ship default device configurations | Commanding an entity the user did not declare is the worst possible failure mode for this product |
| Write actuator state into options.json on every change | Options are user configuration; runtime state belongs in `/data` |
| Depend on add-on options for structured config | They cannot express lists of objects well, and the UI needs to validate interactively |
