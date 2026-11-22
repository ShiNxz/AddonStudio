# Packs the portable Windows build: AddonStudio.exe + addonstudio-core.exe + portable.txt in dist/AddonStudio,
# plus a zip of that folder. Nothing gets installed: unzip anywhere and run AddonStudio.exe.
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

# Tauri copies the sidecar next to the app exe without its target triple
$core = Join-Path $release "addonstudio-core.exe"
if (-not (Test-Path $core)) { throw "No $core. Run scripts\build-sidecar.ps1 -Rid win-x64 before the tauri build." }

$version = (Get-Content (Join-Path $root "app/package.json") -Raw | ConvertFrom-Json).version
$out = Join-Path $root $OutDir
$folder = Join-Path $out "AddonStudio"

if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
New-Item -ItemType Directory -Force $folder | Out-Null

Copy-Item $app (Join-Path $folder "AddonStudio.exe")
Copy-Item $core (Join-Path $folder "addonstudio-core.exe")

@"
Addon Studio $version (portable)

Keep AddonStudio.exe and addonstudio-core.exe in the same folder.
Settings and the recent projects list are stored in the "data" folder next to them, so the whole folder can move.
Delete this file to store settings in %APPDATA%\AddonStudio instead.
Needs the Microsoft Edge WebView2 Runtime, which Windows 10 and 11 normally already have.
"@ | Set-Content -Encoding utf8 (Join-Path $folder "portable.txt")

$zip = Join-Path $out "AddonStudio-$version-portable-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zip

Get-ChildItem $folder | Format-Table Name, Length
Write-Host "portable folder: $folder"
Write-Host "zip: $zip"
