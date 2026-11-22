param(
    [int]$Port = 4173
)

$ErrorActionPreference = 'Stop'
$AppDir = Join-Path $PSScriptRoot 'app'
$DistDir = Join-Path $AppDir 'dist'

if (-not (Test-Path (Join-Path $DistDir 'index.html'))) {
    throw "未找到构建产物 '$DistDir'，请先运行: pwsh -NoProfile -File WebEditor/build-editor.ps1"
}

Write-Host "[WebEditor] Serving $DistDir at http://127.0.0.1:$Port/editor/ (Ctrl+C to stop)"
Push-Location $AppDir
try {
    npx vite preview --host 127.0.0.1 --port $Port --strictPort
}
finally {
    Pop-Location
}
