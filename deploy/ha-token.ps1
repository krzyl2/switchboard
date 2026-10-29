# Dot-sourced by release.ps1 and set-ha-token.ps1: the Home Assistant token, DPAPI-protected for the current Windows user.
# The plain text exists only in memory, right before it is handed to ha-supervisor.mjs over stdin.

function Get-HaTokenDir {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $HOME '.local/share' }
    return (Join-Path $base 'Switchboard')
}
function Get-HaTokenPath { return (Join-Path (Get-HaTokenDir) 'ha-token.dpapi') }
function Get-HaUrlPath { return (Join-Path (Get-HaTokenDir) 'ha-url.txt') }

function Resolve-HaUrl {
    # env var, else ha-url.txt next to the token file, else the maintainer's instance. Not secret.
    if ($env:SWITCHBOARD_HA_URL) { return $env:SWITCHBOARD_HA_URL.Trim() }
    $f = Get-HaUrlPath
    if (Test-Path -LiteralPath $f) { $u = (Get-Content -LiteralPath $f -TotalCount 1).Trim(); if ($u) { return $u } }
    return 'http://192.168.10.21:55285'
}

function Save-HaToken {
    param([Parameter(Mandatory)][securestring]$Secure, [string]$Path = (Get-HaTokenPath))
    New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
    # No -Key: Windows DPAPI, bound to the current user on this machine.
    [IO.File]::WriteAllText($Path, (ConvertFrom-SecureString $Secure), (New-Object System.Text.UTF8Encoding($false)))
}

function Test-HaTokenFile {
    # $true when the file exists and the current user can decrypt it.
    param([string]$Path = (Get-HaTokenPath))
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try { $s = ConvertTo-SecureString ([IO.File]::ReadAllText($Path).Trim()); return $s.Length -gt 0 } catch { return $false }
}

function Read-HaToken {
    # Plain text token, in memory only: DPAPI file first, env var (CI-style fallback) only if the file is missing.
    param([string]$Path = (Get-HaTokenPath), [bool]$AllowEnvFallback = $true)
    if (Test-Path -LiteralPath $Path) {
        $s = ConvertTo-SecureString ([IO.File]::ReadAllText($Path).Trim())
        return [System.Net.NetworkCredential]::new('', $s).Password
    }
    if ($AllowEnvFallback -and $env:SWITCHBOARD_HA_TOKEN) { return $env:SWITCHBOARD_HA_TOKEN }
    return $null
}
