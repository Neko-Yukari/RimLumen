#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetExe = 'E:/SteamLibrary/steamapps/common/RimWorld/Moddev/tools/dotnet/dotnet.exe',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $repoRoot 'RimLumen.sln'

if (-not (Test-Path -LiteralPath $DotnetExe)) {
    throw "Portable .NET 10 SDK not found at: $DotnetExe"
}

$env:DOTNET_CLI_HOME = Join-Path $env:LOCALAPPDATA 'Temp/opencode/rimlumen-dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

if ($Clean) {
    & $DotnetExe clean $sln -c $Configuration --nologo -v minimal
}

Write-Host "SDK: $(& $DotnetExe --version)"
& $DotnetExe build $sln -c $Configuration --nologo -v minimal /warnaserror
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$coreDll = Join-Path $repoRoot '1.7/Assemblies/RimLumen.Core.dll'
$adapterDll = Join-Path $repoRoot '1.7/Compat/RimUnity/Assemblies/RimLumen.Adapter.RimUnity.dll'
Write-Host "BUILD OK: Core=$( (Get-Item $coreDll).Length )B, Adapter=$( (Get-Item $adapterDll).Length )B"
