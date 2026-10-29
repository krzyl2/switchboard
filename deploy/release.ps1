#!/usr/bin/env pwsh
#Requires -Version 7.2
<#
.SYNOPSIS
    One-command Switchboard release: bump, verify, commit, push (krzyl2), image, deploy to the live HA, post-check.

.DESCRIPTION
    Release by script, not by improvisation. Stages run in order, print one PASS/FAIL line each and stop at
    the first failure (exit code 10 + stage number). Stages: preflight, bump, verify, commit, push, image,
    deploy, postcheck. Every stage is idempotent, so after a failure fix the cause and re-run (-From <stage>).

    NEVER touched: add-on options, mode, the control switch, the live switchboard.yaml, any switch.grzalka_*,
    number.goe_* or select.goe_* entity. The post-check only READS states. The HA token is read from the
    DPAPI file %LOCALAPPDATA%\Switchboard\ha-token.dpapi (deploy/set-ha-token.ps1); the env var
    SWITCHBOARD_HA_TOKEN works only as a CI-style fallback when the file is missing. The token reaches
    ha-supervisor.mjs over stdin, is never printed and never written to a file.

    One-time setup: docs/RELEASE.md.

.PARAMETER Version
    Target version x.y.z; must be a strict bump of the manifest version (or the version already being released).
.PARAMETER Notes
    CHANGELOG body for the new version: a path to a file, or the text itself. `### Fixed` / `### Added` headings
    are kept; plain bullets get a `### Changed` heading.
.PARAMETER Subject
    Commit subject WITHOUT the version prefix; the commit subject becomes "<Version>: <Subject>" (<= 72 chars).
.PARAMETER Stage
    Run only these stages (by name).
.PARAMETER From
    First stage to run.
.PARAMETER To
    Last stage to run.
.PARAMETER DryRun
    Runs preflight and verify for real; prints what the other stages would do. Mutates nothing, contacts no HA.
.PARAMETER Check
    Alias for preflight only.
.PARAMETER SelfTest
    Tests the script's pure functions against deploy/tests fixtures and the current tree (read-only); exits.
.PARAMETER Rebuild
    Build and push the image even when the tag already exists in GHCR.
.PARAMETER CoAuthor
    Full trailer line for the commit; pass '' for none.
.PARAMETER DeployTimeoutSec
    How long the deploy stage waits for the add-on to report the target version started (default 300).

.EXAMPLE
    pwsh deploy/release.ps1 -Version 0.11.7 -Subject 'Plan chart fixes' -Notes notes.md -DryRun
    pwsh deploy/release.ps1 -Version 0.11.7 -Subject 'Plan chart fixes' -Notes notes.md
    pwsh deploy/release.ps1 -Version 0.11.7 -Subject 'Plan chart fixes' -From image   # resume
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Notes,
    [string]$Subject,
    [ValidateSet('preflight', 'bump', 'verify', 'commit', 'push', 'image', 'deploy', 'postcheck')]
    [string[]]$Stage,
    [ValidateSet('preflight', 'bump', 'verify', 'commit', 'push', 'image', 'deploy', 'postcheck')]
    [string]$From,
    [ValidateSet('preflight', 'bump', 'verify', 'commit', 'push', 'image', 'deploy', 'postcheck')]
    [string]$To,
    [switch]$DryRun,
    [switch]$Check,
    [switch]$SelfTest,
    [switch]$Rebuild,
    [string]$CoAuthor = 'Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>',
    [int]$DeployTimeoutSec = 300,
    [string]$HaUrl,
    [string]$Image = 'ghcr.io/krzyl2/switchboard',
    [string]$Builder = 'switchboard-builder',
    [string]$GitHubAccount = 'krzyl2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

. (Join-Path $PSScriptRoot 'ha-token.ps1')
if (-not $HaUrl) { $HaUrl = Resolve-HaUrl }
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $PSScriptRoot 'out'
$fixtureDir = Join-Path $PSScriptRoot 'tests'
$addonSlug = 'fb9ed8c0_switchboard'

# Where the version lives. Each regex has exactly three groups (prefix, version, suffix) and must match once.
$versionPlaces = @(
    @{ Path = 'switchboard/config.yaml'; Regex = '(?m)^(version:\s*")(\d+\.\d+\.\d+)(")' }
    @{ Path = 'Directory.Build.props'; Regex = '(<Version>)(\d+\.\d+\.\d+)(</Version>)' }
    @{ Path = 'tests/Switchboard.Tests/Host/WebSurfaceTests.cs'; Regex = '(Assert\.Equal\(")(\d+\.\d+\.\d+)("\s*,\s*json\.RootElement\.GetProperty\("version"\))' }
)
# Tracked files that may mention the old version without being a missed place.
$versionAllowedElsewhere = '^(switchboard/CHANGELOG\.md|web/package-lock\.json)$'
# Untracked-or-modified paths that must never be staged by the release.
$neverStage = '^src/Switchboard\.Host/Properties/'
$forbiddenStage = '(^|/)(\.env[^/]*|[^/]*\.(pem|key|pfx|token|log)|bin/.*|obj/.*|node_modules/.*|out/.*|\.claude/worktrees/.*|\.playwright-mcp/.*)$'
$allowedRoots = '^(src|web|switchboard|deploy|tests|docs|\.claude)/|^(\.gitignore|\.gitattributes|CLAUDE\.md|README\.md|Directory\.Build\.props|Directory\.Packages\.props|Switchboard\.slnx|global\.json|BannedSymbols\.txt|repository\.yaml|LICENSE)$'
$callServiceAllowed = 'src/Switchboard\.Infrastructure/HomeAssistant/HomeAssistantCommand'

$stages = @('preflight', 'bump', 'verify', 'commit', 'push', 'image', 'deploy', 'postcheck')
$exitBase = 10

# ---------------------------------------------------------------- pure functions (covered by -SelfTest)

function Compare-StrictBump {
    # $true when $New is strictly greater than $Old (both x.y.z).
    param([string]$Old, [string]$New)
    return ([version]$New) -gt ([version]$Old)
}

function Set-VersionInText {
    # Replaces the version captured by $Regex (3 groups) with $New. Throws unless exactly one match.
    param([string]$Text, [string]$Regex, [string]$New, [string]$Where = 'text')
    $m = [regex]::Matches($Text, $Regex)
    if ($m.Count -ne 1) { throw "$Where : expected exactly 1 version match, found $($m.Count)" }
    return [regex]::Replace($Text, $Regex, ('${1}' + $New + '${3}'))
}

function Get-VersionFromText {
    param([string]$Text, [string]$Regex, [string]$Where = 'text')
    $m = [regex]::Matches($Text, $Regex)
    if ($m.Count -ne 1) { throw "$Where : expected exactly 1 version match, found $($m.Count)" }
    return $m[0].Groups[2].Value
}

function ConvertTo-ChangelogBody {
    # Normalises caller notes: keeps '### X' headings, bullets get '- ', loose bullets get a '### Changed' heading.
    param([string]$Notes)
    $lines = @(($Notes -replace "`r", '') -split "`n" | ForEach-Object { $_.TrimEnd() })
    while ($lines.Count -gt 0 -and $lines[0] -eq '') { $lines = $lines[1..($lines.Count - 1)] }
    while ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines = $lines[0..($lines.Count - 2)] }
    if ($lines.Count -eq 0) { throw 'Notes are empty' }
    $body = @(foreach ($l in $lines) {
        if ($l -eq '' -or $l -match '^(#|- |\s+- |\s{2,}\S)') { $l }
        elseif ($l -match '^[*]\s+(.*)$') { "- $($Matches[1])" }
        else { $l }
    })
    # Bare text lines (no bullet, no heading, not blank, not the closing sentence after a blank) become bullets
    # only when the notes have no heading at all; otherwise they are kept verbatim as prose.
    $hasHeading = [bool]($body | Where-Object { $_ -match '^###\s' })
    if (-not $hasHeading) {
        $body = @($body | ForEach-Object { if ($_ -ne '' -and $_ -notmatch '^(- |\s)') { "- $_" } else { $_ } })
        $body = @('### Changed', '') + $body
    }
    elseif ($body[0] -notmatch '^###\s') { throw 'Notes with headings must start with a ### heading' }
    # A blank line after each heading, as in the existing entries.
    $result = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $body.Count; $i++) {
        $result.Add($body[$i])
        if ($body[$i] -match '^###\s' -and $i + 1 -lt $body.Count -and $body[$i + 1] -ne '') { $result.Add('') }
    }
    return ($result -join "`n")
}

function Add-ChangelogEntry {
    # Inserts '## <Version>' + body before the first '## ' heading. Idempotent: an existing entry is kept.
    param([string]$Text, [string]$Version, [string]$Body)
    $nl = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    if ($Text -match "(?m)^## $([regex]::Escape($Version))\s*$") { return $Text }
    $m = [regex]::Match($Text, '(?m)^## ')
    if (-not $m.Success) { throw 'CHANGELOG has no "## " entry to insert before' }
    $entry = "## $Version$nl$nl" + ($Body -replace "`n", $nl) + "$nl$nl"
    return $Text.Insert($m.Index, $entry)
}

function Read-BuildSummary {
    # dotnet build output -> @{ Warnings; Errors; Succeeded }.
    param([string[]]$Lines)
    $w = 0; $e = 0; $seen = $false
    foreach ($l in $Lines) {
        if ($l -match '^\s*(\d+) Warning\(s\)') { $w += [int]$Matches[1]; $seen = $true }
        elseif ($l -match '^\s*(\d+) Error\(s\)') { $e += [int]$Matches[1]; $seen = $true }
    }
    $ok = [bool]($Lines | Where-Object { $_ -match '^Build succeeded' })
    return @{ Warnings = $w; Errors = $e; Succeeded = ($ok -and $seen) }
}

function Read-TestSummary {
    # dotnet test output (VSTest "Passed!/Failed! - Failed: n, Passed: n, ..." or MTP "total: n" block), summed over assemblies.
    param([string[]]$Lines)
    $f = 0; $p = 0; $s = 0; $t = 0; $found = 0
    $mtp = @{}
    foreach ($l in $Lines) {
        if ($l -match '(Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)') {
            $f += [int]$Matches[2]; $p += [int]$Matches[3]; $s += [int]$Matches[4]; $t += [int]$Matches[5]; $found++
        }
        elseif ($l -match '^\s*(total|failed|succeeded|skipped):\s+(\d+)\s*$') { $mtp[$Matches[1]] = [int]$Matches[2] }
        elseif ($l -match 'Test summary:\s+total:\s+(\d+),\s+failed:\s+(\d+),\s+succeeded:\s+(\d+),\s+skipped:\s+(\d+)') {
            $t += [int]$Matches[1]; $f += [int]$Matches[2]; $p += [int]$Matches[3]; $s += [int]$Matches[4]; $found++
        }
    }
    if ($mtp.ContainsKey('total')) {
        $t += $mtp['total']; $found++
        if ($mtp.ContainsKey('failed')) { $f += $mtp['failed'] }
        if ($mtp.ContainsKey('succeeded')) { $p += $mtp['succeeded'] }
        if ($mtp.ContainsKey('skipped')) { $s += $mtp['skipped'] }
    }
    return @{ Failed = $f; Passed = $p; Skipped = $s; Total = $t; Found = $found }
}

function Test-CommitSubject {
    # Returns $null when fine, else the reason.
    param([string]$Version, [string]$Subject)
    if ([string]::IsNullOrWhiteSpace($Subject)) { return 'subject is empty' }
    if ($Subject -match '^(feat|fix|chore|docs|refactor|test|tests|ci|build|perf|style|revert)(\([^)]*\))?!?:\s') { return 'subject carries a type tag' }
    if ($Subject -match '^\d+\.\d+\.\d+') { return 'subject must not repeat the version prefix' }
    if ($Subject -match '[\r\n]') { return 'subject must be one line' }
    $full = "${Version}: $Subject"
    if ($full.Length -gt 72) { return "subject is $($full.Length) chars with the version prefix (max 72)" }
    return $null
}

function Read-ReleaseLogs {
    # Add-on log text -> @{ Started; ConfigLoaded; Signals; Problems }.
    param([string]$Text, [string]$Version)
    $clean = $Text -replace "\x1b\[[0-9;]*m", ''
    $lines = $clean -split "\r?\n"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "Switchboard $([regex]::Escape($Version)) starting") { $start = $i } }
    if ($start -lt 0) { return @{ Started = $false; ConfigLoaded = $false; Signals = $null; Problems = @() } }
    $after = $lines[$start..($lines.Count - 1)]
    $loaded = $after | Where-Object { $_ -match 'Configuration .+ loaded' } | Select-Object -First 1
    $signals = $null
    if ($loaded -and $loaded -match 'loaded:\s*(\d+) signals') { $signals = [int]$Matches[1] }
    $problems = @($after | Where-Object { $_ -match '(?i)\b(error|crit|fail(?!-?safe)\w*)\b' })
    return @{ Started = $true; ConfigLoaded = [bool]$loaded; Signals = $signals; Problems = $problems }
}

function Get-ImageDigest {
    # `docker buildx imagetools inspect` text -> top-level digest, or $null.
    param([string[]]$Lines)
    foreach ($l in $Lines) { if ($l -match '^Digest:\s*(sha256:[0-9a-f]{64})') { return $Matches[1] } }
    return $null
}

function Get-ImagePlatforms {
    param([string[]]$Lines)
    return @($Lines | ForEach-Object { if ($_ -match '^\s*Platform:\s*(linux/\S+)') { $Matches[1] } })
}

function ConvertFrom-PorcelainZ {
    # `git status --porcelain=v1 -z -uall` text -> list of @{ Xy; Path }.
    param([string]$Raw)
    $parts = @($Raw -split "`0" | Where-Object { $_ -ne '' })
    $res = @()
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $xy = $parts[$i].Substring(0, 2); $p = $parts[$i].Substring(3)
        if ($xy[0] -in 'R', 'C') { $i++ }   # next field is the rename source
        $res += @{ Xy = $xy; Path = $p }
    }
    return $res
}

# ---------------------------------------------------------------- self-test

if ($SelfTest) {
    $script:fails = 0
    function Assert-That([string]$Name, [scriptblock]$Body) {
        try { $r = & $Body; if ($r) { Write-Host "PASS  $Name" -ForegroundColor Green } else { Write-Host "FAIL  $Name" -ForegroundColor Red; $script:fails++ } }
        catch { Write-Host "FAIL  $Name : $($_.Exception.Message)" -ForegroundColor Red; $script:fails++ }
    }
    function Assert-Throws([string]$Name, [scriptblock]$Body) {
        $threw = $false; try { & $Body | Out-Null } catch { $threw = $true }
        if ($threw) { Write-Host "PASS  $Name" -ForegroundColor Green } else { Write-Host "FAIL  $Name (no exception)" -ForegroundColor Red; $script:fails++ }
    }
    function Fx([string]$n) { Get-Content -LiteralPath (Join-Path $fixtureDir $n) }

    Assert-That 'bump 0.11.6 -> 0.11.7 is strict' { Compare-StrictBump '0.11.6' '0.11.7' }
    Assert-That 'bump 0.9.9 -> 0.10.0 compares numerically' { Compare-StrictBump '0.9.9' '0.10.0' }
    Assert-That 'same version is not a bump' { -not (Compare-StrictBump '0.11.6' '0.11.6') }
    Assert-That 'downgrade is not a bump' { -not (Compare-StrictBump '0.11.6' '0.11.5') }
    Assert-That 'version 0.10.0 > 0.9.0 (not string order)' { Compare-StrictBump '0.9.0' '0.10.0' }

    foreach ($p in $versionPlaces) {
        $text = [IO.File]::ReadAllText((Join-Path $repoRoot $p.Path))
        Assert-That "place regex matches once and rewrites: $($p.Path)" {
            $cur = Get-VersionFromText $text $p.Regex $p.Path
            $new = Set-VersionInText $text $p.Regex '99.99.99' $p.Path
            ($cur -match '^\d+\.\d+\.\d+$') -and ($new -ne $text) -and (Get-VersionFromText $new $p.Regex) -eq '99.99.99' -and
            ($new.Replace('99.99.99', $cur) -eq $text)
        }
    }
    Assert-Throws 'Set-VersionInText fails when there is no match' { Set-VersionInText 'nothing' '(a)(\d)(b)' '1.2.3' }
    Assert-Throws 'Set-VersionInText fails on two matches' { Set-VersionInText 'a1b a2b' '(a)(\d)(b)' '1.2.3' }
    Assert-That 'all version places agree with each other (current tree)' {
        $v = $versionPlaces | ForEach-Object { Get-VersionFromText ([IO.File]::ReadAllText((Join-Path $repoRoot $_.Path))) $_.Regex } | Select-Object -Unique
        @($v).Count -eq 1
    }

    $cl = (Fx 'changelog-sample.md') -join "`n"
    Assert-That 'changelog: entry goes above the newest one, header kept' {
        $body = ConvertTo-ChangelogBody "### Fixed`n`n- A fix.`n`nNo change in dispatch behaviour."
        $n = Add-ChangelogEntry $cl '9.9.9' $body
        $n.IndexOf('## 9.9.9') -gt $n.IndexOf('# Changelog') -and $n.IndexOf('## 9.9.9') -lt $n.IndexOf('## 9.9.8') -and $n.Contains("## 9.9.9`n`n### Fixed`n`n- A fix.`n`nNo change in dispatch behaviour.`n`n## 9.9.8")
    }
    Assert-That 'changelog: idempotent' {
        $n = Add-ChangelogEntry $cl '9.9.9' (ConvertTo-ChangelogBody '- x')
        (Add-ChangelogEntry $n '9.9.9' (ConvertTo-ChangelogBody '- y')) -eq $n
    }
    Assert-That 'changelog: loose bullets get ### Changed' { (ConvertTo-ChangelogBody "one`n- two") -eq "### Changed`n`n- one`n- two" }
    Assert-That 'changelog: CRLF file stays CRLF' {
        $n = Add-ChangelogEntry ($cl -replace "`n", "`r`n") '9.9.9' (ConvertTo-ChangelogBody '- x')
        (($n -replace "`r`n", '').Contains("`n")) -eq $false
    }
    Assert-Throws 'changelog: empty notes rejected' { ConvertTo-ChangelogBody "  `n " }

    Assert-That 'build ok summary' { $s = Read-BuildSummary (Fx 'build-ok.txt'); $s.Succeeded -and $s.Warnings -eq 0 -and $s.Errors -eq 0 }
    Assert-That 'build with warnings detected' { $s = Read-BuildSummary (Fx 'build-warn.txt'); $s.Warnings -eq 3 -and $s.Errors -eq 0 }
    Assert-That 'build with errors detected' { $s = Read-BuildSummary (Fx 'build-fail.txt'); (-not $s.Succeeded) -and $s.Errors -eq 2 }
    Assert-That 'test summary (VSTest) passed' { $s = Read-TestSummary (Fx 'test-ok.txt'); $s.Total -eq 1234 -and $s.Passed -eq 1230 -and $s.Skipped -eq 4 -and $s.Failed -eq 0 -and $s.Found -eq 1 }
    Assert-That 'test summary (VSTest) failed' { $s = Read-TestSummary (Fx 'test-fail.txt'); $s.Failed -eq 2 -and $s.Total -eq 100 }
    Assert-That 'test summary (MTP block) passed' { $s = Read-TestSummary (Fx 'test-mtp-ok.txt'); $s.Total -eq 50 -and $s.Passed -eq 50 -and $s.Failed -eq 0 }
    Assert-That 'test summary: no summary found' { (Read-TestSummary @('nothing')).Found -eq 0 }

    Assert-That 'subject ok' { $null -eq (Test-CommitSubject '0.11.7' 'Plan chart fixes') }
    Assert-That 'subject with type tag rejected' { $null -ne (Test-CommitSubject '0.11.7' 'fix: plan chart') }
    Assert-That 'subject too long rejected' { $null -ne (Test-CommitSubject '0.11.7' ('x' * 70)) }
    Assert-That 'subject exactly 72 accepted' { $null -eq (Test-CommitSubject '0.11.7' ('x' * 64)) }

    Assert-That 'logs ok: started, config loaded, signals counted' {
        $r = Read-ReleaseLogs ((Fx 'logs-ok.txt') -join "`n") '9.9.9'; $r.Started -and $r.ConfigLoaded -and $r.Signals -eq 24 -and $r.Problems.Count -eq 0
    }
    Assert-That 'logs bad: errors after start reported, errors before start ignored' {
        $r = Read-ReleaseLogs ((Fx 'logs-bad.txt') -join "`n") '9.9.9'; $r.Started -and $r.Problems.Count -eq 2
    }
    Assert-That 'logs: other version means not started' { -not (Read-ReleaseLogs ((Fx 'logs-ok.txt') -join "`n") '9.9.7').Started }

    Assert-That 'digest + platforms parsed from imagetools output' {
        $l = Fx 'imagetools.txt'
        (Get-ImageDigest $l) -eq ('sha256:' + ('ab' * 32)) -and (Get-ImagePlatforms $l).Count -eq 2
    }
    Assert-That 'porcelain -z parsing incl. rename and untracked' {
        $raw = " M a/b.cs`0?? new dir/x.txt`0R  new.cs`0old.cs`0"
        $e = ConvertFrom-PorcelainZ $raw
        $e.Count -eq 3 -and $e[1].Path -eq 'new dir/x.txt' -and $e[2].Path -eq 'new.cs'
    }
    Assert-That 'token DPAPI round trip (temp path, fake value)' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("sb-selftest-" + [guid]::NewGuid().ToString('N') + '.dpapi')
        try {
            $fake = 'fake-token-' + [guid]::NewGuid().ToString('N')
            Save-HaToken (ConvertTo-SecureString $fake -AsPlainText -Force) $tmp
            $onDisk = [IO.File]::ReadAllText($tmp)
            (-not $onDisk.Contains($fake)) -and (Test-HaTokenFile $tmp) -and ((Read-HaToken $tmp $false) -eq $fake)
        }
        finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
    Assert-That 'token: missing file reads $null without env fallback; garbage file is not decryptable' {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("sb-selftest-" + [guid]::NewGuid().ToString('N') + '.dpapi')
        $a = ($null -eq (Read-HaToken $tmp $false)) -and -not (Test-HaTokenFile $tmp)
        try { Set-Content -LiteralPath $tmp -Value 'not-a-dpapi-blob'; $b = -not (Test-HaTokenFile $tmp) } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        $a -and $b
    }
    Assert-That 'Properties dir is never staged, secrets are forbidden' {
        ('src/Switchboard.Host/Properties/launchSettings.json' -match $neverStage) -and ('deploy/.env' -match $forbiddenStage) -and -not ('src/Foo.cs' -match $forbiddenStage)
    }
    if ($script:fails -gt 0) { Write-Host "SELFTEST FAIL ($script:fails)" -ForegroundColor Red; exit 1 }
    Write-Host 'SELFTEST PASS' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- helpers (side effects)

function Fail-Usage([string]$m) { Write-Host "usage: $m" -ForegroundColor Red; exit 2 }

if ($Check) { $Stage = @('preflight') }
if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+$') { Fail-Usage '-Version x.y.z is required' }
$selected = $stages
if ($Stage) { $selected = $stages | Where-Object { $_ -in $Stage } }
elseif ($From -or $To) {
    $a = if ($From) { [array]::IndexOf($stages, $From) } else { 0 }
    $b = if ($To) { [array]::IndexOf($stages, $To) } else { $stages.Count - 1 }
    if ($a -gt $b) { Fail-Usage '-From is after -To' }
    $selected = $stages[$a..$b]
}
# preflight always runs first: it is the guard for everything after it.
if ('preflight' -notin $selected) { $selected = @('preflight') + @($selected) }

Set-Location $repoRoot
# The parsers read English tool output whatever the OS locale is.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'; $env:VSLANG = '1033'; $env:PreferredUILang = 'en-US'
$sw = [Diagnostics.Stopwatch]::StartNew()
$ctx = [ordered]@{
    version = $Version; commit = $null; tests = $null; build = $null; imageDigest = $null; imagePlatforms = $null
    addonVersion = $null; addonState = $null; previousVersion = $null; signals = $null; goe = $null; ingressStatus = $null
    dryRun = [bool]$DryRun; stagesRun = @(); failedStage = $null; elapsedSeconds = 0
}
$statePath = Join-Path $outDir 'last-release.json'

function Invoke-Native {
    # Runs a native command, returns @{ Code; Lines }. Never throws on a non-zero exit.
    param([string]$File, [string[]]$Arguments, [switch]$Echo)
    $global:LASTEXITCODE = 0
    $lines = New-Object System.Collections.Generic.List[string]
    & $File @Arguments 2>&1 | ForEach-Object {
        $s = "$_"; $lines.Add($s)
        if ($Echo) { Write-Host "      $s" -ForegroundColor DarkGray }
    }
    return @{ Code = $global:LASTEXITCODE; Lines = $lines.ToArray() }
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments)][string[]]$GitArgs)
    $r = Invoke-Native 'git' $GitArgs
    if ($r.Code -ne 0) { throw "git $($GitArgs -join ' ') failed ($($r.Code)): $($r.Lines -join ' | ')" }
    return ($r.Lines -join "`n")
}

function Get-Dirty {
    $raw = (& git status --porcelain=v1 -z -uall 2>&1) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'git status failed' }
    return @(ConvertFrom-PorcelainZ $raw)
}

function Get-ManifestVersion([string]$Rev) {
    if ($Rev) { $t = Invoke-Git show "${Rev}:switchboard/config.yaml" } else { $t = [IO.File]::ReadAllText((Join-Path $repoRoot 'switchboard/config.yaml')) }
    return Get-VersionFromText $t $versionPlaces[0].Regex 'switchboard/config.yaml'
}

function Test-ReleaseCommitExists { return ((Invoke-Git log -1 --format=%s HEAD) -like "$Version`:*") }

function Get-PreviousVersion {
    if (Test-ReleaseCommitExists) { return Get-ManifestVersion 'HEAD~1' }
    return Get-ManifestVersion 'HEAD'
}

function Get-StagePaths {
    # Explicit paths for the commit: dirty tracked + untracked (never the Properties dir), plus the bump targets.
    $paths = New-Object System.Collections.Generic.SortedSet[string]
    foreach ($e in (Get-Dirty)) { if ($e.Path -notmatch $neverStage) { [void]$paths.Add($e.Path) } }
    return @($paths)
}

function Read-NotesText {
    if (-not $Notes) { throw '-Notes is required for bump' }
    if (Test-Path -LiteralPath $Notes -PathType Leaf) { return [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Notes)) }
    return $Notes
}

function Invoke-Ha {
    # Calls deploy/ha-supervisor.mjs; returns the parsed JSON. Token and URL travel through the environment only.
    param([string[]]$HaArgs)
    $env:SWITCHBOARD_HA_URL = $HaUrl
    $script = Join-Path $PSScriptRoot 'ha-supervisor.mjs'
    $stdout = New-Object System.Collections.Generic.List[string]
    $tok = Read-HaToken
    if (-not $tok) { throw 'no HA token (run deploy/set-ha-token.ps1)' }
    # plain text lives only in this variable; it goes to node over stdin, never argv or env
    $tok | & node $script @HaArgs '--token-stdin' 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { Write-Host "      $($_.Exception.Message)" -ForegroundColor DarkGray } else { $stdout.Add("$_") }
    }
    $last = $stdout | Where-Object { $_ -match '^\{' } | Select-Object -Last 1
    if (-not $last) { throw "ha-supervisor $($HaArgs[0]): no JSON result (exit $LASTEXITCODE)" }
    $tok = $null
    $j = $last | ConvertFrom-Json
    if (-not $j.ok) { throw "ha-supervisor $($HaArgs[0]): $($j.error)" }
    return $j
}

function Test-TokenPresent {
    # DPAPI file decryptable by this user; the env var counts only when the file is missing.
    if (Test-Path -LiteralPath (Get-HaTokenPath)) { return (Test-HaTokenFile) }
    return -not [string]::IsNullOrEmpty($env:SWITCHBOARD_HA_TOKEN)
}

# ---------------------------------------------------------------- stages: each returns a detail string or throws

function Stage-Preflight {
    $info = @()
    $branch = (Invoke-Git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { throw "on '$branch', not main" }

    if (-not [string]::IsNullOrWhiteSpace((Invoke-Git diff --cached --name-only))) { throw 'index has staged changes; the release stages explicit paths itself (git restore --staged .)' }
    $paths = Get-StagePaths
    $bad = @($paths | Where-Object { $_ -match $forbiddenStage -or $_ -notmatch $allowedRoots })
    if ($bad.Count) { throw "unexpected files in the tree: $($bad -join ', ')" }
    $info += "$($paths.Count) path(s) to stage"

    $head = Get-ManifestVersion 'HEAD'; $work = Get-ManifestVersion $null
    if (Test-ReleaseCommitExists) {
        if ($head -ne $Version) { throw "HEAD is a '$Version' commit but its manifest says $head" }
        $info += "resuming: release commit exists (previous $(Get-ManifestVersion 'HEAD~1'))"
    }
    else {
        if (-not (Compare-StrictBump $head $Version)) { throw "version $Version is not a strict bump of $head" }
        if ($work -ne $head -and $work -ne $Version) { throw "working manifest says $work, expected $head or $Version" }
        $info += "$head -> $Version"
    }
    if ('commit' -in $selected -or 'bump' -in $selected) {
        $why = Test-CommitSubject $Version $Subject
        if ($why) { throw "-Subject: $why" }
    }
    if ('bump' -in $selected -and -not (Test-ReleaseCommitExists)) { [void](ConvertTo-ChangelogBody (Read-NotesText)) }

    # gh: the krzyl2 account must exist; the globally active account is never switched.
    $r = Invoke-Native 'gh' @('auth', 'token', '--user', $GitHubAccount)
    if ($r.Code -ne 0) { throw "gh has no '$GitHubAccount' account" }
    $r = $null
    $b = Invoke-Native 'docker' @('buildx', 'ls')
    if ($b.Code -ne 0 -or -not ($b.Lines | Where-Object { $_ -match "^$([regex]::Escape($Builder))\b" })) { throw "docker buildx builder '$Builder' not found" }
    $nodeMajor = [int](((& node --version) -replace '^v', '') -split '\.')[0]
    if ($nodeMajor -lt 22) { throw "node ${nodeMajor}: 22+ needed for the HA client (global WebSocket)" }
    if (Test-TokenPresent) { $info += 'HA token present' }
    elseif ($DryRun) { $info += 'WARN: no usable HA token, run deploy/set-ha-token.ps1 (a real run would fail here)' }
    else { throw 'no usable HA token: file missing or not decryptable by this user (docs/RELEASE.md, one-time setup: pwsh deploy/set-ha-token.ps1)' }
    return ($info -join '; ')
}

function Stage-Bump {
    if (Test-ReleaseCommitExists) { return 'release commit exists; nothing to bump' }
    $old = Get-ManifestVersion 'HEAD'
    # A place the table does not know about would ship an inconsistent version: find any other mention.
    $grep = Invoke-Native 'git' @('grep', '-n', '-w', '-F', $old, '--', 'src', 'web', 'switchboard', 'tests', 'deploy', 'Directory.Build.props')
    $known = $versionPlaces | ForEach-Object { $_.Path }
    $stray = @($grep.Lines | Where-Object { $_ -match '^([^:]+):' -and $Matches[1] -notin $known -and $Matches[1] -notmatch $versionAllowedElsewhere })
    if ($stray.Count) { throw "old version $old also appears in: $(($stray | ForEach-Object { ($_ -split ':')[0] } | Select-Object -Unique) -join ', '); extend `$versionPlaces" }

    $writes = @()
    foreach ($p in $versionPlaces) {
        $full = Join-Path $repoRoot $p.Path
        $text = [IO.File]::ReadAllText($full)
        $new = Set-VersionInText $text $p.Regex $Version $p.Path
        if ($new -ne $text) { $writes += @{ Full = $full; Text = $new; Path = $p.Path } }
    }
    $clPath = Join-Path $repoRoot 'switchboard/CHANGELOG.md'
    $cl = [IO.File]::ReadAllText($clPath)
    $newCl = Add-ChangelogEntry $cl $Version (ConvertTo-ChangelogBody (Read-NotesText))
    if ($newCl -ne $cl) { $writes += @{ Full = $clPath; Text = $newCl; Path = 'switchboard/CHANGELOG.md' } }
    if (-not $writes.Count) { return "already at $Version" }
    if ($DryRun) { return "would set $Version in: $(($writes | ForEach-Object { $_.Path }) -join ', ')" }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    foreach ($w in $writes) { [IO.File]::WriteAllText($w.Full, $w.Text, $utf8) }
    return "$old -> $Version in $($writes.Count) file(s): $(($writes | ForEach-Object { $_.Path }) -join ', ')"
}

function Stage-Verify {
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $info = @()
    $b = Invoke-Native 'dotnet' @('build', 'Switchboard.slnx', '-c', 'Release', '--no-incremental', '-nologo', '-consoleloggerparameters:Summary')
    Set-Content -LiteralPath (Join-Path $outDir 'verify-build.log') -Value $b.Lines
    $bs = Read-BuildSummary $b.Lines
    $ctx.build = @{ warnings = $bs.Warnings; errors = $bs.Errors }
    if ($b.Code -ne 0 -or -not $bs.Succeeded -or $bs.Warnings -ne 0 -or $bs.Errors -ne 0) {
        throw "build: exit $($b.Code), $($bs.Warnings) warning(s), $($bs.Errors) error(s); see deploy/out/verify-build.log. Last: $($b.Lines | Select-Object -Last 4 | Out-String)"
    }
    $info += 'build 0 warnings 0 errors'

    $t = Invoke-Native 'dotnet' @('test', 'Switchboard.slnx', '-c', 'Release', '--no-build', '--nologo')
    Set-Content -LiteralPath (Join-Path $outDir 'verify-test.log') -Value $t.Lines
    $ts = Read-TestSummary $t.Lines
    $ctx.tests = @{ total = $ts.Total; passed = $ts.Passed; failed = $ts.Failed; skipped = $ts.Skipped }
    if ($t.Code -ne 0 -or $ts.Found -eq 0 -or $ts.Failed -ne 0 -or $ts.Total -eq 0) {
        throw "tests: exit $($t.Code), $($ts.Failed) failed of $($ts.Total) (summaries found: $($ts.Found)); see deploy/out/verify-test.log"
    }
    $info += "tests $($ts.Passed)/$($ts.Total) passed, $($ts.Skipped) skipped"

    # web only when it changed vs HEAD (tracked diff or new files)
    $webChanged = (Invoke-Git diff --name-only HEAD -- web) + (Invoke-Git ls-files --others --exclude-standard -- web)
    if (-not [string]::IsNullOrWhiteSpace($webChanged)) {
        if (-not (Test-Path (Join-Path $repoRoot 'web/node_modules'))) { throw 'web/ changed but web/node_modules is missing; run npm ci in web/ yourself' }
        Push-Location (Join-Path $repoRoot 'web')
        try {
            foreach ($npmArgs in @(@('test'), @('run', 'build'))) {
                $n = Invoke-Native 'npm' $npmArgs
                if ($n.Code -ne 0) { throw "npm $($npmArgs -join ' ') failed: $($n.Lines | Select-Object -Last 8 | Out-String)" }
            }
        }
        finally { Pop-Location }
        $info += 'web test+build ok'
    }
    else { $info += 'web unchanged (skipped)' }

    # call_service lives only in the command port / allow-list; DateTime.Now is banned by the analyzer (build above).
    $hits = @(Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter *.cs -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            Select-String -SimpleMatch 'call_service' -List |
            ForEach-Object { ($_.Path.Substring($repoRoot.Length + 1)) -replace '\\', '/' } |
            Where-Object { $_ -notmatch $callServiceAllowed })
    if ($hits.Count) { throw "call_service outside the command port: $($hits -join ', ')" }
    $info += 'call_service guard ok'
    return ($info -join '; ')
}

function Stage-Commit {
    if (Test-ReleaseCommitExists) {
        if ((Get-Dirty | Where-Object { $_.Path -notmatch $neverStage }).Count) { throw "release commit exists but the tree has further changes" }
        $ctx.commit = (Invoke-Git rev-parse HEAD).Trim()
        return "already committed $($ctx.commit.Substring(0, 8))"
    }
    $why = Test-CommitSubject $Version $Subject
    if ($why) { throw "-Subject: $why" }
    $msg = "${Version}: $Subject"
    $paths = Get-StagePaths
    if ($DryRun) { $paths = @($paths + @($versionPlaces | ForEach-Object { $_.Path }) + 'switchboard/CHANGELOG.md' | Sort-Object -Unique) }
    if (-not $paths.Count) { throw 'nothing to commit' }
    if ($DryRun) { return "would commit $($paths.Count) path(s) as '$msg'$(if ($CoAuthor) { ' + trailer' })`: $($paths -join ', ')" }
    Invoke-Git add -- @paths | Out-Null
    $commitArgs = @('commit', '-m', $msg)
    if ($CoAuthor) { $commitArgs += @('-m', $CoAuthor) }
    Invoke-Git @commitArgs | Out-Null
    $ctx.commit = (Invoke-Git rev-parse HEAD).Trim()
    return "$($ctx.commit.Substring(0, 8)) $msg ($($paths.Count) paths)"
}

function Stage-Push {
    if ($DryRun -and -not (Test-ReleaseCommitExists)) { return "would push the release commit to origin/main as $GitHubAccount (token via gh, never printed)" }
    $ctx.commit = (Invoke-Git rev-parse HEAD).Trim()
    $ahead = [int](Invoke-Git rev-list --count 'origin/main..HEAD').Trim()
    if ($ahead -eq 0) { return 'origin/main already has HEAD' }
    if ($DryRun) { return "would push $ahead commit(s) to origin/main as $GitHubAccount" }
    $r = Invoke-Native 'gh' @('auth', 'token', '--user', $GitHubAccount)
    if ($r.Code -ne 0 -or -not $r.Lines.Count) { throw "no gh token for '$GitHubAccount'" }
    $prev = $env:GH_TOKEN
    $env:GH_TOKEN = $r.Lines[0]
    $r = $null
    try {
        $p = Invoke-Native 'git' @('-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential', 'push', 'origin', 'main')
    }
    finally { if ($null -eq $prev) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $prev } }
    if ($p.Code -ne 0) { throw "push failed: $($p.Lines -join ' | ')" }
    [void](Invoke-Git fetch origin main --quiet)
    if ([int](Invoke-Git rev-list --count 'origin/main..HEAD').Trim() -ne 0) { throw 'push reported success but origin/main is behind HEAD' }
    return "pushed $ahead commit(s)"
}

function Get-RemoteImage([string]$Tag) {
    $r = Invoke-Native 'docker' @('buildx', 'imagetools', 'inspect', "${Image}:$Tag")
    if ($r.Code -ne 0) { return $null }
    return @{ Digest = (Get-ImageDigest $r.Lines); Platforms = (Get-ImagePlatforms $r.Lines) }
}

function Stage-Image {
    if ($DryRun -and -not (Test-ReleaseCommitExists)) { return "would run BUILDX_BUILDER=$Builder deploy/build-push.ps1 -Version $Version -SkipConfigSync (linux/amd64,linux/arm64), then imagetools inspect ${Image}:$Version for digest and platforms" }
    if (-not (Test-ReleaseCommitExists)) { throw "no '$Version`:' release commit at HEAD" }
    if ([int](Invoke-Git rev-list --count 'origin/main..HEAD').Trim() -ne 0) { throw 'HEAD is not pushed; the Supervisor reads config.yaml from main' }
    if ((Get-Dirty | Where-Object { $_.Path -notmatch $neverStage }).Count) { throw 'working tree is dirty; the image is built from the tree' }
    $existing = Get-RemoteImage $Version
    if ($existing -and -not $Rebuild) {
        $ctx.imageDigest = $existing.Digest; $ctx.imagePlatforms = $existing.Platforms
        return "tag exists, not rebuilt (-Rebuild to force): $($existing.Digest)"
    }
    if ($DryRun) { return "would run BUILDX_BUILDER=$Builder deploy/build-push.ps1 -Version $Version -SkipConfigSync, then imagetools inspect ${Image}:$Version" }
    $prev = $env:BUILDX_BUILDER; $env:BUILDX_BUILDER = $Builder
    try { $b = Invoke-Native 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'build-push.ps1'), '-Version', $Version, '-SkipConfigSync') -Echo }
    finally { if ($null -eq $prev) { Remove-Item Env:BUILDX_BUILDER -ErrorAction SilentlyContinue } else { $env:BUILDX_BUILDER = $prev } }
    if ($b.Code -ne 0) { throw "build-push.ps1 exit $($b.Code)" }
    $img = Get-RemoteImage $Version
    if (-not $img -or -not $img.Digest) { throw "pushed tag ${Image}:$Version not found in the registry" }
    $ctx.imageDigest = $img.Digest; $ctx.imagePlatforms = $img.Platforms
    $missing = @('linux/amd64', 'linux/arm64') | Where-Object { $_ -notin $img.Platforms }
    if ($missing) { throw "image lacks platform(s): $($missing -join ', ')" }
    return "$($img.Digest) [$($img.Platforms -join ', ')]"
}

function Stage-Deploy {
    if ($DryRun) { return "would: HA $HaUrl supervisor /store/reload, POST /addons/$addonSlug/update, poll until $Version started (<= ${DeployTimeoutSec}s). Never touches options, mode, control, switchboard.yaml, grzalka/goe entities" }
    $j = Invoke-Ha @('deploy', '--target', $Version, '--timeout', "$DeployTimeoutSec")
    $ctx.previousVersion = $j.previous; $ctx.addonVersion = $j.version; $ctx.addonState = $j.state
    $skipped = if ($j.skipped) { ' (was already running)' } else { '' }
    return "add-on $($j.previous) -> $($j.version) $($j.state)$skipped. The update stops the go-e once; Switchboard resumes it with option '0'"
}

function Stage-Postcheck {
    if ($DryRun) { return "would read add-on logs ('Switchboard $Version starting', 'Configuration ... loaded', no error/fail), states select.goe_422752_frc + sensor.goe_422752_car_value, api/status via ingress. Read-only" }
    $info = @()
    $l = Invoke-Ha @('logs')
    $r = Read-ReleaseLogs $l.text $Version
    if (-not $r.Started) { throw "log has no 'Switchboard $Version starting'" }
    if (-not $r.ConfigLoaded) { throw "log has no 'Configuration ... loaded' line after start" }
    $ctx.signals = $r.Signals
    $info += "signals $($r.Signals)"
    # report go-e state before judging the log, so the summary has it either way
    try {
        $s = Invoke-Ha @('states', 'select.goe_422752_frc', 'sensor.goe_422752_car_value')
        $ctx.goe = [ordered]@{}
        foreach ($e in $s.states) { $ctx.goe[$e.entity_id] = $e.state }
        $info += "go-e frc=$($ctx.goe['select.goe_422752_frc']) car=$($ctx.goe['sensor.goe_422752_car_value'])"
    }
    catch { $info += "go-e states unavailable: $($_.Exception.Message)" }
    try {
        $st = Invoke-Ha @('status')
        $ctx.ingressStatus = $st.status
        $info += "status v$($st.status.version) mode=$($st.status.mode) healthy=$($st.status.healthy)"
    }
    catch { $info += "api/status n/a: $($_.Exception.Message)" }
    if ($r.Problems.Count) { throw "log has $($r.Problems.Count) error/fail line(s) after start, first: $($r.Problems[0])" }
    return ($info -join '; ')
}

# ---------------------------------------------------------------- runner

function Write-Summary {
    $ctx.elapsedSeconds = [int]$sw.Elapsed.TotalSeconds
    Write-Host ''
    Write-Host '----- release summary -----'
    Write-Host "version      $($ctx.version)$(if ($DryRun) { '  (dry run)' })"
    Write-Host "commit       $($ctx.commit)"
    if ($ctx.build) { Write-Host "build        $($ctx.build.warnings) warnings, $($ctx.build.errors) errors" }
    if ($ctx.tests) { Write-Host "tests        $($ctx.tests.passed) passed / $($ctx.tests.total) total, $($ctx.tests.failed) failed, $($ctx.tests.skipped) skipped" }
    Write-Host "image digest $($ctx.imageDigest)"
    Write-Host "add-on       $($ctx.addonVersion) $($ctx.addonState)  (signals $($ctx.signals))"
    if ($ctx.goe) { Write-Host "go-e         $(($ctx.goe.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')" }
    Write-Host "elapsed      $($ctx.elapsedSeconds)s"
    $prev = $ctx.previousVersion
    if (-not $prev) { try { $prev = Get-PreviousVersion } catch { $prev = $null } }
    if ($prev -and -not $DryRun -and $ctx.stagesRun -contains 'deploy') {
        $ctx.previousVersion = $prev
        Write-Host ''
        Write-Host "Rollback hint (not run automatically): previous version was $prev; image ${Image}:$prev stays in GHCR."
        Write-Host "  Forward-roll: git revert --no-edit $($ctx.commit); then"
        Write-Host "  pwsh deploy/release.ps1 -Version <next patch> -Subject 'Revert $Version' -Notes '- Reverts $Version.'"
        Write-Host '  (the Supervisor offers only the manifest version on main, so a rollback is a new, higher version)'
    }
    if ($DryRun) { Write-Host ($ctx | ConvertTo-Json -Depth 6) }
    else {
        New-Item -ItemType Directory -Force $outDir | Out-Null
        [IO.File]::WriteAllText($statePath, ($ctx | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "json         deploy/out/last-release.json"
    }
}

if (-not $DryRun -and (Test-Path $statePath)) {
    # a resumed run (-From) keeps what earlier stages of the SAME version recorded
    try {
        $old = Get-Content $statePath -Raw | ConvertFrom-Json
        if ($old.version -eq $Version) { foreach ($k in 'commit', 'tests', 'build', 'imageDigest', 'imagePlatforms', 'addonVersion', 'addonState', 'previousVersion', 'signals', 'goe') { if ($null -ne $old.$k) { $ctx[$k] = $old.$k } } }
    }
    catch { }
}

Write-Host "Switchboard release $Version$(if ($DryRun) { ' (DRY RUN)' }) from $repoRoot"
foreach ($name in $stages) {
    if ($name -notin $selected) { continue }
    $fn = "Stage-$((Get-Culture).TextInfo.ToTitleCase($name))"
    $t0 = $sw.Elapsed.TotalSeconds
    try {
        $detail = & $fn
        Write-Host ("PASS  {0,-9} ({1:N0}s) {2}" -f $name, ($sw.Elapsed.TotalSeconds - $t0), $detail) -ForegroundColor Green
        $ctx.stagesRun += $name
    }
    catch {
        Write-Host ("FAIL  {0,-9} ({1:N0}s) {2}" -f $name, ($sw.Elapsed.TotalSeconds - $t0), ($_.Exception.Message -replace '\s+$', '')) -ForegroundColor Red
        $ctx.failedStage = $name
        Write-Host "Stopped at '$name'. Fix the cause, then re-run with -From $name (stages are idempotent). No ad-hoc manual fix of this stage." -ForegroundColor Yellow
        if (-not $DryRun -and $name -ne 'preflight') { try { Write-Summary } catch { } }
        exit ($exitBase + [array]::IndexOf($stages, $name) + 1)
    }
}
Write-Summary
exit 0
