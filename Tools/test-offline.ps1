#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Filter = '',
    [string]$DotnetExe = 'E:/SteamLibrary/steamapps/common/RimWorld/Moddev/tools/dotnet/dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProjects = @(
    (Join-Path $repoRoot 'Tests/RimLumen.Core.Tests/RimLumen.Core.Tests.csproj'),
    (Join-Path $repoRoot 'Tests/RimLumen.Adapter.RimUnity.Tests/RimLumen.Adapter.RimUnity.Tests.csproj')
)

if (-not (Test-Path -LiteralPath $DotnetExe)) {
    throw "Portable .NET 10 SDK not found at: $DotnetExe"
}

$env:DOTNET_CLI_HOME = Join-Path $env:LOCALAPPDATA 'Temp/opencode/rimlumen-dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

foreach ($testProj in $testProjects) {
    $runArgs = @('run', '--project', $testProj, '-c', $Configuration, '--')
    if ($Filter) {
        $runArgs += @('--filter', $Filter)
    }

    & $DotnetExe @runArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Offline tests failed for $testProj with exit code $LASTEXITCODE"
    }
}
