# Locates the RimWorld install: $env:RIMWORLD_DIR first, then every Steam library listed in libraryfolders.vdf.
# Sets $env:RIMWORLD_DIR to the result so MSBuild (Directory.Build.props GameRoot) sees the same directory.
# ASCII-only and PowerShell 5.1 compatible because pack-release.ps1 dot-sources it.
function Resolve-RimWorldDir([string]$Explicit) {
    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:RIMWORLD_DIR) { $candidates += $env:RIMWORLD_DIR }
    $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    if ($steam) {
        $vdf = Join-Path $steam 'steamapps/libraryfolders.vdf'
        $libraries = @($steam)
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -Raw -LiteralPath $vdf), '"path"\s+"([^"]+)"')) {
                $libraries += $m.Groups[1].Value.Replace('\\', '\')
            }
        }
        foreach ($lib in $libraries) { $candidates += (Join-Path $lib 'steamapps/common/RimWorld') }
    }
    foreach ($dir in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $dir 'Assembly-CSharp.dll')) {
            $env:RIMWORLD_DIR = $dir
            return $dir
        }
    }
    throw 'RimWorld install not found. Set the RIMWORLD_DIR environment variable to the RimWorld folder (the one containing Assembly-CSharp.dll).'
}

function Resolve-RimWorldDotnet([string]$Explicit, [string]$RimWorldDir) {
    $dotnet = $Explicit
    if (-not $dotnet) { $dotnet = Join-Path $RimWorldDir 'Moddev/tools/dotnet/dotnet.exe' }
    if (-not (Test-Path -LiteralPath $dotnet)) {
        throw "Portable .NET 10 SDK not found at: $dotnet (RimWorld ships it under Moddev/tools/dotnet; pass -DotnetExe to override)"
    }
    return $dotnet
}
