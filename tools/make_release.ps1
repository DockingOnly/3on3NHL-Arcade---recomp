<#
.SYNOPSIS
    Assembles a release folder: the launcher plus the build kit it compiles the game from.

.DESCRIPTION
    The release contains no game code or data, and no compiled game either: the launcher
    builds nhl3on3.exe on the player's PC from their own copy of the game. What ships:

      NHL 3on3 Launcher.exe     the launcher (self-contained .NET 8)
      buildkit\kit.json         kit version; the launcher offers a rebuild when it changes
      buildkit\source\          this repository's sources (CMakeLists.txt, src\, *.toml, ...)
      buildkit\sdk\             the installed, patched ReXGlue SDK, Release files only
      README.txt, licenses\

.PARAMETER SdkDir
    The installed ReXGlue SDK (built with this repository's patches, see BUILDING.md).

.PARAMETER Version
    Release version, written to buildkit\kit.json. Defaults to the launcher's <Version>.

.EXAMPLE
    pwsh tools/make_release.ps1 -SdkDir ..\rexglue-sdk\out\install\win-amd64 -Zip
#>
param(
    [string]$SdkDir = (Join-Path $PSScriptRoot '..\..\rexglue-sdk\out\install\win-amd64'),
    [string]$Version,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\out\release'),
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SdkDir = (Resolve-Path $SdkDir).Path
if (-not (Test-Path (Join-Path $SdkDir 'lib\cmake\rexglue\rexglueConfig.cmake'))) {
    throw "$SdkDir is not an installed ReXGlue SDK (lib\cmake\rexglue\rexglueConfig.cmake is missing)."
}
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repo 'launcher\Nhl3on3Launcher.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
}

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

Write-Host "Publishing the launcher..."
dotnet publish (Join-Path $repo 'launcher') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:Version=$Version -o $OutDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- build kit: sources -------------------------------------------------------------
$kit = Join-Path $OutDir 'buildkit'
$src = Join-Path $kit 'source'
Write-Host "Copying the port's sources..."
New-Item -ItemType Directory -Force (Join-Path $src 'generated') | Out-Null
foreach ($f in 'CMakeLists.txt', 'nhl3on3.toml', 'nhl3on3_functions.toml', 'nhl3on3_manifest.toml', 'LICENSE') {
    Copy-Item (Join-Path $repo $f) $src
}
Copy-Item (Join-Path $repo 'generated\rexglue.cmake') (Join-Path $src 'generated')
Copy-Item -Recurse (Join-Path $repo 'src') $src
Copy-Item -Recurse (Join-Path $repo 'thirdparty') $src
if (Test-Path (Join-Path $repo 'metadata')) { Copy-Item -Recurse (Join-Path $repo 'metadata') $src }

# --- build kit: SDK, Release files only ---------------------------------------------
Write-Host "Copying the ReXGlue SDK (Release files)..."
$sdk = Join-Path $kit 'sdk'
function Test-DebugVariant([System.IO.FileInfo]$file) {
    if ($file.Name -match '-(debug|relwithdebinfo)\.cmake$') { return $true }
    if ($file.Extension -notin '.dll', '.lib', '.pdb', '.exe') { return $false }
    foreach ($suffix in 'rd', 'd') {
        if ($file.BaseName.EndsWith($suffix)) {
            $release = $file.BaseName.Substring(0, $file.BaseName.Length - $suffix.Length) + $file.Extension
            if (Test-Path (Join-Path $file.DirectoryName $release)) { return $true }
        }
    }
    return $false
}
# Header folders the game's build never includes (checked with 'ninja -t deps').
$unusedHeaders = 'dxc', 'renderdoc', 'disruptorplus', 'tracy' |
    ForEach-Object { (Join-Path $SdkDir 'include' $_) + [IO.Path]::DirectorySeparatorChar }
Get-ChildItem -Recurse -File $SdkDir | Where-Object {
    $file = $_
    -not (Test-DebugVariant $file) -and $file.Extension -ne '.pdb' -and
    -not ($unusedHeaders | Where-Object { $file.FullName.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })
} | ForEach-Object {
    $dest = Join-Path $sdk $_.FullName.Substring($SdkDir.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $_.FullName $dest
}

# The SDK's CMake package lists those folders as include directories and stops if they don't
# exist, so they ship empty.
foreach ($name in 'dxc', 'renderdoc', 'disruptorplus', 'tracy') {
    $dir = Join-Path $sdk "include\$name"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content (Join-Path $dir 'README.txt') "Left empty on purpose: the game's build includes no headers from here."
}

@{ version = $Version } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $kit 'kit.json')

# --- player files --------------------------------------------------------------------
Copy-Item (Join-Path $repo 'dist\README.txt') $OutDir
Copy-Item -Recurse (Join-Path $repo 'dist\licenses') (Join-Path $OutDir 'licenses')
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $OutDir 'licenses\nhl3on3.txt')

# --- guard: nothing from the game, and no prebuilt game ------------------------------
$forbidden = Get-ChildItem -Recurse -File $OutDir | Where-Object {
    $_.Extension -in '.xex', '.big', '.bh', '.viv' -or $_.Name -in 'nhl3on3.exe', 'ArcadeInfo.xml'
}
if ($forbidden) { throw "Game files or a prebuilt game ended up in the release: $($forbidden.FullName -join ', ')" }
if (Test-Path (Join-Path $src 'generated\default')) { throw 'Generated code ended up in the release.' }

$size = (Get-ChildItem -Recurse -File $OutDir | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Release {0} assembled in {1} ({2:N0} MB)" -f $Version, $OutDir, $size)

if ($Zip) {
    $zipPath = Join-Path (Split-Path $OutDir) "nhl3on3-$Version-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("Zipped to {0} ({1:N0} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
}
