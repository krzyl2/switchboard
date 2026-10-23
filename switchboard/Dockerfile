# Switchboard add-on image. Build context = repository root:
#   docker buildx build -f switchboard/Dockerfile .
# deploy/build-push.ps1 does the multi-arch build and push; deploy/docker-compose.dev.yml
# builds it for the local dev stack. switchboard/Dockerfile.dockerignore limits the context.

# BUILD_FROM must be a global ARG declared before the first FROM: an ARG declared between
# FROM lines is not visible to a later FROM. Since Supervisor 2026.04 BUILD_FROM is no longer
# passed by default, so the default here is the real value, pinned to an exact base release.
ARG BUILD_FROM=ghcr.io/home-assistant/base-debian:trixie-2026.10.0
# Empty means "use <Version> from Directory.Build.props" (local dev builds).
ARG BUILD_VERSION=""
# Set by buildx per platform; global so the last FROM can pick the per-arch label stage.
ARG TARGETARCH

# ── Stage 1: SPA build (Node, discarded) ─────────────────────────────────────
# Pinned to BUILDPLATFORM: the output is plain JS/CSS, identical for every target arch,
# so emulating this stage under QEMU for the arm64 leg would only cost time.
FROM --platform=$BUILDPLATFORM node:24.21.0-alpine3.24 AS ui-build
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# vite.config.ts writes to ../src/Switchboard.Host/wwwroot relative to web/,
# i.e. /src/src/Switchboard.Host/wwwroot here — not web/dist.
RUN npm run build

# ── Stage 2: publish the .NET host (SDK, discarded) ──────────────────────────
# Pinned to BUILDPLATFORM. The publish is framework-dependent and RID-less and s6 starts it
# as `dotnet Switchboard.Host.dll`, so the output is portable IL, correct in both the amd64
# and the arm64 image. Argus learned the hard way that running the SDK under QEMU for the
# arm64 leg buys nothing and can segfault the build.
# The tag is the exact SDK that global.json pins.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402 AS dotnet-build
ARG BUILD_VERSION
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /src
COPY Switchboard.slnx global.json Directory.Build.props Directory.Packages.props BannedSymbols.txt ./
COPY src/ ./src/
# The SPA build output must be in wwwroot before publish: the Web SDK's static web assets
# pipeline snapshots wwwroot at publish time.
COPY --from=ui-build /src/src/Switchboard.Host/wwwroot/ ./src/Switchboard.Host/wwwroot/
# UseAppHost=false: the apphost would be a build-platform (x64) ELF, useless in the arm64
# image; the process is always started through the dotnet muxer.
RUN set -eu; \
    version_arg=""; \
    if [ -n "${BUILD_VERSION}" ]; then version_arg="-p:Version=${BUILD_VERSION}"; fi; \
    dotnet publish src/Switchboard.Host/Switchboard.Host.csproj \
        -c Release --self-contained false -p:UseAppHost=false ${version_arg} \
        -o /app/publish

# ── Stage 3: runtime ─────────────────────────────────────────────────────────
FROM ${BUILD_FROM} AS runtime

# Exit the container when a service fails instead of looping silently. The base image sets
# it too; stated here so the image does not depend on that default staying put.
ENV S6_BEHAVIOUR_IF_STAGE2_FAILS=2
# The base image makes s6-overlay wait, without a time limit, for every service to be up
# before it would run a CMD. There is no CMD here, and the wait deadlocks the halt in
# finish when the process dies before the wait first sees it up (a fast crash at start):
# the shutdown then queues behind a start-up that never completes. Verified on this base.
ENV S6_CMD_WAIT_FOR_SERVICES=0
# s6 waits only 3 s by default for services to die at shutdown. The host needs time to
# publish health OFF and availability "offline" and to disconnect cleanly.
ENV S6_SERVICES_GRACETIME=8000
# After the services are down, s6 sends TERM to whatever is left and always waits this long
# before KILL (default 3 s, spent in full even when nothing is left). By then the host has
# already exited, so 1 s is plenty, and 8 s + 1 s stays under the Supervisor's default 10 s
# stop timeout; with the default the worst case was 11 s and Docker's SIGKILL replaced the
# real exit code with 137 (seen by deploy/e2e/stage0.ps1 E6 on a host with a short stop timeout).
ENV S6_KILL_GRACETIME=1000
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# ASP.NET Core runtime (not the SDK) via the official dotnet-install.sh, which picks the
# arch-correct build for the image being built. The packages.microsoft.com feed does not
# carry every runtime for arm64 on Debian, which is why Argus moved off apt for this.
# Native dependencies come from Debian (trixie package names); curl is in the base image.
ARG DOTNET_ASPNETCORE_VERSION=10.0.13
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        libgcc-s1 \
        libicu76 \
        libssl3t64 \
        libstdc++6 \
        tzdata \
    && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh \
        --runtime aspnetcore \
        --version "${DOTNET_ASPNETCORE_VERSION}" \
        --install-dir /usr/share/dotnet \
        --no-path \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh \
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet-build /app/publish/ /opt/switchboard/

# s6 service definition. A CRLF shebang is fatal in the container (s6 tries to exec
# "bashio\r"), so CRs are stripped here even though .gitattributes pins these files to LF.
COPY switchboard/rootfs/ /
RUN find /etc/services.d/switchboard -type f -exec sed -i 's/\r$//' {} + \
    && chmod +x /etc/services.d/switchboard/run /etc/services.d/switchboard/finish

ARG BUILD_DATE
ARG BUILD_REF
ARG BUILD_VERSION
LABEL \
    io.hass.name="Switchboard" \
    io.hass.description="Phase-aware load dispatch for Home Assistant. Not a protective device." \
    io.hass.type="app" \
    io.hass.version="${BUILD_VERSION}" \
    org.opencontainers.image.title="Switchboard" \
    org.opencontainers.image.source="https://github.com/krzyl2/switchboard" \
    org.opencontainers.image.created="${BUILD_DATE}" \
    org.opencontainers.image.revision="${BUILD_REF}" \
    org.opencontainers.image.version="${BUILD_VERSION}"

# No EXPOSE: the only listener is the ingress port, reached through the Supervisor's
# ingress proxy, which does not need it. No CMD or ENTRYPOINT: the base image's
# ENTRYPOINT is /init (s6-overlay), and overriding it would keep the service from starting.

# io.hass.arch carries Home Assistant's arch names, which differ from Docker's for ARM
# ("aarch64" vs TARGETARCH "arm64"). One stage per supported arch, chosen by TARGETARCH, so a
# single multi-platform build labels each manifest entry correctly. Any other TARGETARCH
# names a stage that does not exist, and the build fails instead of shipping a wrong label.
FROM runtime AS runtime-amd64
LABEL io.hass.arch="amd64"

FROM runtime AS runtime-arm64
LABEL io.hass.arch="aarch64"

FROM runtime-${TARGETARCH}
