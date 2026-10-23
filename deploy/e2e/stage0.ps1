#Requires -Version 7.2
<#
.SYNOPSIS
    Stage 0 end-to-end check: real Home Assistant + Mosquitto + the Switchboard image, on this machine.

.DESCRIPTION
    Builds the add-on image, brings up the dev stack from deploy/docker-compose.dev.yml on a fresh,
    disposable Home Assistant, and checks E1..E13 against it. Prints PASS/FAIL per check and exits
    with 1 if any check failed. Leaves the stack DOWN (images are kept) unless -KeepUp is given.

    What it does, in order:
      1. docker compose down, delete deploy/dev-data/ and seed deploy/dev-data/ha from
         deploy/dev-ha/configuration.yaml (plus websocket_api debug logging, which E12 reads).
      2. Build switchboard:dev (skip with -SkipBuild when the image is current).
      3. Start mosquitto + homeassistant, onboard HA through its onboarding API with a random
         one-off owner password (never printed, never written), mint a long-lived token over the
         websocket, and add the MQTT integration (broker "mosquitto", port 1883) via config flow.
      4. Start switchboard with that token and log level debug, then run the checks.

    The token reaches docker compose through this process' environment only. deploy/.env is
    neither read nor written: shell variables take precedence over it, so an .env of your own is
    left untouched and does not interfere.

    Safe to re-run: every run starts from an empty deploy/dev-data/. The dev HA is bound to
    127.0.0.1 and thrown away on the next run. Nothing is pushed anywhere.

.PARAMETER SkipBuild
    Reuse the existing switchboard:dev image instead of rebuilding it.

.PARAMETER KeepUp
    Leave the stack running after the checks (for poking around). Default: docker compose down.

.EXAMPLE
    pwsh -File deploy/e2e/stage0.ps1
.EXAMPLE
    pwsh -File deploy/e2e/stage0.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$KeepUp
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$runClock = [Diagnostics.Stopwatch]::StartNew()

$DeployDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot = (Resolve-Path (Join-Path $DeployDir '..')).Path
$ComposeFile = Join-Path $DeployDir 'docker-compose.dev.yml'
$DevData = Join-Path $DeployDir 'dev-data'
$HaTemplate = Join-Path $DeployDir 'dev-ha/configuration.yaml'
$AppSettingsFile = Join-Path $RepoRoot 'src/Switchboard.Host/appsettings.json'

$HaUrl = 'http://127.0.0.1:8123'
$HaWsUrl = 'ws://127.0.0.1:8123/api/websocket'
$SwitchboardUrl = 'http://127.0.0.1:8099'
$OAuthClientId = "$HaUrl/"
$EntityId = 'binary_sensor.switchboard_healthy'
$ExpectedUniqueId = 'switchboard_healthy'
# docker compose builds without BUILD_VERSION, so the image carries the Directory.Build.props default.
$ExpectedVersion = (Select-Xml -Path (Join-Path $RepoRoot 'Directory.Build.props') -XPath '//Version').Node.InnerText
$GuardContainer = 'switchboard-e2e-guard'
$CrashContainer = 'switchboard-e2e-crash'

# The expiry bounds of E7 and E8 follow the shipped configuration instead of repeating it here.
$appSettings = Get-Content -Raw $AppSettingsFile | ConvertFrom-Json
$ExpireAfter = [TimeSpan]::Parse($appSettings.Switchboard.Heartbeat.ExpireAfter)
$HeartbeatInterval = [TimeSpan]::Parse($appSettings.Switchboard.Heartbeat.Interval)
$PingInterval = [TimeSpan]::Parse($appSettings.Switchboard.HomeAssistant.PingInterval)
$PongTimeout = [TimeSpan]::Parse($appSettings.Switchboard.HomeAssistant.PongTimeout)

# ── Output and bookkeeping ────────────────────────────────────────────────────

$results = [Collections.Generic.List[object]]::new()

function Write-Step([string]$Text) {
    Write-Host ("[{0,6:0.0}s] {1}" -f $runClock.Elapsed.TotalSeconds, $Text)
}

function Invoke-Check {
    param([string]$Id, [string]$Name, [scriptblock]$Body)
    Write-Step "$Id $Name ..."
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        # Only the hashtable a check returns at the end counts; stray pipeline output does not.
        $outcome = @(& $Body)[-1]
        $passed = [bool]$outcome.Passed
        $evidence = [string]$outcome.Evidence
    }
    catch {
        $passed = $false
        $evidence = "exception at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"
    }
    $result = [pscustomobject]@{
        Id       = $Id
        Name     = $Name
        Passed   = $passed
        Seconds  = [math]::Round($clock.Elapsed.TotalSeconds, 1)
        Evidence = $evidence
    }
    $results.Add($result)
    $verdict = if ($passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("         {0} {1}: {2}" -f $verdict, $Id, $evidence) -ForegroundColor ($passed ? 'Green' : 'Red')
}

function Wait-Until {
    param([scriptblock]$Condition, [double]$TimeoutSeconds, [int]$IntervalMilliseconds = 500)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($value) {
            return [pscustomobject]@{ Ok = $true; Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); Value = $value }
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    } while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    [pscustomobject]@{ Ok = $false; Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); Value = $null }
}

# ── Docker helpers ────────────────────────────────────────────────────────────

# A simple function on purpose: an advanced one would bind "-d" to its -Debug common parameter.
function Invoke-Compose {
    & docker compose -f $ComposeFile @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

function Get-ComposeLog([string]$Service) {
    $text = & docker compose -f $ComposeFile logs --no-color --no-log-prefix $Service 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "docker compose logs $Service failed" }
    $text
}

function Get-ContainerId([string]$Service) {
    $id = (& docker compose -f $ComposeFile ps -a -q $Service | Select-Object -First 1)
    if (-not $id) { throw "no container for service $Service" }
    $id.Trim()
}

function Get-ContainerIp([string]$Container) {
    (& docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $Container).Trim()
}

function Get-ContainerField([string]$Container, [string]$Template) {
    (& docker inspect -f $Template $Container).Trim()
}

function Remove-GuardContainer {
    & docker rm -f $GuardContainer *> $null
}

function Remove-CrashContainer {
    & docker rm -f $CrashContainer *> $null
}

# ── Home Assistant helpers ────────────────────────────────────────────────────

# Shared with the watcher thread job (E12), which gets them through -InitializationScript.
$WsHelpers = {
    function Receive-WsMessage {
        param([System.Net.WebSockets.ClientWebSocket]$Ws, [Threading.CancellationToken]$CancellationToken)
        $buffer = [byte[]]::new(65536)
        $stream = [IO.MemoryStream]::new()
        try {
            do {
                $segment = [ArraySegment[byte]]::new($buffer)
                $received = $Ws.ReceiveAsync($segment, $CancellationToken).GetAwaiter().GetResult()
                if ($received.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
                $stream.Write($buffer, 0, $received.Count)
            } while (-not $received.EndOfMessage)
            [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json -Depth 64
        }
        finally { $stream.Dispose() }
    }

    function Send-WsJson {
        param([System.Net.WebSockets.ClientWebSocket]$Ws, [hashtable]$Message)
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Message | ConvertTo-Json -Depth 16 -Compress))
        $null = $Ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }

    function Connect-HaWebSocket {
        param([string]$Uri, [string]$Token, [Threading.CancellationToken]$CancellationToken)
        $ws = [System.Net.WebSockets.ClientWebSocket]::new()
        $null = $ws.ConnectAsync([Uri]$Uri, $CancellationToken).GetAwaiter().GetResult()
        $hello = Receive-WsMessage -Ws $ws -CancellationToken $CancellationToken
        if ($hello.type -ne 'auth_required') { throw "unexpected websocket greeting '$($hello.type)'" }
        Send-WsJson -Ws $ws -Message @{ type = 'auth'; access_token = $Token }
        $reply = Receive-WsMessage -Ws $ws -CancellationToken $CancellationToken
        if ($reply.type -ne 'auth_ok') { throw "websocket auth failed: '$($reply.type)'" }
        $ws
    }
}
. $WsHelpers

$script:wsId = 0
function Invoke-HaCommand {
    param([System.Net.WebSockets.ClientWebSocket]$Ws, [hashtable]$Command)
    $script:wsId++
    $Command.id = $script:wsId
    Send-WsJson -Ws $Ws -Message $Command
    $cts = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
    try {
        while ($true) {
            $message = Receive-WsMessage -Ws $Ws -CancellationToken $cts.Token
            if ($null -eq $message) { throw 'websocket closed while waiting for a result' }
            if ($message.type -eq 'result' -and $message.id -eq $script:wsId) {
                if (-not $message.success) { throw "HA command $($Command.type) failed: $($message.error | ConvertTo-Json -Compress)" }
                return $message.result
            }
        }
    }
    finally { $cts.Dispose() }
}

function Invoke-HaWs {
    param([string]$Token, [hashtable]$Command)
    $ws = Connect-HaWebSocket -Uri $HaWsUrl -Token $Token -CancellationToken ([Threading.CancellationToken]::None)
    try { Invoke-HaCommand -Ws $ws -Command $Command }
    finally {
        try {
            $null = $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done',
                [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        catch { }
        $ws.Dispose()
    }
}

# Like Invoke-RestMethod, but an HTTP error carries HA's response body, which names the problem.
function Invoke-HaPost([string]$Path, [object]$Body) {
    $response = Invoke-WebRequest -Method Post -Uri "$HaUrl$Path" -Headers $script:haHeaders -ContentType 'application/json' `
        -Body ($Body | ConvertTo-Json -Depth 8) -SkipHttpErrorCheck -TimeoutSec 30
    if ($response.StatusCode -ge 300) { throw "POST $Path -> $($response.StatusCode): $($response.Content)" }
    $response.Content | ConvertFrom-Json -Depth 32
}

function Get-EntityState {
    try {
        (Invoke-RestMethod -Uri "$HaUrl/api/states/$EntityId" -Headers $script:haHeaders -TimeoutSec 5).state
    }
    catch { $null }
}

function Wait-EntityState([string]$State, [double]$TimeoutSeconds) {
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Condition { (Get-EntityState) -eq $State }
}

# Before onboarding only the unauthenticated /api/onboarding exists; HA stops serving it once
# onboarding is complete, so after a restart readiness is "GET /api/ with the token answers 200".
function Wait-HaApi([double]$TimeoutSeconds, [switch]$Authenticated) {
    Wait-Until -TimeoutSeconds $TimeoutSeconds -IntervalMilliseconds 1000 -Condition {
        try {
            if ($Authenticated) {
                (Invoke-WebRequest -Uri "$HaUrl/api/" -Headers $script:haHeaders -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode -eq 200
            }
            else {
                (Invoke-WebRequest -Uri "$HaUrl/api/onboarding" -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode -eq 200
            }
        }
        catch { $false }
    }
}

function Get-Status {
    Invoke-RestMethod -Uri "$SwitchboardUrl/api/status" -TimeoutSec 5
}

# /proc/net/tcp* print addresses as 32-bit words in host (little-endian) byte order.
function ConvertFrom-ProcNetAddress([string]$Text) {
    $ipHex, $portHex = $Text.Split(':')
    $bytes = [byte[]]::new($ipHex.Length / 2)
    for ($word = 0; $word -lt $ipHex.Length / 8; $word++) {
        for ($b = 0; $b -lt 4; $b++) {
            $bytes[$word * 4 + $b] = [Convert]::ToByte($ipHex.Substring($word * 8 + (3 - $b) * 2, 2), 16)
        }
    }
    $ip = [Net.IPAddress]::new($bytes)
    if ($ip.IsIPv4MappedToIPv6) { $ip = $ip.MapToIPv4() }
    [pscustomobject]@{ Address = $ip.ToString(); Port = [Convert]::ToInt32($portHex, 16) }
}

function Get-ContainerSockets([string]$Container, [string]$Protocol) {
    $raw = & docker exec $Container sh -c "cat /proc/net/$Protocol /proc/net/${Protocol}6 2>/dev/null"
    foreach ($line in $raw) {
        $fields = $line.Trim() -split '\s+'
        if ($fields.Count -lt 4 -or $fields[0] -eq 'sl') { continue }
        $local = ConvertFrom-ProcNetAddress $fields[1]
        $remote = ConvertFrom-ProcNetAddress $fields[2]
        [pscustomobject]@{
            Protocol      = $Protocol
            State         = $fields[3]
            Local         = "$($local.Address):$($local.Port)"
            LocalAddress  = $local.Address
            LocalPort     = $local.Port
            Remote        = "$($remote.Address):$($remote.Port)"
            RemoteAddress = $remote.Address
            RemotePort    = $remote.Port
            Inode         = $fields[9]
        }
    }
}

# Socket inodes held open by processes inside the container. A socket in the container's
# network namespace that no process there holds belongs to someone else: Docker's embedded DNS
# resolver (127.0.0.11) is the one expected case on a user-defined network, Supervisor included.
function Get-ContainerSocketInodes([string]$Container) {
    $links = & docker exec $Container sh -c 'for f in /proc/[0-9]*/fd/*; do readlink "$f"; done 2>/dev/null'
    $inodes = [Collections.Generic.HashSet[string]]::new()
    foreach ($link in $links) {
        $m = [regex]::Match($link, '^socket:\[(\d+)\]$')
        if ($m.Success) { $inodes.Add($m.Groups[1].Value) | Out-Null }
    }
    , $inodes
}

function Test-ContainerHttp([string]$FromService, [string]$Url) {
    # BusyBox wget in the mosquitto image: -S prints the status line on stderr, also for errors.
    $output = & docker compose -f $ComposeFile exec -T $FromService wget -S -O /dev/null -T 5 $Url 2>&1 | Out-String
    $match = [regex]::Match($output, 'HTTP/1\.[01] (\d{3})')
    if ($match.Success) { [int]$match.Groups[1].Value } else { "no HTTP status ($($output.Trim()))" }
}

# ── Run ───────────────────────────────────────────────────────────────────────

$watch = $null
$watcherJob = $null
$guardLog = ''
$sensitive = [Collections.Generic.List[string]]::new()

try {
    Write-Step 'Resetting the dev stack'
    Remove-GuardContainer
    Invoke-Compose down --remove-orphans
    if (Test-Path $DevData) { Remove-Item -Recurse -Force $DevData }
    New-Item -ItemType Directory -Force -Path (Join-Path $DevData 'ha'), (Join-Path $DevData 'switchboard') | Out-Null

    # E12 reads what each websocket peer sent; HA logs that only with websocket_api at debug.
    $template = Get-Content -Raw $HaTemplate
    $loggerBlock = "logger:`n  default: info"
    $normalized = $template -replace "`r`n", "`n"
    if (-not $normalized.Contains($loggerBlock)) {
        throw "deploy/dev-ha/configuration.yaml no longer contains '$($loggerBlock -replace "`n", '\n')'; update the e2e seeding."
    }
    $seeded = $normalized.Replace($loggerBlock, "$loggerBlock`n  logs:`n    homeassistant.components.websocket_api: debug")
    [IO.File]::WriteAllText((Join-Path $DevData 'ha/configuration.yaml'), $seeded)

    $imagePresent = [bool](& docker image ls -q switchboard:dev)
    if ($SkipBuild -and $imagePresent) {
        Write-Step 'Reusing switchboard:dev (-SkipBuild)'
    }
    else {
        Write-Step 'Building switchboard:dev'
        Invoke-Compose build switchboard
    }

    Write-Step 'Starting mosquitto and homeassistant'
    Invoke-Compose up -d mosquitto homeassistant
    $haUp = Wait-HaApi -TimeoutSeconds 300
    if (-not $haUp.Ok) { throw 'Home Assistant did not answer on 127.0.0.1:8123 within 300 s' }
    Write-Step "Home Assistant is up after $($haUp.Seconds) s; onboarding"

    # Random one-off owner password: this HA lives until the next run and only on 127.0.0.1.
    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $sensitive.Add($password)
    $user = Invoke-RestMethod -Method Post -Uri "$HaUrl/api/onboarding/users" -ContentType 'application/json' -Body (@{
            client_id = $OAuthClientId; name = 'Switchboard E2E'; username = 'e2e'; password = $password; language = 'en'
        } | ConvertTo-Json)
    $tokens = Invoke-RestMethod -Method Post -Uri "$HaUrl/auth/token" -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'authorization_code'; code = $user.auth_code; client_id = $OAuthClientId
    }
    $sensitive.Add($tokens.access_token)
    $sensitive.Add($tokens.refresh_token)
    $onboardHeaders = @{ Authorization = "Bearer $($tokens.access_token)" }
    Invoke-RestMethod -Method Post -Uri "$HaUrl/api/onboarding/core_config" -Headers $onboardHeaders -ContentType 'application/json' -Body '{}' | Out-Null
    Invoke-RestMethod -Method Post -Uri "$HaUrl/api/onboarding/analytics" -Headers $onboardHeaders -ContentType 'application/json' -Body '{}' | Out-Null
    Invoke-RestMethod -Method Post -Uri "$HaUrl/api/onboarding/integration" -Headers $onboardHeaders -ContentType 'application/json' -Body (@{
            client_id = $OAuthClientId; redirect_uri = "$HaUrl/?auth_callback=1"
        } | ConvertTo-Json) | Out-Null
    $pending = @((Invoke-RestMethod -Uri "$HaUrl/api/onboarding") | Where-Object { -not $_.done })
    if ($pending.Count -gt 0) { throw "onboarding steps still pending: $($pending.step -join ', ')" }

    $longLived = Invoke-HaWs -Token $tokens.access_token -Command @{
        type = 'auth/long_lived_access_token'; client_name = 'switchboard-e2e'; lifespan = 1
    }
    $sensitive.Add($longLived)
    $script:haHeaders = @{ Authorization = "Bearer $longLived" }
    $userId = (Invoke-HaWs -Token $longLived -Command @{ type = 'auth/current_user' }).id
    Write-Step "Onboarded; owner user id $userId; long-lived token minted"

    Write-Step 'Adding the MQTT integration (mosquitto:1883)'
    $flow = Invoke-HaPost '/api/config/config_entries/flow' @{ handler = 'mqtt'; show_advanced_options = $false }
    for ($stepCount = 0; $flow.type -ne 'create_entry'; $stepCount++) {
        if ($stepCount -ge 4) { throw "MQTT config flow did not finish: $($flow | ConvertTo-Json -Compress -Depth 8)" }
        $answer = switch ($flow.type) {
            'menu' { @{ next_step_id = 'broker' } }
            'form' {
                if ($flow.step_id -ne 'broker') { throw "unexpected MQTT flow step '$($flow.step_id)'" }
                if ($flow.errors -and @($flow.errors.PSObject.Properties).Count -gt 0) {
                    throw "MQTT broker step rejected the settings: $($flow.errors | ConvertTo-Json -Compress)"
                }
                # Start from the form's own defaults (the "other_settings" section is required as a
                # whole), then set the broker and answer the two required fields that have no default:
                # plain TCP, no client certificate, no CA certificate.
                $form = @{}
                foreach ($field in $flow.data_schema) {
                    if ($field.type -eq 'expandable') {
                        $section = @{}
                        foreach ($inner in $field.schema) {
                            if ($inner.PSObject.Properties.Name -contains 'default') { $section[$inner.name] = $inner.default }
                        }
                        $form[$field.name] = $section
                    }
                    elseif ($field.PSObject.Properties.Name -contains 'default') { $form[$field.name] = $field.default }
                }
                $form.broker = 'mosquitto'
                $form.port = 1883
                if ($form.ContainsKey('other_settings')) {
                    $form.other_settings.set_client_cert = $false
                    $form.other_settings.set_ca_cert = 'off'
                }
                $form
            }
            default { throw "unexpected MQTT flow result '$($flow.type)': $($flow | ConvertTo-Json -Compress -Depth 8)" }
        }
        try { $flow = Invoke-HaPost "/api/config/config_entries/flow/$($flow.flow_id)" $answer }
        catch { throw "$($_.Exception.Message); form was $($flow.data_schema | ConvertTo-Json -Compress -Depth 8)" }
    }
    Write-Step "MQTT integration created (entry '$($flow.title)')"

    # E12 watcher: every call_service event, and any state change attributed to the owner user
    # (the user behind switchboard's token), for the whole run. Reconnects across the HA restart.
    $watch = [hashtable]::Synchronized(@{
            Cts      = [Threading.CancellationTokenSource]::new()
            Calls    = [Collections.Concurrent.ConcurrentQueue[string]]::new()
            Writes   = [Collections.Concurrent.ConcurrentQueue[string]]::new()
            Errors   = [Collections.Concurrent.ConcurrentQueue[string]]::new()
            Sessions = 0
        })
    $watcherJob = Start-ThreadJob -InitializationScript $WsHelpers -ArgumentList $HaWsUrl, $longLived, $userId, $watch -ScriptBlock {
        param($Url, $Token, $UserId, $Watch)
        $ct = $Watch.Cts.Token
        while (-not $ct.IsCancellationRequested) {
            $ws = $null
            try {
                $ws = Connect-HaWebSocket -Uri $Url -Token $Token -CancellationToken $ct
                Send-WsJson -Ws $ws -Message @{ id = 1; type = 'subscribe_events'; event_type = 'call_service' }
                Send-WsJson -Ws $ws -Message @{ id = 2; type = 'subscribe_events'; event_type = 'state_changed' }
                $acks = 0
                while (-not $ct.IsCancellationRequested) {
                    $message = Receive-WsMessage -Ws $ws -CancellationToken $ct
                    if ($null -eq $message) { break }
                    if ($message.type -eq 'result') {
                        if ($message.success) { $acks++; if ($acks -eq 2) { $Watch.Sessions++ } }
                        else { $Watch.Errors.Enqueue("subscribe $($message.id) failed") }
                        continue
                    }
                    if ($message.type -ne 'event') { continue }
                    $evt = $message.event
                    $by = $evt.context.user_id
                    if ($evt.event_type -eq 'call_service') {
                        $Watch.Calls.Enqueue("$($evt.data.domain).$($evt.data.service) user=$by")
                    }
                    elseif ($evt.event_type -eq 'state_changed' -and $by -eq $UserId) {
                        $Watch.Writes.Enqueue("$($evt.data.entity_id) -> $($evt.data.new_state.state)")
                    }
                }
            }
            catch {
                if (-not $ct.IsCancellationRequested) { $Watch.Errors.Enqueue($_.Exception.Message) }
            }
            finally {
                if ($ws) { $ws.Dispose() }
            }
            if (-not $ct.IsCancellationRequested) { $ct.WaitHandle.WaitOne(2000) | Out-Null }
        }
    }
    $watchUp = Wait-Until -TimeoutSeconds 30 -Condition { $watch.Sessions -ge 1 }
    if (-not $watchUp.Ok) { throw "E12 watcher did not subscribe: $(@($watch.Errors) -join '; ')" }

    Write-Step 'Starting switchboard (log level debug)'
    $env:SWITCHBOARD_HA_TOKEN = $longLived
    $env:SWITCHBOARD_LOG_LEVEL = 'debug'
    Invoke-Compose up -d --no-deps switchboard
    $switchboard = Get-ContainerId 'switchboard'
    $switchboardIps = [Collections.Generic.HashSet[string]]::new()
    $switchboardIps.Add((Get-ContainerIp $switchboard)) | Out-Null

    Invoke-Check 'E1' 'health entity exists and is on' {
        $wait = Wait-EntityState -State 'on' -TimeoutSeconds 90
        @{ Passed = $wait.Ok; Evidence = "$EntityId state '$(Get-EntityState)' after $($wait.Seconds) s" }
    }

    Invoke-Check 'E2' 'registry identity, no MQTT warnings for our discovery' {
        $entry = Invoke-HaWs -Token $longLived -Command @{ type = 'config/entity_registry/get'; entity_id = $EntityId }
        $haLog = Get-ComposeLog 'homeassistant'
        $warnings = @($haLog -split '\r?\n' | Where-Object {
                $_ -match '\b(WARNING|ERROR|CRITICAL)\b' -and
                ($_ -match '\[homeassistant\.components\.mqtt' -or $_ -match '(?i)switchboard')
            })
        $ok = $entry.unique_id -eq $ExpectedUniqueId -and $entry.entity_id -eq $EntityId -and
            $entry.platform -eq 'mqtt' -and $warnings.Count -eq 0
        @{
            Passed   = $ok
            Evidence = "entity_id=$($entry.entity_id) unique_id=$($entry.unique_id) platform=$($entry.platform) " +
            "original_name=$($entry.original_name) has_entity_name=$($entry.has_entity_name); " +
            "HA MQTT/switchboard warnings: $($warnings.Count)" + ($warnings.Count ? " -> $($warnings -join ' | ')" : '')
        }
    }

    Invoke-Check 'E3' 'status API and SPA; HA link survives a real ping/pong' {
        $wait = Wait-Until -TimeoutSeconds 30 -Condition {
            try { $s = Get-Status; $s.homeAssistant -eq 'connected' -and $s.mqtt -eq 'connected' -and $s.healthy } catch { $false }
        }
        $response = Invoke-WebRequest -Uri "$SwitchboardUrl/api/status" -TimeoutSec 5
        $status = $response.Content | ConvertFrom-Json
        $names = ($status.PSObject.Properties.Name | Sort-Object) -join ','
        $statusOk = $wait.Ok -and $names -eq 'healthy,homeAssistant,mode,mqtt,version' -and
            $status.version -eq $ExpectedVersion -and $status.mode -eq 'shadow' -and
            $status.homeAssistant -eq 'connected' -and $status.mqtt -eq 'connected' -and $status.healthy -eq $true -and
            $response.Headers['Content-Type'] -match '^application/json'
        $page = Invoke-WebRequest -Uri "$SwitchboardUrl/" -TimeoutSec 5
        $asset = [regex]::Match($page.Content, 'src="\./(assets/index-[^"]+\.js)"')
        $assetStatus = if ($asset.Success) { (Invoke-WebRequest -Uri "$SwitchboardUrl/$($asset.Groups[1].Value)" -TimeoutSec 5).StatusCode } else { 'none' }
        $pageOk = $page.StatusCode -eq 200 -and $page.Headers['Content-Type'] -match '^text/html' -and
            $page.Content.Contains('<div id="app"></div>') -and $page.Content.Contains('<title>Switchboard</title>') -and $assetStatus -eq 200
        # The scripted-transport tests cannot prove that real HA's pong satisfies the client: wait
        # for the first keepalive ping in HA's websocket log, then give the pong its deadline. A
        # rejected pong would show as a keepalive warning and a second connection.
        $ip = [regex]::Escape((Get-ContainerIp $switchboard))
        $ping = Wait-Until -TimeoutSeconds ($PingInterval.TotalSeconds + 15) -IntervalMilliseconds 1000 -Condition {
            (Get-ComposeLog 'homeassistant') -match "from $ip[^:]*: Received \{[^}]*'type': 'ping'"
        }
        if ($ping.Ok) { Start-Sleep -Seconds ($PongTimeout.TotalSeconds + 3) }
        $sbLog = Get-ComposeLog 'switchboard'
        $keepaliveWarning = $sbLog -match 'did not answer a ping'
        $haSessions = ([regex]::Matches($sbLog, 'Connected to Home Assistant at')).Count
        $after = Get-Status
        $keepaliveOk = $ping.Ok -and -not $keepaliveWarning -and $haSessions -eq 1 -and $after.homeAssistant -eq 'connected'
        @{
            Passed   = $statusOk -and $pageOk -and $keepaliveOk
            Evidence = "api/status $($response.StatusCode) $($response.Headers['Content-Type']) $($response.Content); " +
            "GET / $($page.StatusCode) $($page.Headers['Content-Type']) index.html with #app, script $($asset.Groups[1].Value) -> $assetStatus; " +
            "first ping in HA log after $($ping.Seconds) s (PingInterval $($PingInterval.TotalSeconds) s), " +
            "$($PongTimeout.TotalSeconds + 3) s later: keepalive warning $keepaliveWarning, HA sessions $haSessions, homeAssistant=$($after.homeAssistant)"
        }
    }

    Invoke-Check 'E4' 'only 8099 listens; outbound only to HA and broker' {
        $haIp = Get-ContainerIp (Get-ContainerId 'homeassistant')
        $brokerIp = Get-ContainerIp (Get-ContainerId 'mosquitto')
        $held = Get-ContainerSocketInodes $switchboard
        $tcp = @(Get-ContainerSockets $switchboard 'tcp')
        $udp = @(Get-ContainerSockets $switchboard 'udp')
        # TCP LISTEN, and UDP sockets bound to a port without a peer: everything that accepts traffic.
        $listening = @(@($tcp | Where-Object State -eq '0A') + @($udp | Where-Object { $_.RemotePort -eq 0 -and $_.LocalPort -ne 0 }))
        $ours = @($listening | Where-Object { $held.Contains($_.Inode) })
        $notOurs = @($listening | Where-Object { -not $held.Contains($_.Inode) })
        $dockerDns = @($notOurs | Where-Object LocalAddress -eq '127.0.0.11')
        $unexplained = @($notOurs | Where-Object LocalAddress -ne '127.0.0.11')
        $outbound = @($tcp | Where-Object { $_.State -in '01', '02' -and $_.LocalPort -ne 8099 })
        $allowed = @("${haIp}:8123", "${brokerIp}:1883")
        $stray = @($outbound | Where-Object { $_.Remote -notin $allowed })
        $published = (& docker port $switchboard) -join ', '
        $ok = $held.Count -gt 0 -and $ours.Count -ge 1 -and
            @($ours | Where-Object { $_.Protocol -ne 'tcp' -or $_.LocalPort -ne 8099 }).Count -eq 0 -and
            $unexplained.Count -eq 0 -and $stray.Count -eq 0 -and
            @($outbound | Where-Object Remote -eq "${haIp}:8123").Count -ge 1 -and
            @($outbound | Where-Object Remote -eq "${brokerIp}:1883").Count -ge 1 -and
            $published -eq '8099/tcp -> 127.0.0.1:8099'
        @{
            Passed   = $ok
            Evidence = "listening in the container: $(($ours | ForEach-Object { "$($_.Protocol) $($_.Local)" }) -join ', '); " +
            "not held by any container process: Docker DNS $(($dockerDns | ForEach-Object { "$($_.Protocol) $($_.Local)" }) -join ', '), other $($unexplained.Count); " +
            "outbound: $(($outbound | ForEach-Object { "$($_.Remote)" }) -join ', ') (HA $haIp, broker $brokerIp); " +
            "stray: $($stray.Count); docker port: $published"
        }
    }

    Invoke-Check 'E5' 'ingress guard refuses a non-ingress peer' {
        $network = Get-ContainerField $switchboard '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}}{{end}}'
        Remove-GuardContainer
        # Same image, dev flag NOT set, no HA or broker settings: it serves the web surface only.
        & docker run -d --name $GuardContainer --network $network switchboard:dev | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'could not start the guard container' }
        $ready = Wait-Until -TimeoutSeconds 60 -Condition {
            (& docker exec $GuardContainer curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8099/api/status 2>$null) -eq '200'
        }
        $fromPeer = Test-ContainerHttp 'mosquitto' "http://${GuardContainer}:8099/api/status"
        $fromPeerRoot = Test-ContainerHttp 'mosquitto' "http://${GuardContainer}:8099/"
        $control = Test-ContainerHttp 'mosquitto' 'http://switchboard:8099/api/status'
        $guardLog = (& docker logs $GuardContainer 2>&1 | Out-String)
        $script:guardLog = $guardLog
        $flagWarned = $guardLog -match 'SWITCHBOARD_DEV_TRUST_ALL_REQUESTS is set'
        Remove-GuardContainer
        @{
            Passed   = $ready.Ok -and $fromPeer -eq 403 -and $fromPeerRoot -eq 403 -and $control -eq 200 -and -not $flagWarned
            Evidence = "guard container loopback 200 after $($ready.Seconds) s; from mosquitto container: api/status $fromPeer, / $fromPeerRoot; " +
            "control (dev compose instance, flag on) $control; dev-flag warning in guard log: $flagWarned"
        }
    }

    Invoke-Check 'E6' 'graceful stop -> unavailable' {
        $before = Get-EntityState
        $stopClock = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Compose stop switchboard
        $stopSeconds = [math]::Round($stopClock.Elapsed.TotalSeconds, 1)
        $wait = Wait-EntityState -State 'unavailable' -TimeoutSeconds 15
        $exitCode = Get-ContainerField $switchboard '{{.State.ExitCode}}'
        $graceful = (Get-ComposeLog 'switchboard') -match 'MQTT session closed; health entity left OFF and offline'
        @{
            Passed   = $before -eq 'on' -and $wait.Ok -and $graceful -and $exitCode -eq '0'
            Evidence = "state before '$before'; docker compose stop took $stopSeconds s, exit code $exitCode; " +
            "'unavailable' $($wait.Seconds) s after the stop returned; shutdown sequence logged: $graceful"
        }
    }

    Invoke-Check 'E7' 'start -> on; SIGKILL -> unavailable via last will' {
        Invoke-Compose start switchboard
        $on = Wait-EntityState -State 'on' -TimeoutSeconds 90
        $switchboardIps.Add((Get-ContainerIp $switchboard)) | Out-Null
        $gracefulBefore = ([regex]::Matches((Get-ComposeLog 'switchboard'), 'MQTT session closed')).Count
        # A connection that ends without DISCONNECT, which is what makes mosquitto publish the will.
        $dropPattern = 'Client switchboard closed its connection'
        $dropsBefore = ([regex]::Matches((Get-ComposeLog 'mosquitto'), $dropPattern)).Count
        # The last beat was at most one Interval before the kill, so expire_after cannot turn the
        # entity unavailable sooner than ExpireAfter - Interval after it: inside this bound only
        # the last will can have done it.
        $willBound = $ExpireAfter.TotalSeconds - $HeartbeatInterval.TotalSeconds - 5
        & docker kill --signal KILL $switchboard | Out-Null
        $gone = Wait-EntityState -State 'unavailable' -TimeoutSeconds $willBound
        $gracefulAfter = ([regex]::Matches((Get-ComposeLog 'switchboard'), 'MQTT session closed')).Count
        $dropsAfter = ([regex]::Matches((Get-ComposeLog 'mosquitto'), $dropPattern)).Count
        @{
            Passed   = $on.Ok -and $gone.Ok -and $gracefulAfter -eq $gracefulBefore -and $dropsAfter -gt $dropsBefore
            Evidence = "on $($on.Seconds) s after start; 'unavailable' $($gone.Seconds) s after SIGKILL (bound $willBound s = ExpireAfter - Interval - 5 s, before any expiry); " +
            "no shutdown sequence ran (graceful lines $gracefulBefore -> $gracefulAfter); broker logged the connection closing without DISCONNECT: $dropsBefore -> $dropsAfter"
        }
    }

    Invoke-Check 'E8' 'hang (docker pause) -> unavailable; unpause -> on' {
        Invoke-Compose start switchboard
        $on = Wait-EntityState -State 'on' -TimeoutSeconds 90
        $switchboardIps.Add((Get-ContainerIp $switchboard)) | Out-Null
        $bound = $ExpireAfter.TotalSeconds + 15
        & docker pause $switchboard | Out-Null
        $gone = Wait-EntityState -State 'unavailable' -TimeoutSeconds $bound
        $keepaliveDrop = (Get-ComposeLog 'mosquitto') -match 'Client switchboard has exceeded timeout'
        & docker unpause $switchboard | Out-Null
        $back = Wait-EntityState -State 'on' -TimeoutSeconds 90
        @{
            Passed   = $on.Ok -and $gone.Ok -and $back.Ok
            Evidence = "on $($on.Seconds) s after start; 'unavailable' $($gone.Seconds) s after pause (bound $bound s = ExpireAfter $($ExpireAfter.TotalSeconds) s + 15 s); " +
            "broker keep-alive drop logged meanwhile: $keepaliveDrop; 'on' again $($back.Seconds) s after unpause"
        }
    }

    Invoke-Check 'E9' 'HA restart -> on again, switchboard untouched' {
        $startedAt = Get-ContainerField $switchboard '{{.State.StartedAt}}'
        Invoke-Compose restart homeassistant
        $api = Wait-HaApi -TimeoutSeconds 240 -Authenticated
        $back = Wait-EntityState -State 'on' -TimeoutSeconds 180
        $status = Get-Status
        $same = (Get-ContainerField $switchboard '{{.State.StartedAt}}') -eq $startedAt
        $republished = (Get-ComposeLog 'switchboard') -match 'Home Assistant came online; republishing discovery'
        @{
            Passed   = $api.Ok -and $back.Ok -and $same -and $status.homeAssistant -eq 'connected' -and $republished
            Evidence = "HA API back after $($api.Seconds) s; entity 'on' $($back.Seconds) s later; switchboard not restarted: $same; " +
            "birth-triggered republish logged: $republished; status homeAssistant=$($status.homeAssistant) healthy=$($status.healthy)"
        }
    }

    Invoke-Check 'E10' 'broker restart -> on again, switchboard untouched' {
        $startedAt = Get-ContainerField $switchboard '{{.State.StartedAt}}'
        $connectsBefore = ([regex]::Matches((Get-ComposeLog 'switchboard'), 'Connected to the MQTT broker')).Count
        Invoke-Compose restart mosquitto
        $reconnect = Wait-Until -TimeoutSeconds 120 -Condition {
            ([regex]::Matches((Get-ComposeLog 'switchboard'), 'Connected to the MQTT broker')).Count -gt $connectsBefore
        }
        $back = Wait-EntityState -State 'on' -TimeoutSeconds 60
        $status = Get-Status
        $same = (Get-ContainerField $switchboard '{{.State.StartedAt}}') -eq $startedAt
        @{
            Passed   = $reconnect.Ok -and $back.Ok -and $same -and $status.mqtt -eq 'connected' -and $status.healthy
            Evidence = "switchboard reconnected to the broker $($reconnect.Seconds) s after the restart; entity 'on' $($back.Seconds) s later; " +
            "switchboard not restarted: $same; status mqtt=$($status.mqtt) healthy=$($status.healthy)"
        }
    }

    # Late MQTT deprecation warnings (e.g. on the republish after the HA restart) count against E2.
    $haLogFinal = Get-ComposeLog 'homeassistant'
    $deprecations = @($haLogFinal -split '\r?\n' | Where-Object { $_ -match '(?i)deprecat' -and $_ -match '(?i)mqtt|switchboard' })
    if ($deprecations.Count -gt 0) {
        $e2 = $results | Where-Object Id -eq 'E2'
        $e2.Passed = $false
        $e2.Evidence += "; deprecation lines in the full HA log: $($deprecations -join ' | ')"
    }

    Invoke-Check 'E11' 'switchboard log: no secrets, no stack trace, mode shadow' {
        $devLog = Get-ComposeLog 'switchboard'
        # The E5 guard instance is started without HA and broker settings on purpose: its two
        # "Standalone mode needs ..." errors are expected, so it is scanned for secrets and stack
        # traces but not for error lines.
        $log = $devLog + "`n" + [string]$script:guardLog
        $leaks = @($sensitive | Where-Object { $_ -and $log.Contains($_) })
        $stackLines = @($log -split '\r?\n' | Where-Object { $_ -match '^\s+at \S+\(' -or $_ -match '--- End of (inner exception )?stack trace' -or $_ -match 'Unhandled exception' })
        $severe = @($devLog -split '\r?\n' | Where-Object { $_ -match '\b(fail|crit): ' })
        $startup = @($log -split '\r?\n' | Where-Object { $_ -match 'Switchboard\.Startup' -and $_ -match 'starting as' })
        $shadow = $startup.Count -ge 1 -and @($startup | Where-Object { $_ -notmatch '(?i)mode shadow' }).Count -eq 0
        $debugOn = $log -match 'dbug: '
        @{
            Passed   = $leaks.Count -eq 0 -and $stackLines.Count -eq 0 -and $severe.Count -eq 0 -and $shadow -and $debugOn
            Evidence = "secrets found: $($leaks.Count) (checked $($sensitive.Count): owner password, access/refresh token, long-lived token); " +
            "stack-trace lines: $($stackLines.Count); fail/crit lines: $($severe.Count)" + ($severe.Count ? " -> $($severe -join ' | ')" : '') +
            "; startup lines: $($startup.Count), all 'mode Shadow': $shadow; debug level active: $debugOn; " +
            "first startup line: $(($startup | Select-Object -First 1) -replace '^\S+ \S+ ', '')"
        }
    }

    Invoke-Check 'E12' 'switchboard never called a service or wrote a state' {
        $watch.Cts.Cancel()
        $null = Wait-Job $watcherJob -Timeout 10
        $calls = @($watch.Calls)
        $ownCalls = @($calls | Where-Object { $_ -like "*user=$userId" })
        $writes = @($watch.Writes)

        # Every frame each websocket peer sent, as HA logged it: "[connid] <user> from <ip> (...): Received {...}".
        $haLog = Get-ComposeLog 'homeassistant'
        $sent = @{}
        foreach ($line in $haLog -split '\r?\n') {
            $m = [regex]::Match($line, '\[(\d+)\] (?:.*? )?from (\d+\.\d+\.\d+\.\d+)[^:]*: Received (.*)$')
            if (-not $m.Success -or -not $switchboardIps.Contains($m.Groups[2].Value)) { continue }
            foreach ($t in [regex]::Matches($m.Groups[3].Value, "'type': '([^']+)'")) {
                $type = $t.Groups[1].Value
                $sent[$type] = 1 + ($sent[$type] ?? 0)
            }
        }
        $connections = @([regex]::Matches($haLog, '\[(\d+)\] (?:.*? )?from (\d+\.\d+\.\d+\.\d+)[^:]*: Received ') |
            Where-Object { $switchboardIps.Contains($_.Groups[2].Value) } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $foreign = @($sent.Keys | Where-Object { $_ -notin 'auth', 'ping' })

        $since = [Uri]::EscapeDataString((Get-Date).ToUniversalTime().AddHours(-1).ToString('yyyy-MM-ddTHH:mm:ssZ'))
        $logbook = @(Invoke-RestMethod -Uri "$HaUrl/api/logbook/$since" -Headers $script:haHeaders -TimeoutSec 10)
        $ownLogbook = @($logbook | Where-Object { $_.PSObject.Properties.Name -contains 'context_user_id' -and $_.context_user_id -eq $userId })

        $sentText = ($sent.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '
        @{
            Passed   = $watch.Sessions -ge 2 -and $ownCalls.Count -eq 0 -and $writes.Count -eq 0 -and
            $connections.Count -ge 1 -and $foreign.Count -eq 0 -and $sent['auth'] -ge 1 -and $ownLogbook.Count -eq 0
            Evidence = "event watcher sessions $($watch.Sessions) (reconnected across the HA restart); call_service events seen $($calls.Count), " +
            "by the switchboard user $($ownCalls.Count); state changes by that user $($writes.Count); " +
            "HA websocket log, $($connections.Count) switchboard connections from $(@($switchboardIps) -join '/'): sent $sentText; " +
            "other types: $($foreign.Count); logbook entries by that user: $($ownLogbook.Count)"
        }
    }

    Invoke-Check 'E13' 'crash at start -> fatal verdict, container halts with the signal code' {
        Remove-CrashContainer
        # An expiry shorter than two intervals fails options validation at start; the unhandled
        # exception makes the .NET runtime abort (SIGABRT), the way any crash ends.
        & docker run -d --name $CrashContainer -e Switchboard__Heartbeat__ExpireAfter=00:00:01 switchboard:dev | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'could not start the crash container' }
        $stopped = Wait-Until -TimeoutSeconds 60 -Condition { (Get-ContainerField $CrashContainer '{{.State.Running}}') -eq 'false' }
        $exitCode = Get-ContainerField $CrashContainer '{{.State.ExitCode}}'
        $crashLog = (& docker logs $CrashContainer 2>&1 | Out-String)
        Remove-CrashContainer
        $validation = $crashLog -match 'OptionsValidationException'
        $fatal = $crashLog -match 'Switchboard was killed by signal 6; halting the container'
        $benign = $crashLog -match 'Switchboard stopped by signal'
        @{
            Passed   = $stopped.Ok -and $validation -and $fatal -and -not $benign -and $exitCode -eq '134'
            Evidence = "container stopped after $($stopped.Seconds) s with exit code $exitCode (128 + SIGABRT = 134); " +
            "options validation failure logged: $validation; fatal verdict from finish: $fatal; benign 'stopped by signal' line: $benign"
        }
    }
}
catch {
    Write-Host "Setup failed at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)" -ForegroundColor Red
    $results.Add([pscustomobject]@{ Id = 'SETUP'; Name = 'stack setup'; Passed = $false; Seconds = 0; Evidence = $_.Exception.Message })
}
finally {
    if ($watch) { $watch.Cts.Cancel() }
    if ($watcherJob) { Remove-Job $watcherJob -Force -ErrorAction SilentlyContinue }
    Remove-GuardContainer
    Remove-CrashContainer
    Remove-Item Env:SWITCHBOARD_HA_TOKEN -ErrorAction SilentlyContinue
    if ($KeepUp) {
        Write-Step 'Leaving the stack up (-KeepUp): docker compose -f deploy/docker-compose.dev.yml down'
    }
    else {
        Write-Step 'Bringing the stack down (images kept)'
        & docker compose -f $ComposeFile down --remove-orphans
    }
}

Write-Host ''
Write-Host 'Stage 0 e2e results'
$results | Format-Table -AutoSize -Wrap Id, @{ n = 'Result'; e = { $_.Passed ? 'PASS' : 'FAIL' } }, Seconds, Name | Out-Host
$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ("{0} of {1} checks passed; total wall time {2:0} s" -f ($results.Count - $failed.Count), $results.Count, $runClock.Elapsed.TotalSeconds)
if ($failed.Count -gt 0 -or $results.Count -lt 13) { exit 1 }
exit 0
