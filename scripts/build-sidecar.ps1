# Publishes the core (core/src/AddonStudio.Host) as a self-contained single file and copies it to
# app/src-tauri/binaries/addonstudio-core-<target triple>[.exe], where Tauri's bundle.externalBin looks for it.
#
# usage: scripts/build-sidecar.ps1 [-Rid win-x64] [-- extra dotnet publish args...]
#   Rid: win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64. Default: this machine (from rustc -vV).
#
# The build uses its own temporary artifacts folder, so it never collides with other builds of the solution.
[CmdletBinding()]
param(
    [string]$Rid = "",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PublishArgs = @()
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "core/src/AddonStudio.Host/AddonStudio.Host.csproj"
$binaries = Join-Path $root "app/src-tauri/binaries"

$ridToTriple = @{
    "win-x64"     = "x86_64-pc-windows-msvc"
    "win-arm64"   = "aarch64-pc-windows-msvc"
    "osx-x64"     = "x86_64-apple-darwin"
    "osx-arm64"   = "aarch64-apple-darwin"
    "linux-x64"   = "x86_64-unknown-linux-gnu"
    "linux-arm64" = "aarch64-unknown-linux-gnu"
}

if ([string]::IsNullOrWhiteSpace($Rid)) {
    if (-not (Get-Command rustc -ErrorAction SilentlyContinue)) {
        throw "rustc not found: pass -Rid (e.g. win-x64) or install Rust"
    }
    $hostLine = (& rustc -vV) | Where-Object { $_ -like "host: *" } | Select-Object -First 1
    if (-not $hostLine) { throw "rustc -vV printed no host line" }
    $triple = $hostLine.Substring("host: ".Length).Trim()
    if ($triple -eq "x86_64-pc-windows-gnu") { $triple = "x86_64-pc-windows-msvc" }
    $match = $ridToTriple.GetEnumerator() | Where-Object { $_.Value -eq $triple } | Select-Object -First 1
    if (-not $match) { throw "no .NET rid known for host triple $triple, pass -Rid" }
    $Rid = $match.Key
}
else {
    if (-not $ridToTriple.ContainsKey($Rid)) {
        throw "unknown rid $Rid (win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64)"
    }
    $triple = $ridToTriple[$Rid]
}

$ext = if ($Rid.StartsWith("win-")) { ".exe" } else { "" }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("addonstudio-sidecar-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    Write-Host "publishing core for $Rid ($triple)"
    $out = Join-Path $work "out"
    & dotnet publish $project -c Release -r $Rid --self-contained `
        -p:PublishSingleFile=true `
        --artifacts-path (Join-Path $work "artifacts") `
        -o $out `
        @PublishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $built = Join-Path $out "addonstudio-core$ext"
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) {
        Get-ChildItem -LiteralPath $out -ErrorAction SilentlyContinue | Format-Table Name, Length | Out-String | Write-Host
        throw "publish did not produce $built"
    }

    New-Item -ItemType Directory -Force -Path $binaries | Out-Null
    $target = Join-Path $binaries "addonstudio-core-$triple$ext"
    Copy-Item -LiteralPath $built -Destination $target -Force

    # a single-file publish should leave nothing else next to the exe. If it does, the installer would miss it.
    $extra = Get-ChildItem -LiteralPath $out -Recurse -File |
        Where-Object { $_.Name -ne "addonstudio-core$ext" -and $_.Extension -notin @(".pdb", ".dbg") }
    if ($extra) {
        Write-Warning "the publish left files next to the exe that the app will not ship:"
        $extra | ForEach-Object { Write-Warning ("  " + $_.FullName.Substring($out.Length + 1)) }
    }

    $size = (Get-Item -LiteralPath $target).Length
    Write-Host "wrote $target ($size bytes)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
