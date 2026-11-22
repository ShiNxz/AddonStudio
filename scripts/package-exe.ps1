# Copies the single-file app out of the Tauri build: dist/AddonStudio.exe (for testing) and
# dist/AddonStudio-<version>-win-x64.exe (for releases). The .NET core is embedded in it (app/src-tauri/build.rs),
# so this one exe is the whole app: no installer, no extra files.
#
# Run after the two builds:
#   scripts\build-sidecar.ps1 -Rid win-x64
#   cd app; npm ci; npm run tauri build -- --no-bundle
#
param([string]$OutDir = "dist")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root "app/src-tauri/target/release"

# cargo names the binary after the crate; newer Tauri CLIs may name it after productName
$app = @("Addon Studio.exe", "addon-studio.exe", "addon_studio.exe") |
    ForEach-Object { Join-Path $release $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $app) { throw "No app exe in $release. Run 'npm run tauri build -- --no-bundle' in app/ first." }

$version = (Get-Content (Join-Path $root "app/package.json") -Raw | ConvertFrom-Json).version
$out = Join-Path $root $OutDir
New-Item -ItemType Directory -Force $out | Out-Null

Copy-Item $app (Join-Path $out "AddonStudio.exe") -Force
Copy-Item $app (Join-Path $out "AddonStudio-$version-win-x64.exe") -Force

Get-ChildItem $out -Filter "AddonStudio*.exe" | Format-Table Name, Length
