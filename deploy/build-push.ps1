#!/usr/bin/env pwsh
# Build the Switchboard add-on image locally (multi-arch) and push it to GHCR.
# This PUSHES. It is a release tool: never run it from a check, a test or CI.
#
# Usage:
#   ./deploy/build-push.ps1 -Version 0.1.0
#   ./deploy/build-push.ps1 -Version 0.1.1 -Dev     # amd64 only, faster (skips the arm64/QEMU leg)
#
# Prereqs (one-time):
#   docker login ghcr.io -u krzyl2                   # PAT with write:packages
#   docker run --privileged --rm tonistiigi/binfmt --install all
#   docker buildx create --use
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Image = 'ghcr.io/krzyl2/switchboard',
    [string]$Platforms = 'linux/amd64,linux/arm64',
    [switch]$SkipConfigSync,
    # -Dev: amd64-only build for fast iteration on an amd64 host. Not for real releases:
    # aarch64 users would be left on a stale image.
    [switch]$Dev
)

# -Dev forces single-arch amd64 unless the caller passed an explicit -Platforms override.
if ($Dev -and -not $PSBoundParameters.ContainsKey('Platforms')) {
    $Platforms = 'linux/amd64'
    Write-Host "-Dev: building amd64-only ($Platforms)"
}

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$configPath = Join-Path $repoRoot 'switchboard/config.yaml'

# 1. Keep config.yaml version == image tag: the Supervisor pulls image:version, and it reads
#    config.yaml from the repository's default branch.
if (-not $SkipConfigSync) {
    $config = Get-Content $configPath -Raw
    $config = [regex]::Replace($config, '(?m)^version:\s*".*"\s*$', "version: `"$Version`"")
    Set-Content -Path $configPath -Value $config -NoNewline
    Write-Host "switchboard/config.yaml -> version `"$Version`""
}

# 2. Multi-arch buildx build + push. switchboard/Dockerfile builds the SPA and publishes the
#    .NET host itself, so nothing is built on the host first. BUILD_VERSION becomes the
#    assembly version shown in the UI and the io.hass.version label.
$buildDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$buildRef = git rev-parse HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or -not $buildRef) { $buildRef = 'unknown' }
$buildRef = "$buildRef".Trim()

docker buildx build `
    --platform $Platforms `
    -f switchboard/Dockerfile `
    -t "${Image}:$Version" `
    -t "${Image}:latest" `
    --build-arg BUILD_VERSION=$Version `
    --build-arg BUILD_DATE=$buildDate `
    --build-arg BUILD_REF=$buildRef `
    --push .
if ($LASTEXITCODE -ne 0) { throw "docker buildx build failed" }

Write-Host ""
Write-Host "Pushed ${Image}:$Version (+ :latest)"
Write-Host "Next: add the version to switchboard/CHANGELOG.md, commit switchboard/config.yaml to the default branch, then update the add-on in Home Assistant."
