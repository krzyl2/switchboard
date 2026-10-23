# Changelog

All notable changes to this add-on. Newest version first; format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## 0.1.0

Stage 0 skeleton. Controls nothing.

### Added

- Add-on packaging: custom repository, ingress page (sidebar or Open Web UI), amd64 and aarch64 images.
- Connection to Home Assistant over the Supervisor WebSocket, with keepalive and reconnect.
- MQTT discovery entity `binary_sensor.switchboard_healthy`, with MQTT last will and `expire_after`, so your own automation can react when Switchboard is not running.
- Status page through ingress (sidebar or Open Web UI): mode, Home Assistant and MQTT connection, health, version.
- `mode` option, `shadow` by default.
- Standalone Docker path configured through environment variables.
