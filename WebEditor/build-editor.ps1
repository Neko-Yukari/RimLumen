param(
    [switch]$SkipWasm,
    [switch]$SkipNpmInstall
)

$ErrorActionPreference = 'Stop'
$WebEditorDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent $WebEditorDir
$WebSimProj = Join-Path $WebEditorDir 'RimLumen.WebSim/RimLumen.WebSim.csproj'
$PublishDir = Join-Path $WebEditorDir 'RimLumen.WebSim/spike-out/aot'
$AppDir = Join-Path $WebEditorDir 'app'
$WasmTargetDir = Join-Path $AppDir 'public/wasm/_framework'

$IsolatedDotnetRoot = Join-Path $env:LOCALAPPDATA 'rimlumen-dotnet-wasm'
if (-not $env:DOTNET_ROOT -and (Test-Path (Join-Path $IsolatedDotnetRoot 'dotnet.exe'))) {
    $env:DOTNET_ROOT = $IsolatedDotnetRoot
}
$DotnetExe = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    Join-Path $env:DOTNET_ROOT 'dotnet.exe'
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

if (-not $SkipWasm) {
    Write-Host "[WebEditor] Publishing RimLumen.WebSim (AOT + SIMD) using $DotnetExe ..."
    if (Test-Path $PublishDir) {
        Remove-Item -Recurse -Force $PublishDir
    }
    & $DotnetExe publish $WebSimProj -c Release -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $SourceFramework = Join-Path $PublishDir 'wwwroot/_framework'
    if (-not (Test-Path $SourceFramework)) {
        throw "Expected published WASM framework directory not found: $SourceFramework"
    }
    if (Test-Path $WasmTargetDir) {
        Remove-Item -Recurse -Force $WasmTargetDir
    }
    New-Item -ItemType Directory -Force -Path $WasmTargetDir | Out-Null
    Copy-Item -Path (Join-Path $SourceFramework '*') -Destination $WasmTargetDir -Recurse -Force
    Write-Host "[WebEditor] Synced WASM runtime to $WasmTargetDir"
}

Push-Location $AppDir
try {
    if (-not $SkipNpmInstall) {
        if (-not (Test-Path 'node_modules') -or -not (Test-Path 'package-lock.json')) {
            npm install
            if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
        }
    }

    Write-Host "[WebEditor] Running TypeScript check + Vite production build ..."
    npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host "[WebEditor] BUILD OK: $((Join-Path $AppDir 'dist'))"
Write-Host "[WebEditor] Run 'pwsh -NoProfile -File WebEditor/serve-editor.ps1' to preview locally."
