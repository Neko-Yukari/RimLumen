#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RimWorldDir = 'E:\SteamLibrary\steamapps\common\RimWorld',
    [string]$CaptureAndQuit = '',
    [string]$Benchmark = '',
    [switch]$NoBuild,
    [switch]$Detach,
    [switch]$CleanupJunction,
    [ValidateRange(10, 1800)] [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$OutputEncoding = [Console]::OutputEncoding

$repoRoot = Split-Path -Parent $PSScriptRoot
$junction = Join-Path $RimWorldDir 'Mods/RimLumen'
# Benchmark runs keep their own save-data folder so interactive showcase sessions (dev mode, mod settings) cannot skew them.
$profileName = if ($Benchmark) { 'rimlumen-bench-profile' } else { 'rimlumen-showcase-profile' }
$profile = Join-Path $env:LOCALAPPDATA "Temp/opencode/$profileName"

function Get-RimWorldProcess {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^RimWorld(?:Win64)?$' })
}

function Remove-OwnedJunction([string]$JunctionPath, [string]$ExpectedTarget) {
    $item = Get-Item -LiteralPath $JunctionPath -Force -ErrorAction SilentlyContinue
    if (-not $item) { return }
    if ($item.LinkType -ne 'Junction') {
        throw "Refusing to remove non-junction at: $JunctionPath"
    }
    $actual = [IO.Path]::GetFullPath($item.Target).TrimEnd('\', '/')
    $expected = [IO.Path]::GetFullPath($ExpectedTarget).TrimEnd('\', '/')
    if ($actual -ne $expected) {
        throw "Refusing to remove junction targeting another worktree: $($item.Target)"
    }
    [IO.Directory]::Delete($JunctionPath)
}

if (@(Get-RimWorldProcess).Count -ne 0) {
    throw 'Preflight refused: a RimWorld process is already running.'
}

if ($CleanupJunction) {
    Remove-OwnedJunction -JunctionPath $junction -ExpectedTarget $repoRoot
    Write-Host "Cleaned up junction (if present): $junction"
    exit 0
}

if ($CaptureAndQuit -and $Detach) {
    throw '-CaptureAndQuit and -Detach are mutually exclusive.'
}

if ($Benchmark -and ($CaptureAndQuit -or $Detach)) {
    throw '-Benchmark cannot be combined with -CaptureAndQuit or -Detach.'
}

$rimWorldExe = Join-Path $RimWorldDir 'RimWorld.exe'
if (-not (Test-Path -LiteralPath $rimWorldExe -PathType Leaf)) {
    throw "RimWorld.exe not found at: $rimWorldExe"
}

if (-not $NoBuild) {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
}

$ownsJunction = $false
$cleanupInFinally = -not $Detach
$process = $null

try {
    $existing = Get-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.LinkType -ne 'Junction') {
            throw "Refusing non-junction: $junction"
        }
        $actualTarget = [IO.Path]::GetFullPath($existing.Target).TrimEnd('\', '/')
        $expectedTarget = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')
        if ($actualTarget -ne $expectedTarget) {
            throw "Refusing junction targeting another worktree: $($existing.Target)"
        }
        $ownsJunction = $true
    }
    else {
        New-Item -ItemType Junction -Path $junction -Target $repoRoot | Out-Null
        $ownsJunction = $true
    }

    New-Item -ItemType Directory -Path (Join-Path $profile 'Config') -Force | Out-Null
    $expansions = @('Royalty', 'Ideology', 'Biotech', 'Anomaly', 'Odyssey') | Where-Object {
        Test-Path -LiteralPath (Join-Path $RimWorldDir "Data/$_/About/About.xml")
    }
    $known = ($expansions | ForEach-Object { '<li>ludeon.rimworld.' + $_.ToLowerInvariant() + '</li>' }) -join ''
    $modsXml = @"
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData><version>1.7.5005 rev1</version><activeMods><li>ludeon.rimworld</li><li>nekoyukari.rimlumen</li></activeMods><knownExpansions>$known</knownExpansions></ModsConfigData>
"@
    Set-Content -LiteralPath (Join-Path $profile 'Config/ModsConfig.xml') -Value $modsXml -Encoding utf8

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $rimWorldExe
    $start.WorkingDirectory = $RimWorldDir
    $start.UseShellExecute = $false

    if ($Benchmark) {
        $resolvedJson = [IO.Path]::GetFullPath($Benchmark)
        $jsonDir = Split-Path -Parent $resolvedJson
        if ($jsonDir) {
            New-Item -ItemType Directory -Path $jsonDir -Force | Out-Null
        }
        if (Test-Path -LiteralPath $resolvedJson) {
            Remove-Item -LiteralPath $resolvedJson -Force
        }

        $start.CreateNoWindow = $true
        foreach ($arg in @(
            '-NoWindow',
            '-NoCrashDialog',
            '-quicktest',
            '-WindowSize=1920,1080',
            '-ResetVulkanFallback',
            "-savedatafolder=$profile",
            "-rimlumen-showcase-bench=$resolvedJson"
        )) {
            $start.ArgumentList.Add($arg)
        }

        $process = [Diagnostics.Process]::Start($start)
        Write-Host "Started headless RimWorld showcase benchmark PID=$($process.Id); timeout=${TimeoutSeconds}s"
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "RimWorld showcase benchmark timed out after ${TimeoutSeconds}s; process tree killed."
        }
        if ($process.ExitCode -ne 0) {
            throw "RimWorld showcase benchmark exited with code $($process.ExitCode)"
        }
        if (-not (Test-Path -LiteralPath $resolvedJson -PathType Leaf) -or (Get-Item -LiteralPath $resolvedJson).Length -le 0) {
            throw "Showcase benchmark JSON was not created or is empty: $resolvedJson"
        }
        Write-Host "SHOWCASE BENCH OK: $resolvedJson ($((Get-Item -LiteralPath $resolvedJson).Length) bytes)"
    }
    elseif ($CaptureAndQuit) {
        $resolvedPng = [IO.Path]::GetFullPath($CaptureAndQuit)
        $pngDir = Split-Path -Parent $resolvedPng
        if ($pngDir) {
            New-Item -ItemType Directory -Path $pngDir -Force | Out-Null
        }
        if (Test-Path -LiteralPath $resolvedPng) {
            Remove-Item -LiteralPath $resolvedPng -Force
        }

        $start.CreateNoWindow = $true
        foreach ($arg in @(
            '-NoWindow',
            '-NoCrashDialog',
            '-quicktest',
            '-WindowSize=1920,1080',
            "-savedatafolder=$profile",
            '-rimlumen-showcase',
            "-rimlumen-showcase-capture=$resolvedPng"
        )) {
            $start.ArgumentList.Add($arg)
        }

        $process = [Diagnostics.Process]::Start($start)
        Write-Host "Started headless RimWorld showcase capture PID=$($process.Id); timeout=${TimeoutSeconds}s"
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "RimWorld showcase capture timed out after ${TimeoutSeconds}s; process tree killed."
        }
        if ($process.ExitCode -ne 0) {
            throw "RimWorld showcase capture exited with code $($process.ExitCode)"
        }
        if (-not (Test-Path -LiteralPath $resolvedPng -PathType Leaf) -or (Get-Item -LiteralPath $resolvedPng).Length -le 0) {
            throw "Showcase capture PNG was not created or is empty: $resolvedPng"
        }
        Write-Host "SHOWCASE CAPTURE OK: $resolvedPng ($((Get-Item -LiteralPath $resolvedPng).Length) bytes)"
    }
    else {
        foreach ($arg in @(
            '-quicktest',
            '-popupwindow',
            '-WindowSize=1920,1080',
            "-savedatafolder=$profile",
            '-rimlumen-showcase'
        )) {
            $start.ArgumentList.Add($arg)
        }

        $process = [Diagnostics.Process]::Start($start)
        if ($Detach) {
            Write-Host "Launched interactive RimWorld showcase PID=$($process.Id) (detached)."
            Write-Host "Junction kept active at: $junction"
            Write-Host "After closing RimWorld, clean up with: pwsh -NoProfile -File Tools/launch-showcase.ps1 -CleanupJunction"
        }
        else {
            Write-Host "Launched interactive RimWorld showcase PID=$($process.Id); waiting for exit..."
            $process.WaitForExit()
            Write-Host "RimWorld exited with code $($process.ExitCode)"
        }
    }
}
finally {
    if ($cleanupInFinally -and $ownsJunction) {
        if ($process -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        Remove-OwnedJunction -JunctionPath $junction -ExpectedTarget $repoRoot
    }
}
