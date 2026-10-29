#!/usr/bin/env pwsh
#Requires -Version 7.2
<#
.SYNOPSIS
    Stores the Home Assistant Long-Lived Access Token for deploy/release.ps1, DPAPI-protected for the current Windows user.

.DESCRIPTION
    Run it yourself in your own terminal. It prompts for the token (hidden), checks it with one authenticated
    WebSocket handshake against SWITCHBOARD_HA_URL (default http://192.168.10.21:55285, or ha-url.txt next to the
    token file), writes %LOCALAPPDATA%\Switchboard\ha-token.dpapi and prints only "OK, token stored for user <name>".
    The token is never echoed, never put in a process argument or environment, never written anywhere else.
    Create the token in HA: profile (bottom left) > Security > Long-lived access tokens.

.PARAMETER Clear
    Delete the stored token file.
.PARAMETER NoValidate
    Store without the handshake (HA unreachable right now).
#>
[CmdletBinding()]
param([switch]$Clear, [switch]$NoValidate)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ha-token.ps1')

$path = Get-HaTokenPath
if ($Clear) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    Write-Host "OK, token cleared for user $env:USERNAME"
    exit 0
}

$secure = Read-Host -AsSecureString 'Home Assistant long-lived access token'
if ($secure.Length -eq 0) { Write-Host 'FAIL  empty input' -ForegroundColor Red; exit 1 }

if (-not $NoValidate) {
    $env:SWITCHBOARD_HA_URL = Resolve-HaUrl
    $plain = [System.Net.NetworkCredential]::new('', $secure).Password
    $out = $plain | & node (Join-Path $PSScriptRoot 'ha-supervisor.mjs') ping --token-stdin 2>$null
    $plain = $null
    $j = $out | Where-Object { $_ -match '^\{' } | Select-Object -Last 1 | ForEach-Object { $_ | ConvertFrom-Json }
    if (-not $j -or -not $j.ok) { Write-Host "FAIL  handshake with $env:SWITCHBOARD_HA_URL: $(if ($j) { $j.error } else { 'no answer' }). Nothing stored." -ForegroundColor Red; exit 1 }
}

Save-HaToken $secure $path
Write-Host "OK, token stored for user $env:USERNAME"
