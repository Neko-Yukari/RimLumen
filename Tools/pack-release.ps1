# Packages RimLumen into a clean, upload-ready Workshop mod folder (default: Dist\RimLumen).
# ASCII-only source so Windows PowerShell 5.1 and pwsh 7 parse it identically; child scripts run under pwsh.
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$DotnetExe = 'E:/SteamLibrary/steamapps/common/RimWorld/Moddev/tools/dotnet/dotnet.exe',
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) (Join-Path 'Dist' 'RimLumen'))
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$MaxPreviewBytes = 1MB

function Invoke-Pwsh([string]$ScriptName, [string]$What) {
    $script = Join-Path $PSScriptRoot $ScriptName
    & pwsh -NoProfile -File $script
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE ($ScriptName)"
    }
}

function Get-RelativePath([string]$Root, [string]$Path) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $pathFull = [IO.Path]::GetFullPath($Path)
    return $pathFull.Substring($rootFull.Length).TrimStart('\', '/')
}

function Copy-ToPackage([string]$RelativePath) {
    $src = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
        throw "Release input missing: $RelativePath"
    }
    $dst = Join-Path $script:PackageRoot $RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst -Force
}

function Copy-FilesToPackage([string]$RelativeDir) {
    $srcDir = Join-Path $RepoRoot $RelativeDir
    if (-not (Test-Path -LiteralPath $srcDir -PathType Container)) {
        throw "Release input directory missing: $RelativeDir"
    }
    $files = @(Get-ChildItem -LiteralPath $srcDir -File -Recurse)
    if ($files.Count -eq 0) {
        throw "Release input directory is empty: $RelativeDir"
    }
    foreach ($file in $files) {
        Copy-ToPackage (Get-RelativePath $RepoRoot $file.FullName)
    }
}

# Step 1: build
Write-Host '== [1/5] build =='
Invoke-Pwsh 'build.ps1' 'Build'

# Step 2: offline tests
if ($SkipTests) {
    Write-Host '== [2/5] offline tests: SKIPPED (-SkipTests) =='
}
else {
    Write-Host '== [2/5] offline tests =='
    Invoke-Pwsh 'test-offline.ps1' 'Offline tests'
}

# Step 3: Workshop preview (640x360 PNG, must stay under 1 MB)
Write-Host '== [3/5] About/Preview.png =='
$previewPath = Join-Path $RepoRoot (Join-Path 'About' 'Preview.png')
$previewSource = Join-Path $RepoRoot (Join-Path '.scratch' 'showcase-preview.png')
if (-not (Test-Path -LiteralPath $previewPath -PathType Leaf)) {
    if (-not (Test-Path -LiteralPath $previewSource -PathType Leaf)) {
        throw 'About/Preview.png is missing and no source screenshot exists at .scratch/showcase-preview.png'
    }
    if ((Get-Item -LiteralPath $previewSource).Length -lt $MaxPreviewBytes) {
        Copy-Item -LiteralPath $previewSource -Destination $previewPath -Force
    }
    else {
        Add-Type -AssemblyName System.Drawing
        $image = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $previewSource).Path)
        try {
            $bitmap = New-Object System.Drawing.Bitmap 640, 360, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                    $graphics.DrawImage($image, 0, 0, 640, 360)
                }
                finally { $graphics.Dispose() }
                $bitmap.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $bitmap.Dispose() }
        }
        finally { $image.Dispose() }
    }
    Write-Host "generated $previewPath"
}
else {
    Write-Host 'About/Preview.png already present, keeping it'
}

# Step 4: assemble the distributable folder
Write-Host "== [4/5] assemble $OutDir =="
$outFull = [IO.Path]::GetFullPath($OutDir).TrimEnd('\', '/')
$repoFull = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
$sep = [string][IO.Path]::DirectorySeparatorChar
$distFull = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'Dist')).TrimEnd('\', '/')
# Inside the repo only a subdirectory of Dist may be wiped (never Source, 1.7, Tests, Tools, About, docs, .git, ...).
$insideRepo = $outFull.StartsWith($repoFull + $sep, [StringComparison]::OrdinalIgnoreCase)
$insideDist = $outFull.StartsWith($distFull + $sep, [StringComparison]::OrdinalIgnoreCase)
$unsafe = ($outFull -eq $repoFull) -or
    $repoFull.StartsWith($outFull + $sep, [StringComparison]::OrdinalIgnoreCase) -or
    ($outFull.Length -le 3) -or
    ($insideRepo -and -not $insideDist)
if ($unsafe) {
    throw "Refusing to wipe unsafe output directory: $outFull (inside the repo only a subdirectory of Dist is allowed)"
}
if (Test-Path -LiteralPath $outFull) {
    Remove-Item -LiteralPath $outFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outFull | Out-Null
$script:PackageRoot = $outFull

foreach ($relative in @(
        'About/About.xml',
        'About/Preview.png',
        'LoadFolders.xml',
        'README.md',
        'docs/api.md',
        '1.7/Compat/RimUnity/Assemblies/RimLumen.Adapter.RimUnity.dll')) {
    Copy-ToPackage $relative
}
if (Test-Path -LiteralPath (Join-Path $RepoRoot 'About/PublishedFileId.txt') -PathType Leaf) {
    Copy-ToPackage 'About/PublishedFileId.txt'
}

# Build a stripped Core DLL (without Probes/, Showcase/, ProbeOptions) into a temp dir so dev 1.7/Assemblies stays test-ready.
if (-not (Test-Path -LiteralPath $DotnetExe)) {
    throw "Portable .NET 10 SDK not found at: $DotnetExe"
}
$env:DOTNET_CLI_HOME = Join-Path $env:LOCALAPPDATA 'Temp/opencode/rimlumen-dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$shipStage = Join-Path $RepoRoot ('.scratch/ship-stage-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $shipStage | Out-Null
try {
    $coreProj = Join-Path $RepoRoot 'Source/RimLumen.Core/RimLumen.Core.csproj'
    $stageSlash = $shipStage.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    & $DotnetExe build $coreProj -c Release -p:RimLumenShipRelease=true "-p:OutDir=$stageSlash" "-p:OutputPath=$stageSlash" --nologo -v minimal /warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw "Stripped Core release build failed with exit code $LASTEXITCODE"
    }
    $strippedCore = Join-Path $shipStage 'RimLumen.Core.dll'
    $coreDst = Join-Path $script:PackageRoot '1.7/Assemblies/RimLumen.Core.dll'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $coreDst) | Out-Null
    Copy-Item -LiteralPath $strippedCore -Destination $coreDst -Force
}
finally {
    if (Test-Path -LiteralPath $shipStage) {
        Remove-Item -LiteralPath $shipStage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Copy-FilesToPackage '1.7/Defs'
Copy-FilesToPackage '1.7/Textures'
Copy-FilesToPackage '1.7/ShaderPacks'
Copy-FilesToPackage '1.7/Shaders/Particles'
Copy-FilesToPackage '1.7/Shaders/PostFx'

# Step 5: verification gate
Write-Host '== [5/5] verify =='
$failures = New-Object System.Collections.Generic.List[string]

foreach ($required in @(
        'About/About.xml',
        'About/Preview.png',
        'LoadFolders.xml',
        'docs/api.md',
        '1.7/Assemblies/RimLumen.Core.dll',
        '1.7/Compat/RimUnity/Assemblies/RimLumen.Adapter.RimUnity.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $outFull $required) -PathType Leaf)) {
        $failures.Add("missing required file: $required")
    }
}

$packedPreview = Join-Path $outFull 'About/Preview.png'
if (Test-Path -LiteralPath $packedPreview -PathType Leaf) {
    $previewBytes = (Get-Item -LiteralPath $packedPreview).Length
    if ($previewBytes -ge $MaxPreviewBytes) {
        $failures.Add("About/Preview.png is $previewBytes bytes, must be < $MaxPreviewBytes")
    }
}

foreach ($shaderDir in @('Particles', 'PostFx')) {
    $srcShaders = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "1.7/Shaders/$shaderDir") -File -Recurse |
            Where-Object { $_.Extension -in @('.glsl', '.glslinc') })
    if ($srcShaders.Count -eq 0) {
        $failures.Add("no runtime shaders found in 1.7/Shaders/$shaderDir")
    }
    foreach ($shader in $srcShaders) {
        $relative = Get-RelativePath $RepoRoot $shader.FullName
        if (-not (Test-Path -LiteralPath (Join-Path $outFull $relative) -PathType Leaf)) {
            $failures.Add("missing shader: $relative")
        }
    }
}

foreach ($dataDir in @('1.7/Defs', '1.7/Textures', '1.7/ShaderPacks')) {
    $srcData = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot $dataDir) -File -Recurse)
    if ($srcData.Count -eq 0) {
        $failures.Add("no files found in $dataDir")
    }
    foreach ($dataFile in $srcData) {
        $relative = Get-RelativePath $RepoRoot $dataFile.FullName
        if (-not (Test-Path -LiteralPath (Join-Path $outFull $relative) -PathType Leaf)) {
            $failures.Add("missing data file: $relative")
        }
    }
}

$packed = @(Get-ChildItem -LiteralPath $outFull -File -Recurse)
foreach ($file in $packed) {
    $relative = Get-RelativePath $outFull $file.FullName
    $forbidden = ($file.Extension -in @('.cs', '.pdb', '.sln', '.csproj')) -or
        ($file.Name -like '*.deps.json') -or
        ($relative -match 'Probes')
    if ($forbidden) {
        $failures.Add("forbidden file in package: $relative")
    }
    if (($relative.Replace('\', '/') -like '1.7/ShaderPacks/*') -and
        ($file.Extension -notin @('.json', '.glsl', '.glslinc'))) {
        $failures.Add("unexpected file type in 1.7/ShaderPacks (allowed: .json .glsl .glslinc): $relative")
    }
    if ($relative.Replace('\', '/') -match '^1\.7/Shaders/PostFx/RimLumen(?:Probe|Identity)') {
        $failures.Add("probe shader must not ship in PostFx: $relative")
    }
    if ($file.Extension -eq '.xml') {
        try {
            $xmlDoc = [System.Xml.XmlDocument]::new()
            $xmlDoc.Load($file.FullName)
        }
        catch {
            $failures.Add("invalid XML ($($_.Exception.Message)): $relative")
        }
    }
}

$packedCoreDll = Join-Path $outFull '1.7/Assemblies/RimLumen.Core.dll'
if (Test-Path -LiteralPath $packedCoreDll -PathType Leaf) {
    $dllBytes = [IO.File]::ReadAllBytes($packedCoreDll)
    $peStream = [IO.MemoryStream]::new($dllBytes)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($peStream)
        try {
            $mdReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            foreach ($handle in $mdReader.TypeDefinitions) {
                $td = $mdReader.GetTypeDefinition($handle)
                $ns = $mdReader.GetString($td.Namespace)
                $name = $mdReader.GetString($td.Name)
                if ($ns -like 'RimLumen.Core.Rendering.Probes*' -or
                    $ns -like 'RimLumen.Core.Showcase*' -or
                    ($ns -eq 'RimLumen.Core.Rendering.Logic' -and $name -like 'ProbeOptions*')) {
                    $failures.Add("dev/test type must not ship in release DLL: $ns.$name")
                }
            }
        }
        finally { $peReader.Dispose() }
    }
    finally { $peStream.Dispose() }
}

$totalBytes = ($packed | Measure-Object -Property Length -Sum).Sum
Write-Host ('files: {0}, bytes: {1}' -f $packed.Count, $totalBytes)
foreach ($file in ($packed | Sort-Object FullName)) {
    Write-Host ('  {0,9}  {1}' -f $file.Length, (Get-RelativePath $outFull $file.FullName))
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "VERIFY FAIL: $failure"
    }
    throw "Release verification failed with $($failures.Count) problem(s)."
}
Write-Host "PACK OK: $outFull ($($packed.Count) files, $totalBytes bytes)"
