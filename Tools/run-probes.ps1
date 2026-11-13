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
$validationPolicy = 'sync-khronos-settings-host-proof-v3'
$baselinePath = $null
$baselineReport = $null
$freshArtifact = $null
$vanillaUnion = $null
$comparison = $null
$baselineCaptured = $false
# Reviewed Waylith.dll (GraphicsDevice.cs:233-255 enables SynchronizationValidationEXT in pNext).
# A mismatch means the reviewed source no longer matches the running engine: re-audit before trusting sync proof.
$expectedWaylithHash = '99B8F8B7A2051DAA05DC9A4B61771FE3545C172AAF022E96D9620863B45CE512'
$hostEnableProof = @()
$layerInsertProof = @()
$waylithHash = $null
$hashMatchesReview = $false
$processExitOk = $false
$settingsProof = $null
$enableProof = $null
$hostProof = $null
$baselineProof = $null
$report = $null

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

function Get-VanillaBaselineArtifact([string]$Directory) {
    $run = $null
    $ids = @()
    $reason = $null
    try {
        foreach ($file in @('run-result.json', 'ModsConfig.xml', 'Player.log', 'validation-report.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $Directory $file) -PathType Leaf)) { throw "Missing $file" }
        }
        $run = Get-Content -LiteralPath (Join-Path $Directory 'run-result.json') -Raw | ConvertFrom-Json
        if ($run.Baseline -isnot [bool] -or -not $run.Baseline) { throw 'run-result.json Baseline is not true' }
        [xml]$mods = Get-Content -LiteralPath (Join-Path $Directory 'ModsConfig.xml') -Raw
        $active = @($mods.SelectNodes('/ModsConfigData/activeMods/li') | ForEach-Object { $_.InnerText.Trim() })
        if ($active.Count -ne 1 -or $active[0] -ine 'ludeon.rimworld') { throw "ModsConfig.xml is not Core-only: $($active -join ', ')" }
        if ([IO.File]::ReadAllText((Join-Path $Directory 'Player.log')) -match '\[RimLumen\] Installed:') { throw 'Player.log contains RimLumen Install line' }
        $report = Get-Content -LiteralPath (Join-Path $Directory 'validation-report.json') -Raw | ConvertFrom-Json
        if ($null -eq $report -or $null -eq $report.PSObject.Properties['Messages']) { throw 'validation-report.json has no Messages array' }
        foreach ($message in $report.Messages) {
            if ([string]::IsNullOrWhiteSpace($message.Id)) { throw 'validation-report.json contains a message without an id' }
        }
        $ids = @($report.Messages.Id | Where-Object { $_ } | Sort-Object -Unique)
    }
    catch { $reason = $_.Exception.Message; $ids = @() }
    [pscustomobject]@{
        Verified = ($null -eq $reason); RunId = Split-Path -Leaf $Directory; Directory = $Directory
        ValidationPolicy = $run.ValidationPolicy; LayerHash = $run.LayerHash
        Ids = $ids; Reason = $reason
    }
}

function Get-VanillaBaselineUnion([string]$ResultsRoot) {
    $verified = @()
    $skipped = @()
    foreach ($directory in (Get-ChildItem -LiteralPath $ResultsRoot -Directory | Sort-Object Name -Descending)) {
        $artifact = Get-VanillaBaselineArtifact $directory.FullName
        if ($artifact.Verified) { $verified += $artifact } else { $skipped += $artifact }
    }
    [pscustomobject]@{ VerifiedArtifacts = $verified; SkippedArtifacts = $skipped }
}

function Compare-ValidationReport($Current, $Reference, [object[]]$VerifiedArtifacts = @(), $FreshArtifact = $null) {
    $ids = @(@($Current.Messages.Id) + @($Reference.Messages.Id) | Where-Object { $_ } | Sort-Object -Unique)
    $rows = @(foreach ($id in $ids) {
        $freshCount = @($Reference.Messages | Where-Object Id -eq $id).Count
        $proofs = @()
        if ($freshCount -gt 0) {
            $proofs += [pscustomobject]@{ Source = 'fresh'; RunId = $FreshArtifact.RunId; ValidationPolicy = $FreshArtifact.ValidationPolicy; LayerHash = $FreshArtifact.LayerHash }
        }
        # Historical evidence admits message IDs only. Leaks are NEVER admitted against the union.
        if ($id -ne 'VUID-vkDestroyDevice-device-05137') {
            foreach ($artifact in $VerifiedArtifacts) {
                if ($artifact.Verified -and $artifact.RunId -ne $FreshArtifact.RunId -and $artifact.Ids -contains $id) {
                    $proofs += [pscustomobject]@{ Source = $artifact.RunId; RunId = $artifact.RunId; ValidationPolicy = $artifact.ValidationPolicy; LayerHash = $artifact.LayerHash }
                }
            }
        }
        [pscustomobject]@{
            Id = $id; Baseline = $freshCount; Current = @($Current.Messages | Where-Object Id -eq $id).Count
            ProvenBy = $(if ($proofs.Count -gt 0) { $proofs.Source -join ', ' } else { 'unproven' })
            Proofs = $proofs
        }
    })
    $owned = @($Current.Messages | Where-Object { $_.NamesRimLumenOwnedHandle -or $_.InsideRimLumenCommands }).Count
    $newIds = @($rows | Where-Object { $_.Current -gt 0 -and $_.Proofs.Count -eq 0 } | ForEach-Object Id)
    [pscustomobject]@{
        Passed = ($owned -eq 0 -and $Current.LeakedObjectCount -le $Reference.LeakedObjectCount -and $newIds.Count -eq 0)
        OwnedMessageCount = $owned; NewIds = $newIds; Rows = $rows
        FreshBaselineRunId = $FreshArtifact.RunId
        BaselineLeaks = $Reference.LeakedObjectCount; CurrentLeaks = $Current.LeakedObjectCount
    }
}

function Test-BaselineCapture($Proof, [bool]$Exited, [bool]$LeaksParsed) {
    $missing = @()
    if (-not $Proof.LayerInserted) { $missing += 'stderr Inserted device layer line' }
    if (-not $Proof.HostFeaturesLogged) { $missing += 'Player.log Enabling vulkan validation features line' }
    if (-not $Proof.WaylithHashMatchesReviewedSource) { $missing += 'Waylith.dll hash differs from reviewed source' }
    if (-not $Exited) { $missing += 'process did not exit normally' }
    if (-not $LeaksParsed) { $missing += 'leaked-object count not parsed' }
    [pscustomobject]@{ Passed = ($missing.Count -eq 0); Missing = $missing }
}

function Get-LayerSettingsProof([string]$OutputText, [string]$ExpectedLog, [bool]$LogExists, [object[]]$HazardMessages) {
    $expected = [IO.Path]::GetFullPath($ExpectedLog).Replace('\', '/')
    $echoes = @([regex]::Matches($OutputText, '(?m)Validation Layer Info - Logging validation error to ([^\r\n]+)') |
        Where-Object { $_.Groups[1].Value.Trim().Trim('"').Replace('\', '/') -eq $expected } | ForEach-Object Value)
    $withoutExtra = @($HazardMessages | Where-Object { $_ -notmatch '\[Extra properties\]' }).Count
    [pscustomobject]@{
        Passed = ($echoes.Count -gt 0 -and $LogExists -and $withoutExtra -eq 0)
        LoggingEcho = $echoes; LogExists = $LogExists
        HazardMessagesChecked = $HazardMessages.Count; HazardsMissingExtraProperties = $withoutExtra
        ShaderAccessHeuristic = 'Not directly observed. Applied via the same file; sibling log_filename/debug_action/extra-properties keys provide the runtime evidence.'
        MessageLimit = 'Requested enable_message_limit=false via the same file; no duplicate-limit environment override.'
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
    $goodProof = [pscustomobject]@{ LayerInserted=$true; HostFeaturesLogged=$true; WaylithHashMatchesReviewedSource=$true }
    if (-not (Test-BaselineCapture $goodProof $true $true).Passed) { throw 'Complete baseline proof rejected.' }
    if ((Test-BaselineCapture $goodProof $true $true).Missing.Count -ne 0) { throw 'Complete baseline reported missing parts.' }
    foreach ($missingCase in @('LayerInserted','HostFeaturesLogged','WaylithHashMatchesReviewedSource')) {
        $bad = [pscustomobject]@{ LayerInserted=$true; HostFeaturesLogged=$true; WaylithHashMatchesReviewedSource=$true }
        $bad.$missingCase = $false
        if ((Test-BaselineCapture $bad $true $true).Passed) { throw "Baseline accepted without $missingCase." }
    }
    if ((Test-BaselineCapture $goodProof $false $true).Passed) { throw 'Baseline accepted without normal exit.' }
    if ((Test-BaselineCapture $goodProof $true $false).Passed) { throw 'Baseline accepted without parsed leak count.' }
    $log = [IO.Path]::GetFullPath('validation.log')
    $echo = "Validation Layer Info - Logging validation error to $log"
    if (-not (Get-LayerSettingsProof $echo $log $true @()).Passed) { throw 'Settings proof with no hazards rejected.' }
    if ((Get-LayerSettingsProof '' $log $true @()).Passed) { throw 'Settings accepted without echo.' }
    if ((Get-LayerSettingsProof ($echo + '.wrong') $log $true @()).Passed) { throw 'Settings echo from a different file accepted.' }
    if ((Get-LayerSettingsProof $echo $log $false @()).Passed) { throw 'Settings accepted without log file.' }
    if ((Get-LayerSettingsProof $echo $log $true @('SYNC-HAZARD-WRITE-AFTER-WRITE')).Passed) { throw 'Hazard without extra properties accepted.' }
    if (-not (Get-LayerSettingsProof $echo $log $true @('SYNC-HAZARD-WRITE-AFTER-WRITE [Extra properties]')).Passed) { throw 'Complete settings proof rejected.' }
    if ((Get-LayerSettingsProof $echo $log $true @('SYNC-HAZARD [Extra properties]', 'SYNC-HAZARD')).Passed) { throw 'Only one of two hazards has extra properties.' }
    # Exercise the artifact reader on its actual file seam; these temporary fixtures are not device evidence.
    $fixtures = Join-Path $env:LOCALAPPDATA ('Temp/opencode/rimlumen-union-test-' + [guid]::NewGuid().ToString('N'))
    try {
        foreach ($fixture in @(
            @{ Name='old-vanilla'; Baseline=$true; Mods='<li>ludeon.rimworld</li>'; Log='vanilla'; Id='OLD-ONLY' },
            @{ Name='bad-mods'; Baseline=$true; Mods='<li>ludeon.rimworld</li><li>nekoyukari.rimlumen</li>'; Log='vanilla'; Id='BAD-MODS-ONLY' },
            @{ Name='not-baseline'; Baseline=$false; Mods='<li>ludeon.rimworld</li>'; Log='vanilla'; Id='NOT-BASELINE-ONLY' },
            @{ Name='installed'; Baseline=$true; Mods='<li>ludeon.rimworld</li>'; Log='[RimLumen] Installed: pass=6'; Id='INSTALLED-ONLY' },
            @{ Name='malformed'; Baseline=$true; Mods='<li>ludeon.rimworld</li>'; Log='vanilla'; Id='MALFORMED-ONLY' },
            @{ Name='missing'; Baseline=$true; Mods='<li>ludeon.rimworld</li>'; Log='vanilla'; Id='MISSING-ONLY' }
        )) {
            $dir = Join-Path $fixtures $fixture.Name
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            @{ Baseline=$fixture.Baseline; ValidationPolicy='older-policy'; LayerHash='older-layer' } | ConvertTo-Json | Set-Content (Join-Path $dir 'run-result.json')
            "<ModsConfigData><activeMods>$($fixture.Mods)</activeMods></ModsConfigData>" | Set-Content (Join-Path $dir 'ModsConfig.xml')
            $fixture.Log | Set-Content (Join-Path $dir 'Player.log')
            @{ Messages=@(@{Id=$fixture.Id}); LeakedObjectCount=20 } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $dir 'validation-report.json')
        }
        '{broken' | Set-Content (Join-Path $fixtures 'malformed/validation-report.json')
        Remove-Item -LiteralPath (Join-Path $fixtures 'missing/validation-report.json')
        $union = Get-VanillaBaselineUnion $fixtures
        if ($union.VerifiedArtifacts.Count -ne 1 -or $union.VerifiedArtifacts[0].RunId -ne 'old-vanilla') { throw 'Unverified vanilla artifact was admitted.' }
        if ($union.SkippedArtifacts.Count -ne 5 -or @($union.SkippedArtifacts | Where-Object { -not $_.Reason }).Count -gt 0) { throw 'Skipped artifacts/reasons missing.' }
        $fresh = [pscustomobject]@{RunId='fresh-run';ValidationPolicy='current-policy';LayerHash='current-layer'}
        $olderId = [pscustomobject]@{Id='OLD-ONLY';NamesRimLumenOwnedHandle=$false;InsideRimLumenCommands=$false}
        $current = [pscustomobject]@{Messages=@($olderId);LeakedObjectCount=6}
        $decision = Compare-ValidationReport $current $reference $union.VerifiedArtifacts $fresh
        if (-not $decision.Passed) { throw 'Verified older vanilla ID rejected.' }
        $row = $decision.Rows | Where-Object Id -eq 'OLD-ONLY'
        if ($row.ProvenBy -ne 'old-vanilla') { throw 'Proving run id missing.' }
        if ($row.Proofs[0].ValidationPolicy -ne 'older-policy' -or $row.Proofs[0].LayerHash -ne 'older-layer') { throw 'Historical policy/hash provenance missing.' }
        $decision.Rows | Format-Table Id, Baseline, Current, ProvenBy -AutoSize -Wrap | Out-String -Width 240 | Write-Host
        foreach ($unknown in @('BAD-MODS-ONLY', 'NOT-BASELINE-ONLY', 'ABSENT-EVERYWHERE')) {
            $current.Messages = @([pscustomobject]@{Id=$unknown;NamesRimLumenOwnedHandle=$false;InsideRimLumenCommands=$false})
            if ((Compare-ValidationReport $current $reference $union.VerifiedArtifacts $fresh).Passed) { throw "Unproven ID admitted: $unknown" }
        }
        $current.Messages=@($olderId); $current.LeakedObjectCount=7
        if ((Compare-ValidationReport $current $reference $union.VerifiedArtifacts $fresh).Passed) { throw 'Old baseline leaks20 hid an increase over fresh6.' }
        $current.LeakedObjectCount=6; $olderId.NamesRimLumenOwnedHandle=$true
        if ((Compare-ValidationReport $current $reference $union.VerifiedArtifacts $fresh).Passed) { throw 'Historical ID hid an owned handle.' }
        $olderId.NamesRimLumenOwnedHandle=$false; $olderId.InsideRimLumenCommands=$true
        if ((Compare-ValidationReport $current $reference $union.VerifiedArtifacts $fresh).Passed) { throw 'Historical ID hid an owned command.' }
    }
    finally { if (Test-Path -LiteralPath $fixtures) { Remove-Item -LiteralPath $fixtures -Recurse -Force } }
    Write-Host 'REPORT GATE: 30 assertions passed; temporary artifact fixtures removed; no game/process/profile operations performed.'
    exit 0
}

if ($Baseline -and $NoBarrier) { throw '-Baseline and -NoBarrier are mutually exclusive.' }
if (@(Get-RimWorldProcess).Count -ne 0) { throw 'Preflight refused: a RimWorld process is already running.' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
Write-Host "Artifacts: $out"
try {
    if (-not $Baseline) {
        $layerHash = (Get-FileHash (Join-Path $layerRoot 'VkLayer_khronos_validation.dll')).Hash
        $vanillaUnion = Get-VanillaBaselineUnion (Join-Path $repoRoot '.scratch/probe-results')
        $vanillaUnion | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'vanilla-baseline-union.json') -Encoding utf8
        foreach ($skipped in $vanillaUnion.SkippedArtifacts) { Write-Host "Skipped vanilla artifact $($skipped.RunId): $($skipped.Reason)" }
        foreach ($artifact in $vanillaUnion.VerifiedArtifacts) { Write-Host "Verified vanilla artifact $($artifact.RunId): policy=$($artifact.ValidationPolicy); layer=$($artifact.LayerHash); ids=$($artifact.Ids -join ', ')" }
        foreach ($candidate in $vanillaUnion.VerifiedArtifacts) {
            $run = Get-Content -LiteralPath (Join-Path $candidate.Directory 'run-result.json') -Raw | ConvertFrom-Json
            if ($run.BaselineCaptured -and $run.HostEnableProofPassed -and $run.LayerSettingsProof.Passed -and $run.QuitPath -eq $quitPath -and $run.ValidationPolicy -eq $validationPolicy -and $run.LayerHash -eq $layerHash -and $run.WaylithHash -eq $expectedWaylithHash) {
                $freshArtifact = $candidate
                $baselinePath = Join-Path $candidate.Directory 'validation-report.json'
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
    # File namespace comes from VK_LAYER_KHRONOS_validation, NOT the manifest's "prefix": "vvl".
    # VUL layer_settings_util.cpp:40-48; see .scratch/findings-vvl-settings.md.
    $settings = @"
khronos_validation.validate_sync = true
khronos_validation.syncval_shader_accesses_heuristic = true
khronos_validation.syncval_message_extra_properties = true
khronos_validation.report_flags = error,warn,info
khronos_validation.debug_action = VK_DBG_LAYER_ACTION_LOG_MSG
khronos_validation.log_filename = $($out.Replace('\', '/'))/validation.log
khronos_validation.enable_message_limit = false
"@
    [IO.File]::WriteAllText((Join-Path $out 'vk_layer_settings.txt'), $settings, [Text.UTF8Encoding]::new($false))

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $gameRoot 'RimWorld.exe'
    $start.WorkingDirectory = $gameRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoWindow', '-NoCrashDialog', '-quicktest', '-WindowSize=1920,1080', '-EnableVulkanValidation', "-savedatafolder=$profile")) {
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
    $start.Environment.Remove('VK_LAYER_DUPLICATE_MESSAGE_LIMIT') | Out-Null
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
    if ($Baseline -and $process.ExitCode -ne 0) { throw "Baseline process exited $($process.ExitCode)" }
    if (-not $Baseline) {
        $resultPath = Join-Path $out 'probe-result.json'
        if (-not (Test-Path -LiteralPath $resultPath)) { throw 'RimWorld did not write probe-result.json.' }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.QuitPath -ne $quitPath) { throw 'Probe quit path differs from baseline.' }
        if ($process.ExitCode -ne $result.ExpectedProcessExitCode) { throw "Unexpected process exit $($process.ExitCode); probe expected $($result.ExpectedProcessExitCode)." }
        Write-Host "Process exit=$($process.ExitCode); probe Passed=$($result.Passed); expected=$($result.ExpectedProcessExitCode)"
        if ($result.LogRoot) { $logRoot = $result.LogRoot }
        foreach ($check in $result.Checks) { Write-Host "$($check.Id): Passed=$($check.Passed) $($check.Detail)" }
        $missingChecks = @(foreach ($required in @('T1a', 'T1b', 'T2', 'T3', 'T4', 'P1-idle', 'P1-active', 'P2-L2-10k', 'P2-L2-50k', 'P2-L1-Glower', 'Compat-VanillaAdditive', 'feature-order', 'validation-layer')) {
            $check = @($result.Checks | Where-Object Id -eq $required)
            if ($check.Count -ne 1 -or -not $check[0].Passed) { $required }
        })
        if ($missingChecks.Count -gt 0 -or -not $result.Passed -or @($result.Checks | Where-Object { -not $_.Passed }).Count -gt 0) {
            throw "Failed/absent required checks: $($missingChecks -join ', '). See all per-check results above."
        }
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
            $playerText = [IO.File]::ReadAllText($playerLog)
            $report = Get-ValidationReport $playerText
            $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'validation-report.json') -Encoding utf8
            $validationErrors = @($report.Messages | ForEach-Object Header)
            $syncHazards = @($report.Messages | Where-Object Id -like 'SYNC-HAZARD*' | ForEach-Object Header)
            $syncProof = @($syncHazards | Select-Object -First 1)
            # Source-backed host enable proof: NOT a layer self-report. The layer is demonstrably loaded
            # (loader log) and the reviewed engine code enables SynchronizationValidationEXT through pNext
            # (Waylith GraphicsDevice.cs:233-255), and the exact engine binary is the reviewed one.
            $hostEnableProof = @(Select-String -LiteralPath $playerLog -SimpleMatch 'Enabling vulkan validation features' | ForEach-Object { "Player.log:$($_.LineNumber): $($_.Line)" })
            $layerProof = @(Select-String -LiteralPath $playerLog -SimpleMatch '[RimLumen] ValidationEnabled=True; InstanceEnabledLayers=VK_LAYER_KHRONOS_validation' | ForEach-Object Line)
            if ($Baseline -and $report.InstallSeen) { throw 'Baseline unexpectedly loaded RimLumen.' }
            if ($playerText -match '\[RimLumen\] Vulkan resource shutdown failed:') { throw 'RimLumen resource shutdown failed.' }
        } else { $exitCode = 1; Write-Host 'FAIL: no Player.log collected.' }
        $loaderLog = Join-Path $out 'stderr.log'
        if (Test-Path -LiteralPath $loaderLog) {
            $layerInsertProof = @(Select-String -LiteralPath $loaderLog -SimpleMatch 'Inserted device layer "VK_LAYER_KHRONOS_validation"' | ForEach-Object { "stderr.log:$($_.LineNumber): $($_.Line)" })
            $layerProof += $layerInsertProof
        }
        $waylithDll = Join-Path $gameRoot 'Waylith.dll'
        if (Test-Path -LiteralPath $waylithDll) { $waylithHash = (Get-FileHash -LiteralPath $waylithDll -Algorithm SHA256).Hash }
        $hashMatchesReview = ($waylithHash -eq $expectedWaylithHash)
        $enableProof = [pscustomobject]@{
            LayerInserted = ($layerInsertProof.Count -gt 0)
            HostFeaturesLogged = ($hostEnableProof.Count -gt 0)
            WaylithHashMatchesReviewedSource = $hashMatchesReview
            WaylithHash = $waylithHash; ExpectedWaylithHash = $expectedWaylithHash
            HostLines = $hostEnableProof; LoaderLines = $layerInsertProof
            Detail = 'Not layer self-reported settings. Evidence: loader Inserted device layer line, host "Enabling vulkan validation features" line, and Waylith.dll hash matching the reviewed GraphicsDevice.cs:233-255. Layer status/settings echo, if ever produced, is preferred over this.'
        }
        $processExitOk = ($process -and $process.HasExited -and $process.ExitCode -eq 0)
        $leaksParsed = ($null -ne $report -and $null -ne $report.LeakedObjectCount)
        $hostProof = Test-BaselineCapture $enableProof $true $true
        $baselineProof = Test-BaselineCapture $enableProof $processExitOk $leaksParsed
        if (-not $hostProof.Passed) {
            $exitCode = 1
            Write-Host "FAIL: host enable proof incomplete: $($hostProof.Missing -join ', '). Hash mismatch requires source re-audit."
        }
        $proofOutput = $playerText
        $stdoutLog = Join-Path $out 'stdout.log'
        if (Test-Path -LiteralPath $stdoutLog) { $proofOutput += "`n" + [IO.File]::ReadAllText($stdoutLog) }
        $validationLog = Join-Path $out 'validation.log'
        $hazardMessages = @($report.Messages | Where-Object Id -like 'SYNC-HAZARD*' | ForEach-Object Message)
        # The file callback also sees messages emitted before the host messenger was installed.
        if (Test-Path -LiteralPath $validationLog) {
            $rawValidation = [IO.File]::ReadAllText($validationLog)
            $hazardMessages += @([regex]::Matches($rawValidation, '(?ms)^[^\r\n]*\[\s*SYNC-HAZARD[^\]\r\n]+\].*?(?=^[^\r\n]*\[\s*(?:SYNC-HAZARD|VUID-)|\z)') | ForEach-Object Value)
        }
        $settingsProof = Get-LayerSettingsProof $proofOutput $validationLog (Test-Path -LiteralPath $validationLog) $hazardMessages
        Write-Host "Layer settings applied: $($settingsProof.Passed); logExists=$($settingsProof.LogExists); hazardsMissingExtraProperties=$($settingsProof.HazardsMissingExtraProperties)"
        if (-not $settingsProof.Passed) { $exitCode = 1; Write-Host 'FAIL: settings not applied (logging echo/file/extra-properties proof incomplete).' }
        # Sync validation is no longer inferred from vanilla emitting a hazard: hazards are reported, never required.
        Write-Host "SourceBackedHostEnableProof: Passed=$($hostProof.Passed); missing=$($hostProof.Missing -join ', ')"
        if ($layerProof.Count -eq 0) { $exitCode = 1; Write-Host 'FAIL: no positive validation-layer proof.' }
        if ($Baseline) {
            $baselineCaptured = ($baselineProof.Passed -and $layerProof.Count -gt 0 -and $exitCode -eq 0)
            if (-not $baselineCaptured) { $exitCode = 1; Write-Host "FAIL: baseline not captured: $($baselineProof.Missing -join ', ')" }
            if ($report) { @($report.Groups | Select-Object Id, Phase, Count) | Format-Table -AutoSize | Out-String | Write-Host }
            Write-Host "Baseline leaked objects: $(if ($report) { $report.LeakedObjectCount } else { 'unparsed' })"
        } elseif ($report -and $baselineReport) {
            $comparison = Compare-ValidationReport $report $baselineReport $vanillaUnion.VerifiedArtifacts $freshArtifact
            $comparison.Rows | Format-Table Id, Baseline, Current, ProvenBy -AutoSize -Wrap | Out-String -Width 240 | Write-Host
            Write-Host "Leaked objects: baseline=$($comparison.BaselineLeaks); current=$($comparison.CurrentLeaks); ownedMessages=$($comparison.OwnedMessageCount)"
            if (-not $comparison.Passed) { $exitCode = 1 }
            $comparison | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'baseline-comparison.json') -Encoding utf8
        } else { $exitCode = 1 }
        $validationErrors | Set-Content -LiteralPath (Join-Path $out 'validation-errors.txt') -Encoding utf8
        Write-Host "Validation errors: $($validationErrors.Count); SYNC-HAZARD=$($syncHazards.Count); control=$NoBarrier"
        $layerProof | Select-Object -First 2 | ForEach-Object { Write-Host "Layer proof: $_" }
        $syncProof | Select-Object -First 1 | ForEach-Object { Write-Host "Sync hazard (information only, never required): $_" }
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
            ValidationClean = ($hostProof.Passed -and $settingsProof.Passed -and $validationErrors.Count -eq 0)
            HostEnableProofPassed = $hostProof.Passed
            HostEnableProofMissing = @($hostProof.Missing)
            SourceBackedHostEnableProof = $enableProof
            LayerSettingsProof = $settingsProof
            WaylithHash = $waylithHash; ExpectedWaylithHash = $expectedWaylithHash
            Baseline = [bool]$Baseline; BaselineCaptured = ($baselineCaptured -and $exitCode -eq 0)
            QuitPath = $quitPath; ValidationPolicy = $validationPolicy
            LayerHash = $(if (Test-Path (Join-Path $layerRoot 'VkLayer_khronos_validation.dll')) { (Get-FileHash (Join-Path $layerRoot 'VkLayer_khronos_validation.dll')).Hash } else { $null })
            BaselineReport = $baselinePath; AttributionGatePassed = ($null -ne $comparison -and $comparison.Passed)
            VanillaBaselineUnionReport = $(if ($vanillaUnion) { Join-Path $out 'vanilla-baseline-union.json' } else { $null })
            ProcessExitCode = $(if ($process -and $process.HasExited) { $process.ExitCode } else { $null })
            NoBarrier = [bool]$NoBarrier; LayerProof = $layerProof; SyncProof = $syncProof
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'run-result.json') -Encoding utf8
        Write-Host "Cleanup: junctionExists=$(Test-Path -LiteralPath $junction); RimWorldProcesses=$(@(Get-RimWorldProcess).Count)"
        Write-Host "Duration: $($timer.Elapsed.TotalSeconds.ToString('F3'))s; exit=$exitCode"
        Write-Host "Artifacts: $out"
    }
}
exit $exitCode
