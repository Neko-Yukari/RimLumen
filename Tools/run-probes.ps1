#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateRange(10, 1800)] [int]$TimeoutSeconds = 240,
    [switch]$NoBarrier,
    [switch]$Baseline,
    [switch]$TestReportGate
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$OutputEncoding = [Console]::OutputEncoding
$repoRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = 'E:/SteamLibrary/steamapps/common/RimWorld'
$junction = Join-Path $gameRoot 'Mods/RimLumen'
$profileName = if ($Baseline) { 'rimlumen-baseline-profile' } else { 'rimlumen-lab-profile' }
$profile = Join-Path $env:LOCALAPPDATA "Temp/opencode/$profileName"
$layerRoot = Join-Path $repoRoot '.scratch/vulkan-layers'
$logRoot = Join-Path $env:USERPROFILE 'AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios'
$out = Join-Path $repoRoot ('.scratch/probe-results/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$timer = [Diagnostics.Stopwatch]::StartNew()
$process = $null
$stdout = $null
$stderr = $null
$ownsJunction = $false
$startedUtc = $null
$exitCode = 1
$failure = $null
$validationErrors = @()
$syncProof = @()
$syncHazards = @()
$layerProof = @()
$quitPath = 'Root.Shutdown/Application.Quit'
$validationPolicy = 'sync-shader-accesses-v1'
$baselinePath = $null
$baselineReport = $null
$comparison = $null
$baselineCaptured = $false

function Get-RimWorldProcess {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^RimWorld(?:Win64)?$' })
}

function Get-ValidationReport([string]$LogText) {
    $install = [regex]::Match($LogText, '(?m)^\d{4}-[^\r\n]*\[RimLumen\] Installed:')
    $ownedHandles = @{}
    foreach ($owned in [regex]::Matches($LogText, '\[RimLumen\] OwnedVkObject type=(\w+) handle=(0x[0-9A-Fa-f]+) name=([^;\r\n]+)')) {
        $ownedHandles[$owned.Groups[2].Value.ToLowerInvariant()] = "$($owned.Groups[1].Value):$($owned.Groups[3].Value)"
    }
    $messages = @(foreach ($entry in [regex]::Matches($LogText, '(?ms)^\d{4}-\d\d-\d\d [^\r\n]*? ERROR [^\r\n]*\[(?:Validation|General|Performance)\].*?(?=^\d{4}-\d\d-\d\d |\z)')) {
        $id = [regex]::Match($entry.Value, '\[(VUID-[^\]]+|SYNC-HAZARD[^\]]+)\]')
        $handles = @([regex]::Matches($entry.Value, '0x[0-9A-Fa-f]+') | ForEach-Object { $_.Value.ToLowerInvariant() } | Sort-Object -Unique)
        $owned = @($handles | Where-Object { $ownedHandles.ContainsKey($_) } | ForEach-Object { "$($_)=$($ownedHandles[$_])" })
        [pscustomobject]@{
            Id = $(if ($id.Success) { $id.Groups[1].Value } else { 'unclassified-validation' })
            Phase = $(if (-not $install.Success) { 'no-install' } elseif ($entry.Index -lt $install.Index) { 'before-install' } else { 'after-install' })
            NamesRimLumenOwnedHandle = ($owned.Count -gt 0)
            OwnedHandles = $owned
            InsideRimLumenCommands = ($entry.Value -match 'RimLumen\.Core\.Rendering\.[^\r\n]*\.(?:Record|RecordHdrDraws|RecordBufferBarriers)\(' -or
                $entry.Value -match 'RimLumen/(?:HDR|Composite|Bloom)' -or $entry.Value -match 'RimLumen\.Probe\.(?:Ssbo|Indirect)')
            NamesRimLumen = ($entry.Value -match 'RimLumen')
            Header = ($entry.Value -split '\r?\n')[0]
            Message = $entry.Value.TrimEnd()
        }
    })
    $leaks = @($messages | Where-Object Id -eq 'VUID-vkDestroyDevice-device-05137')
    $leakCount = 0
    $leakedObjects = @(foreach ($leak in $leaks) {
        $count = [regex]::Match($leak.Message, 'has (\d+) leaked objects')
        if (-not $count.Success) { throw 'Unrecognized leaked-object count; cannot certify baseline delta.' }
        $leakCount += [int]$count.Groups[1].Value
        foreach ($obj in [regex]::Matches(($leak.Message -split '\r?\n' | Select-Object -Skip 1) -join "`n", '\b(Vk\w+) (0x[0-9A-Fa-f]+)')) {
            [pscustomobject]@{ Type = $obj.Groups[1].Value; Handle = $obj.Groups[2].Value }
        }
    })
    [pscustomobject]@{
        ErrorCount = $messages.Count
        LeakedObjectCount = $leakCount
        LeakedObjects = $leakedObjects
        InstallSeen = $install.Success
        OwnedHandles = $ownedHandles
        Groups = @($messages | Group-Object Id, Phase, NamesRimLumenOwnedHandle | ForEach-Object {
            [pscustomobject]@{ Id = $_.Group[0].Id; Phase = $_.Group[0].Phase; NamesRimLumenOwnedHandle = $_.Group[0].NamesRimLumenOwnedHandle; Count = $_.Count }
        })
        Messages = $messages
    }
}

function Compare-ValidationReport($Current, $Reference) {
    $ids = @(@($Current.Messages.Id) + @($Reference.Messages.Id) | Where-Object { $_ } | Sort-Object -Unique)
    $rows = @(foreach ($id in $ids) {
        [pscustomobject]@{ Id = $id; Baseline = @($Reference.Messages | Where-Object Id -eq $id).Count; Current = @($Current.Messages | Where-Object Id -eq $id).Count }
    })
    $owned = @($Current.Messages | Where-Object { $_.NamesRimLumenOwnedHandle -or $_.InsideRimLumenCommands }).Count
    $newIds = @($rows | Where-Object { $_.Current -gt 0 -and $_.Baseline -eq 0 } | ForEach-Object Id)
    [pscustomobject]@{
        Passed = ($owned -eq 0 -and $Current.LeakedObjectCount -le $Reference.LeakedObjectCount -and $newIds.Count -eq 0)
        OwnedMessageCount = $owned; NewIds = $newIds; Rows = $rows
        BaselineLeaks = $Reference.LeakedObjectCount; CurrentLeaks = $Current.LeakedObjectCount
    }
}

if ($TestReportGate) {
    # Pure report seam: known engine ids are permitted, ownership/new ids/leak increases are not.
    $engine = [pscustomobject]@{ Id='SYNC-HAZARD-PRESENT-AFTER-WRITE'; NamesRimLumenOwnedHandle=$false; InsideRimLumenCommands=$false }
    $reference = [pscustomobject]@{ Messages=@($engine); LeakedObjectCount=6 }
    $current = [pscustomobject]@{ Messages=@($engine); LeakedObjectCount=6 }
    if (-not (Compare-ValidationReport $current $reference).Passed) { throw 'Equal baseline rejected.' }
    $current.LeakedObjectCount=7
    if ((Compare-ValidationReport $current $reference).Passed) { throw 'Leak increase accepted.' }
    $current.LeakedObjectCount=6
    $current.Messages=@([pscustomobject]@{ Id=$engine.Id; NamesRimLumenOwnedHandle=$true; InsideRimLumenCommands=$false })
    if ((Compare-ValidationReport $current $reference).Passed) { throw 'Owned handle accepted.' }
    $current.Messages=@([pscustomobject]@{ Id=$engine.Id; NamesRimLumenOwnedHandle=$false; InsideRimLumenCommands=$true })
    if ((Compare-ValidationReport $current $reference).Passed) { throw 'Owned command accepted.' }
    $current.Messages=@([pscustomobject]@{ Id='NEW-VUID'; NamesRimLumenOwnedHandle=$false; InsideRimLumenCommands=$false })
    if ((Compare-ValidationReport $current $reference).Passed) { throw 'New id accepted.' }
    Write-Host 'REPORT GATE: 5 passed; no game/process/profile operations performed.'
    exit 0
}

if ($Baseline -and $NoBarrier) { throw '-Baseline and -NoBarrier are mutually exclusive.' }
if (@(Get-RimWorldProcess).Count -ne 0) { throw 'Preflight refused: a RimWorld process is already running.' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
Write-Host "Artifacts: $out"
try {
    if (-not $Baseline) {
        $layerHash = (Get-FileHash (Join-Path $layerRoot 'VkLayer_khronos_validation.dll')).Hash
        foreach ($candidate in (Get-ChildItem (Join-Path $repoRoot '.scratch/probe-results') -Filter run-result.json -Recurse | Sort-Object FullName -Descending)) {
            $run = Get-Content $candidate.FullName -Raw | ConvertFrom-Json
            if ($run.BaselineCaptured -and $run.QuitPath -eq $quitPath -and $run.ValidationPolicy -eq $validationPolicy -and $run.LayerHash -eq $layerHash) {
                $baselinePath = Join-Path $candidate.DirectoryName 'validation-report.json'
                $baselineReport = Get-Content $baselinePath -Raw | ConvertFrom-Json
                break
            }
        }
        if (-not $baselineReport) { throw 'No comparable captured baseline; run -Baseline with this validation policy first.' }
        Write-Host "Baseline: $baselinePath"
        $existing = Get-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue
        if ($existing) {
            if ($existing.LinkType -ne 'Junction') { throw "Refusing non-junction: $junction" }
            if ([IO.Path]::GetFullPath($existing.Target).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')) {
                throw "Refusing junction targeting another worktree: $($existing.Target)"
            }
            $ownsJunction = $true
        }
    }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1') 2>&1 | Tee-Object -FilePath (Join-Path $out 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Build exited $LASTEXITCODE" }
    foreach ($file in @('VkLayer_khronos_validation.dll', 'VkLayer_khronos_validation.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $layerRoot $file))) { throw "Missing validation layer: $file" }
    }
    if (-not $Baseline -and -not $ownsJunction) {
        New-Item -ItemType Junction -Path $junction -Target $repoRoot | Out-Null
        $ownsJunction = $true
    }
    New-Item -ItemType Directory -Path (Join-Path $profile 'Config') -Force | Out-Null
    $expansions = @('Royalty', 'Ideology', 'Biotech', 'Anomaly', 'Odyssey') | Where-Object {
        Test-Path -LiteralPath (Join-Path $gameRoot "Data/$_/About/About.xml")
    }
    $known = ($expansions | ForEach-Object { '<li>ludeon.rimworld.' + $_.ToLowerInvariant() + '</li>' }) -join ''
    $mod = if ($Baseline) { '' } else { '<li>nekoyukari.rimlumen</li>' }
    @"
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData><version>1.7.5005 rev1</version><activeMods><li>ludeon.rimworld</li>$mod</activeMods><knownExpansions>$known</knownExpansions></ModsConfigData>
"@ | Set-Content -LiteralPath (Join-Path $profile 'Config/ModsConfig.xml') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $profile 'Config/ModsConfig.xml') -Destination (Join-Path $out 'ModsConfig.xml')
    @"
vvl.validate_sync = true
vvl.syncval_shader_accesses_heuristic = true
vvl.syncval_message_extra_properties = true
vvl.report_flags = error,warn,info
vvl.debug_action = VK_DBG_LAYER_ACTION_LOG_MSG
vvl.log_filename = $($out.Replace('\', '/'))/validation.log
vvl.enable_message_limit = false
"@ | Set-Content -LiteralPath (Join-Path $out 'vk_layer_settings.txt') -Encoding utf8

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $gameRoot 'RimWorld.exe'
    $start.WorkingDirectory = $gameRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoWindow', '-NoCrashDialog', '-quicktest', '-EnableVulkanValidation', "-savedatafolder=$profile")) {
        $start.ArgumentList.Add($argument)
    }
    if ($Baseline) {
        # Bootstrap.cs:205-207 calls Root.Shutdown at boot-complete frame 180.
        $start.ArgumentList.Add('-QuitAfterBoot')
    } else {
        $start.ArgumentList.Add("-rimlumen-probe=$out")
        if ($NoBarrier) { $start.ArgumentList.Add('-rimlumen-probe-nobarrier') }
    }
    $start.Environment['VK_ADD_LAYER_PATH'] = $layerRoot
    $start.Environment.Remove('VK_LAYER_PATH') | Out-Null
    $start.Environment['VK_LOADER_DEBUG'] = 'layer'
    $start.Environment['VK_LAYER_SETTINGS_PATH'] = $out
    $start.Environment['VK_LAYER_VALIDATE_SYNC'] = '1'
    $start.Environment['VK_LAYER_DUPLICATE_MESSAGE_LIMIT'] = '0'
    $startedUtc = [DateTime]::UtcNow
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    Write-Host "Started hidden RimWorld PID=$($process.Id); timeout=${TimeoutSeconds}s; Baseline=$Baseline; NoBarrier=$NoBarrier"
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "RimWorld timed out after ${TimeoutSeconds}s; process tree killed."
    }
    if ($Baseline -and $process.ExitCode -ne 0) { throw "Baseline RimWorld exited $($process.ExitCode)" }
    if (-not $Baseline) {
        $resultPath = Join-Path $out 'probe-result.json'
        if (-not (Test-Path -LiteralPath $resultPath)) { throw 'RimWorld did not write probe-result.json.' }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.QuitPath -ne $quitPath) { throw 'Probe quit path differs from baseline.' }
        if ($process.ExitCode -ne $result.ExpectedProcessExitCode) { throw "Unexpected process exit $($process.ExitCode); probe expected $($result.ExpectedProcessExitCode)." }
        Write-Host "Process exit=$($process.ExitCode); probe Passed=$($result.Passed); expected=$($result.ExpectedProcessExitCode)"
        foreach ($required in @('T1a', 'T1b', 'T2', 'T3', 'T4', 'P1-idle', 'P1-active', 'feature-order', 'validation-layer')) {
            $check = @($result.Checks | Where-Object Id -eq $required)
            if ($check.Count -ne 1 -or -not $check[0].Passed) { throw "Required check failed or absent: $required" }
        }
        foreach ($check in $result.Checks) { Write-Host "$($check.Id): Passed=$($check.Passed) $($check.Detail)" }
        if (-not $result.Passed -or @($result.Checks | Where-Object { -not $_.Passed }).Count -gt 0) { throw 'One or more probes failed.' }
        if ($result.LogRoot) { $logRoot = $result.LogRoot }
    }
    $exitCode = 0
}
catch { $failure = $_.Exception.Message; Write-Host "PROBE FAILURE: $failure" }
finally {
    try {
        if ($process) {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            if ($stdout) { [IO.File]::WriteAllText((Join-Path $out 'stdout.log'), $stdout.GetAwaiter().GetResult()) }
            if ($stderr) { [IO.File]::WriteAllText((Join-Path $out 'stderr.log'), $stderr.GetAwaiter().GetResult()) }
        }
        if ($startedUtc) {
            foreach ($root in @($logRoot, $profile)) {
                $logs = Join-Path $root 'Logs'
                if (Test-Path -LiteralPath $logs) {
                    Get-ChildItem -LiteralPath $logs -File | Where-Object { $_.LastWriteTimeUtc -ge $startedUtc } | ForEach-Object {
                        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $out $_.Name) -Force
                    }
                }
            }
        }
        $playerLog = Join-Path $out 'Player.log'
        if (Test-Path -LiteralPath $playerLog) {
            $report = Get-ValidationReport ([IO.File]::ReadAllText($playerLog))
            $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'validation-report.json') -Encoding utf8
            $validationErrors = @($report.Messages | ForEach-Object Header)
            $syncHazards = @($report.Messages | Where-Object Id -like 'SYNC-HAZARD*' | ForEach-Object Header)
            $syncProof = @($syncHazards | Select-Object -First 1)
            $layerProof = @(Select-String -LiteralPath $playerLog -SimpleMatch '[RimLumen] ValidationEnabled=True; InstanceEnabledLayers=VK_LAYER_KHRONOS_validation' | ForEach-Object Line)
            if ($Baseline -and $report.InstallSeen) { throw 'Baseline unexpectedly loaded RimLumen.' }
            if ([IO.File]::ReadAllText($playerLog) -match '\[RimLumen\] Vulkan resource shutdown failed:') { throw 'RimLumen resource shutdown failed.' }
        } else { $exitCode = 1; Write-Host 'FAIL: no Player.log collected.' }
        $loaderLog = Join-Path $out 'stderr.log'
        if (Test-Path -LiteralPath $loaderLog) {
            $layerProof += @(Select-String -LiteralPath $loaderLog -SimpleMatch 'Inserted device layer "VK_LAYER_KHRONOS_validation"' | ForEach-Object Line)
        }
        $validationLog = Join-Path $out 'validation.log'
        if (Test-Path -LiteralPath $validationLog) {
            $syncProof += @(Select-String -LiteralPath $validationLog -Pattern 'VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT|validate_sync.*true' | ForEach-Object Line)
        }
        if ($layerProof.Count -eq 0) { $exitCode = 1; Write-Host 'FAIL: no positive validation-layer proof.' }
        if ($syncProof.Count -eq 0) { $exitCode = 1; Write-Host 'FAIL: no positive synchronization-validation proof (setting echo or SYNC-HAZARD).' }
        if ($Baseline) {
            $baselineCaptured = ($exitCode -eq 0 -and $layerProof.Count -gt 0 -and $syncProof.Count -gt 0)
            @($report.Groups | Select-Object Id, Phase, Count) | Format-Table -AutoSize | Out-String | Write-Host
            Write-Host "Baseline leaked objects: $($report.LeakedObjectCount)"
        } elseif ($report -and $baselineReport) {
            $comparison = Compare-ValidationReport $report $baselineReport
            $comparison.Rows | Format-Table -AutoSize | Out-String | Write-Host
            Write-Host "Leaked objects: baseline=$($comparison.BaselineLeaks); current=$($comparison.CurrentLeaks); ownedMessages=$($comparison.OwnedMessageCount)"
            if (-not $comparison.Passed) { $exitCode = 1 }
            $comparison | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'baseline-comparison.json') -Encoding utf8
        } else { $exitCode = 1 }
        $validationErrors | Set-Content -LiteralPath (Join-Path $out 'validation-errors.txt') -Encoding utf8
        Write-Host "Validation errors: $($validationErrors.Count); SYNC-HAZARD=$($syncHazards.Count); control=$NoBarrier"
        $layerProof | Select-Object -First 2 | ForEach-Object { Write-Host "Layer proof: $_" }
        $syncProof | Select-Object -First 1 | ForEach-Object { Write-Host "Sync proof: $_" }
    }
    catch { $exitCode = 1; $failure = $_.Exception.Message; Write-Host "Collection failure: $failure" }
    finally {
        if ($ownsJunction) {
            try { [IO.Directory]::Delete($junction) }
            catch { $exitCode = 1; Write-Host "FAIL: junction cleanup: $($_.Exception.Message)" }
        }
        if ($ownsJunction -and (Test-Path -LiteralPath $junction)) { $exitCode = 1; Write-Host 'FAIL: junction remains.' }
        if (@(Get-RimWorldProcess).Count -ne 0) { $exitCode = 1; Write-Host 'FAIL: RimWorld process remains.' }
        $timer.Stop()
        @{
            ExitCode = $exitCode; Failure = $failure; DurationSeconds = $timer.Elapsed.TotalSeconds
            ValidationErrorCount = $validationErrors.Count; SyncHazardCount = $syncHazards.Count
            ValidationClean = ($layerProof.Count -gt 0 -and $syncProof.Count -gt 0 -and $validationErrors.Count -eq 0)
            Baseline = [bool]$Baseline; BaselineCaptured = ($baselineCaptured -and $exitCode -eq 0)
            QuitPath = $quitPath; ValidationPolicy = $validationPolicy
            LayerHash = $(if (Test-Path (Join-Path $layerRoot 'VkLayer_khronos_validation.dll')) { (Get-FileHash (Join-Path $layerRoot 'VkLayer_khronos_validation.dll')).Hash } else { $null })
            BaselineReport = $baselinePath; AttributionGatePassed = ($null -ne $comparison -and $comparison.Passed)
            ProcessExitCode = $(if ($process -and $process.HasExited) { $process.ExitCode } else { $null })
            NoBarrier = [bool]$NoBarrier; LayerProof = $layerProof; SyncProof = $syncProof
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'run-result.json') -Encoding utf8
        Write-Host "Cleanup: junctionExists=$(Test-Path -LiteralPath $junction); RimWorldProcesses=$(@(Get-RimWorldProcess).Count)"
        Write-Host "Duration: $($timer.Elapsed.TotalSeconds.ToString('F3'))s; exit=$exitCode"
        Write-Host "Artifacts: $out"
    }
}
exit $exitCode
